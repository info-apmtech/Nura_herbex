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
        if (string.IsNullOrWhiteSpace(expected)) return StatusCode(503);
        if (supplied != expected)
            return Unauthorized(new ApiResult { Success = false, Message = "Unauthorized webhook request" });

        string? Str(string name) => payload.TryGetProperty(name, out var v) ? v.ToString() : null;
        var awb = Str("awb") ?? Str("awb_code");
        var orderId = Str("order_id");
        var code = Str("current_status_id") ?? Str("shipment_status_id") ?? "";

        if (!StatusMap.TryGetValue(code, out var mapped) || (orderId is null && awb is null))
            return Ok(new ApiResult { Success = true, Message = "Webhook processed successfully" });

        var order = await db.Orders.FirstOrDefaultAsync(o => (orderId != null && o.Id == orderId) || (awb != null && o.ShiprocketAwb == awb));
        if (order is null || ShippingGateway.IsShadowfax(order.Courier)) return Ok(new ApiResult { Success = true, Message = "Webhook processed successfully" });

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

        // Email every genuine status transition; repeated scans of the same status never spam the customer.
        var notify = EmailService.TransitionEmail(mapped.Status, previous);
        if (notify is null) return Ok(new ApiResult { Success = true, Message = "Webhook processed successfully" });

        var snapshot = order;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopes.CreateScope();
                var wa = scope.ServiceProvider.GetRequiredService<WhatsAppService>();
                var mail = scope.ServiceProvider.GetRequiredService<EmailService>();
                try { await mail.SendStatusUpdateAsync(snapshot, notify); }
                catch (Exception ex) { log.LogWarning(ex, "Order {Order} {Notify} email failed", snapshot.Id, notify); }
                if (notify == "DELIVERED") await wa.NotifyDeliveredAsync(snapshot);
                else if (notify == "SHIPPED") await wa.NotifyShippedAsync(snapshot);
            }
            catch (Exception ex) { log.LogWarning(ex, "Shiprocket webhook notification failed"); }
        });

        return Ok(new ApiResult { Success = true, Message = "Webhook processed successfully" });
    }
}

/// <summary>Register the exact callback template and Authorization token with Shadowfax.</summary>
[ApiController]
[AllowAnonymous]
[Route("api/webhooks/shadowfax")]
public class ShadowfaxWebhookController(IOptions<ShadowfaxOptions> options, ShadowfaxTrackingProcessor processor) : ControllerBase
{
    [HttpPost]
    [RequestSizeLimit(65536)]
    public async Task<IActionResult> Receive([FromBody] System.Text.Json.JsonElement payload)
    {
        var expected = options.Value.WebhookToken;
        if (string.IsNullOrWhiteSpace(expected)) return StatusCode(503);
        var supplied = Request.Headers.Authorization.ToString();
        if (supplied.StartsWith("Token ", StringComparison.Ordinal)) supplied = supplied[6..];
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(supplied)),
                System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(expected)))) return Unauthorized();
        try { await processor.ProcessAsync(payload); }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResult { Success = false, Message = ex.Message }); }
        return Ok(new ApiResult { Success = true });
    }
}