using Microsoft.AspNetCore.Authorization;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nuraherbex.Api.Data;
using Nuraherbex.Api.Options;
using Nuraherbex.Api.Services;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Controllers;

/// <summary>
/// WhatsApp Cloud API webhook.
///
/// Meta setup:  App Dashboard â†’ WhatsApp â†’ Configuration â†’ Webhook
///   Callback URL : https://&lt;your-api-host&gt;/api/webhooks/whatsapp
///   Verify token : the value of WhatsApp:VerifyToken
///   Subscribe to : messages
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/webhooks/whatsapp")]
public class WhatsAppWebhookController(WhatsAppService whatsapp, IWebHostEnvironment env, ILogger<WhatsAppWebhookController> log) : ControllerBase
{
    /// <summary>
    /// Verification handshake. Meta sends hub.mode=subscribe, hub.verify_token and hub.challenge.
    /// Reply 200 with the raw challenge when the token matches, otherwise 403.
    /// </summary>
    [HttpGet]
    public IActionResult Verify(
        [FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.verify_token")] string? verifyToken,
        [FromQuery(Name = "hub.challenge")] string? challenge)
    {
        if (whatsapp.TryVerifyHandshake(mode, verifyToken, out var misconfigured) && challenge is not null)
        {
            log.LogInformation("[WhatsApp] Webhook verified");
            return Content(challenge, "text/plain", Encoding.UTF8);
        }

        if (misconfigured) log.LogError("[WhatsApp] WhatsApp:VerifyToken is not configured â€” cannot verify webhook");
        else log.LogWarning("[WhatsApp] Webhook verification rejected (mode={Mode})", mode);
        // Plain 403 (not Forbid(), which goes through the JWT handler and looks like an auth failure).
        return StatusCode(403, misconfigured
            ? "WhatsApp:VerifyToken is not configured on the server."
            : "Verification failed: send hub.mode=subscribe, hub.verify_token=<WhatsApp:VerifyToken> and hub.challenge.");
    }

    /// <summary>Event delivery. The signature is validated against the exact raw bytes Meta signed.</summary>
    [HttpPost]
    public async Task<IActionResult> Receive()
    {
        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms);
        var raw = ms.ToArray();

        if (whatsapp.HasAppSecret)
        {
            if (!whatsapp.IsSignatureValid(raw, Request.Headers["X-Hub-Signature-256"].FirstOrDefault()))
            {
                log.LogWarning("[WhatsApp] Rejected event with invalid X-Hub-Signature-256");
                return Unauthorized();
            }
        }
        else if (!env.IsDevelopment())
        {
            log.LogError("[WhatsApp] WhatsApp:AppSecret is not configured â€” refusing unsigned events outside Development");
            return StatusCode(503);
        }
        else log.LogWarning("[WhatsApp] AppSecret not set â€” accepting unsigned event (Development only)");

        try { await whatsapp.ProcessWebhookAsync(Encoding.UTF8.GetString(raw)); }
        catch (Exception ex)
        {
            // Always ack with 200 for a validated payload â€” Meta retries aggressively on non-2xx.
            log.LogError(ex, "[WhatsApp] Failed to process event");
        }
        return Ok();
    }
}

/// <summary>Shiprocket tracking webhook: maps courier status codes to order fulfillment status.</summary>
[ApiController]
[AllowAnonymous]
[Route("api/webhooks/shiprocket")]
public class ShiprocketWebhookController(
    NuraDbContext db,
    IOptions<ShiprocketOptions> options,
    IServiceScopeFactory scopes,
    ILogger<ShiprocketWebhookController> log) : ControllerBase
{
    private static readonly Dictionary<string, (string Status, string Text)> StatusMap = new()
    {
        ["6"] = ("IN_TRANSIT", "Shipped & In Transit"),
        ["7"] = ("DELIVERED", "Delivered to Recipient"),
        ["8"] = ("CANCELLED", "Shipment Cancelled"),
        ["9"] = ("RTO", "RTO Initiated"),
        ["17"] = ("OUT_FOR_DELIVERY", "Out for Delivery"),
        ["18"] = ("IN_TRANSIT", "In Transit between Hubs"),
        ["19"] = ("OUT_FOR_DELIVERY", "Out for Delivery"),
        ["42"] = ("PICKED_UP", "Picked Up by Courier"),
    };

    [HttpPost("webhook")]
    public async Task<IActionResult> Receive([FromBody] System.Text.Json.JsonElement payload)
    {
        var expected = options.Value.WebhookToken;
        var supplied = Request.Headers["x-api-key"].FirstOrDefault() ?? Request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrEmpty(expected) && supplied != expected)
            return Unauthorized(new ApiResult { Success = false, Message = "Unauthorized webhook request" });

        string? Str(string name) => payload.TryGetProperty(name, out var v) ? v.ToString() : null;
        var awb = Str("awb") ?? Str("awb_code");
        var orderId = Str("order_id");
        var code = Str("current_status_id") ?? Str("shipment_status_id") ?? "";

        if (!StatusMap.TryGetValue(code, out var mapped) || (orderId is null && awb is null))
            return Ok(new ApiResult { Success = true, Message = "Webhook processed successfully" });

        var order = await db.Orders.FirstOrDefaultAsync(o => (orderId != null && o.Id == orderId) || (awb != null && o.ShiprocketAwb == awb));
        if (order is null) return Ok(new ApiResult { Success = true, Message = "Webhook processed successfully" });

        string? location = null;
        if (payload.TryGetProperty("scans", out var scans) && scans.ValueKind == System.Text.Json.JsonValueKind.Array && scans.GetArrayLength() > 0
            && scans[0].TryGetProperty("location", out var loc)) location = loc.GetString();

        var previous = order.FulfillmentStatus;
        order.FulfillmentStatus = mapped.Status;
        order.DeliveryStatus = mapped.Text;
        order.UpdatedAt = DateTime.UtcNow;
        order.DeliveryTrackingEvents = [.. order.DeliveryTrackingEvents, new TrackingEventDto
            { Status = mapped.Text, Location = location ?? "Express Sorting Facility", Time = Mapping.TimelineStamp(), Done = true, Active = true }];
        if (mapped.Status == "DELIVERED" && order.PaymentMethod == "COD") order.PaymentStatus = "COD_COLLECTED";
        await db.SaveChangesAsync();
        log.LogInformation("[Shiprocket] Order {Order} â†’ {Status}", order.Id, mapped.Status);

        // Notify only on a genuine transition so repeated courier scans never spam the customer.
        var notifyDelivered = mapped.Status == "DELIVERED" && previous != "DELIVERED";
        var notifyShipped = mapped.Status is "PICKED_UP" or "IN_TRANSIT" && previous is "PENDING" or "SHIPMENT_CREATED" or "AWB_ASSIGNED";
        if (!notifyDelivered && !notifyShipped) return Ok(new ApiResult { Success = true, Message = "Webhook processed successfully" });

        var snapshot = order;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopes.CreateScope();
                var wa = scope.ServiceProvider.GetRequiredService<WhatsAppService>();
                var mail = scope.ServiceProvider.GetRequiredService<EmailService>();
                if (notifyDelivered) { await mail.SendOrderDeliveredAsync(snapshot); await wa.NotifyDeliveredAsync(snapshot); }
                else await wa.NotifyShippedAsync(snapshot);
            }
            catch (Exception ex) { log.LogWarning(ex, "Shiprocket webhook notification failed"); }
        });

        return Ok(new ApiResult { Success = true, Message = "Webhook processed successfully" });
    }
}

/// <summary>
/// Shadowfax push-callback receiver (Client Portal → Webhook tab → URL <c>{ApiPublicUrl}/api/webhooks/shadowfax</c>).
/// Maps Shadowfax <c>status_id</c> values to order fulfillment status. Events with no fulfillment meaning (not contactable, on hold, NDR…) are
/// recorded on the tracking timeline only.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/webhooks/shadowfax")]
public class ShadowfaxWebhookController(
    NuraDbContext db,
    IOptions<ShadowfaxOptions> options,
    IServiceScopeFactory scopes,
    ILogger<ShadowfaxWebhookController> log) : ControllerBase
{
    private static readonly Dictionary<string, (string? Status, string Text)> EventMap = new()
    {
        ["assigned_for_pickup"] = (null, "Pickup Assigned"),
        ["assigned_for_seller_pickup"] = (null, "Pickup Assigned"),
        ["ofp"] = (null, "Courier Out for Pickup"),
        ["picked"] = ("PICKED_UP", "Picked Up by Courier"),
        ["qc_failed"] = (null, "Pickup Quality Check Failed"),
        ["recd_at_rev_hub"] = ("PICKED_UP", "Received at Pickup Hub"),
        ["received_from_client_warehouse"] = ("PICKED_UP", "Received from Warehouse"),
        ["item_manifested"] = ("IN_TRANSIT", "Packed for Transit"),
        ["bag_in_transit"] = ("IN_TRANSIT", "In Transit between Hubs"),
        ["bag_received_at_via"] = ("IN_TRANSIT", "In Transit between Hubs"),
        ["bag_received"] = ("IN_TRANSIT", "Arrived at Destination Facility"),
        ["recd_at_fwd_dc"] = ("IN_TRANSIT", "Arrived at Delivery City"),
        ["recd_at_fwd_hub"] = ("IN_TRANSIT", "Arrived at Delivery Hub"),
        ["assigned_for_delivery"] = ("IN_TRANSIT", "Assigned for Delivery"),
        ["ofd"] = ("OUT_FOR_DELIVERY", "Out for Delivery"),
        ["delivered"] = ("DELIVERED", "Delivered to Recipient"),
        ["partially_delivered"] = (null, "Partially Delivered"),
        ["rts"] = ("RTO", "Return Initiated"),
        ["rts_in_process"] = ("RTO", "Return in Progress"),
        ["rts_ofd"] = ("RTO", "Return Out for Delivery"),
        ["rts_d"] = ("RTO", "Returned to Seller"),
        ["rts_nd"] = ("RTO", "Return Delivery Attempt Failed"),
        ["rto"] = ("RTO", "Return Initiated"),
        ["rto_in_process"] = ("RTO", "Return in Progress"),
        ["rto_nd"] = ("RTO", "Return Delivery Attempt Failed"),
        ["rto_d"] = ("RTO", "Returned to Origin"),
        ["cancelled"] = ("CANCELLED", "Shipment Cancelled"),
        ["cancelled_by_client"] = ("CANCELLED", "Shipment Cancelled"),
        ["cancelled_by_seller"] = ("CANCELLED", "Shipment Cancelled"),
        ["cancelled_by_customer"] = ("CANCELLED", "Shipment Cancelled"),
        ["nc"] = (null, "Delivery Attempted: Customer Not Reachable"),
        ["na"] = (null, "Delivery Not Attempted"),
        ["cid"] = (null, "Delivery Rescheduled at Customer Request"),
        ["on_hold"] = (null, "Shipment On Hold"),
        ["reopen_ndr"] = (null, "Delivery Will Be Re-attempted"),
        ["lost"] = (null, "Shipment Reported Lost"),
    };

    // Terminal states are never overwritten by a late or out-of-order scan.
    private static readonly HashSet<string> Terminal = ["DELIVERED", "CANCELLED", "RTO"];

    [HttpPost]
    public async Task<IActionResult> Receive([FromBody] System.Text.Json.JsonElement payload)
    {
        var expected = options.Value.WebhookToken;
        if (!string.IsNullOrEmpty(expected))
        {
            var supplied = Request.Headers.Authorization.FirstOrDefault()?.Trim();
            if (supplied != expected && supplied != $"Token {expected}")
                return Unauthorized(new ApiResult { Success = false, Message = "Unauthorized webhook request" });
        }

        string? Str(string name) => payload.TryGetProperty(name, out var v) && v.ValueKind != System.Text.Json.JsonValueKind.Null ? v.ToString() : null;
        var awb = Str("awb_number");
        var orderId = Str("client_order_id") ?? Str("order_id");
        var ev = (Str("status_id") ?? Str("event") ?? "").ToLowerInvariant();
        const string ok = "Webhook processed successfully";

        if (awb is null && orderId is null)
            return Ok(new ApiResult { Success = true, Message = ok });

        var knownEvent = EventMap.TryGetValue(ev, out var mapped);
        if (!knownEvent)
        {
            var statusText = Str("status");
            if (string.IsNullOrWhiteSpace(statusText))
                return Ok(new ApiResult { Success = true, Message = ok });
            mapped = (null, statusText);
        }

        var order = await db.Orders.FirstOrDefaultAsync(o =>
            (orderId != null && o.Id == orderId) || (awb != null && o.ShiprocketAwb == awb));
        if (order is null) return Ok(new ApiResult { Success = true, Message = ok });

        var previous = order.FulfillmentStatus;
        var location = Str("current_location");
        var detail = Str("remarks");

        var eventText = knownEvent ? mapped.Text : Str("status") ?? mapped.Text;
        order.DeliveryStatus = eventText;
        order.UpdatedAt = DateTime.UtcNow;
        if (mapped.Status is not null && !Terminal.Contains(previous)) order.FulfillmentStatus = mapped.Status;
        order.DeliveryTrackingEvents = [.. order.DeliveryTrackingEvents, new TrackingEventDto
            { Status = eventText, Location = location ?? detail ?? "Shadowfax Facility", Time = Mapping.TimelineStamp(), Done = true, Active = true }];
        if (mapped.Status == "DELIVERED" && order.PaymentMethod == "COD") order.PaymentStatus = "COD_COLLECTED";
        await db.SaveChangesAsync();
        log.LogInformation("[Shadowfax] Order {Order} {Event} → {Status}", order.Id, ev, order.FulfillmentStatus);

        // Notify only on a genuine transition so repeated scans never spam the customer.
        var notifyDelivered = mapped.Status == "DELIVERED" && previous != "DELIVERED";
        var notifyShipped = mapped.Status is "PICKED_UP" or "IN_TRANSIT" && previous is "PENDING" or "SHIPMENT_CREATED" or "AWB_ASSIGNED";
        if (!notifyDelivered && !notifyShipped) return Ok(new ApiResult { Success = true, Message = ok });

        var snapshot = order;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopes.CreateScope();
                var wa = scope.ServiceProvider.GetRequiredService<WhatsAppService>();
                var mail = scope.ServiceProvider.GetRequiredService<EmailService>();
                if (notifyDelivered) { await mail.SendOrderDeliveredAsync(snapshot); await wa.NotifyDeliveredAsync(snapshot); }
                else await wa.NotifyShippedAsync(snapshot);
            }
            catch (Exception ex) { log.LogWarning(ex, "Shadowfax webhook notification failed"); }
        });

        return Ok(new ApiResult { Success = true, Message = ok });
    }
}
