using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nuraherbex.Api.Data;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Services;

public sealed class ShadowfaxTrackingProcessor(NuraDbContext db, OrderOperationLock operationLock, IServiceScopeFactory scopes, ILogger<ShadowfaxTrackingProcessor> log)
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

    public async Task ProcessAsync(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Callback must be an object.");
        string? Str(string name) => payload.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : null;
        var awb = Str("awb_number");
        var reference = Str("client_order_id");
        if (string.IsNullOrWhiteSpace(awb)) throw new InvalidOperationException("awb_number is required.");
        var id = await db.Orders.AsNoTracking().Where(o => o.Courier == "Shadowfax" && o.ShiprocketAwb == awb).Select(o => o.Id).FirstOrDefaultAsync();
        if (id is null) return; // Unknown AWBs cannot mutate an order by guessing its reference.
        await using var lease = await operationLock.AcquireAsync(id);
        db.ChangeTracker.Clear();
        var order = await db.Orders.SingleAsync(o => o.Id == id);
        if (order.Courier != "Shadowfax" || order.ShiprocketAwb != awb) return;
        if (reference is not null && reference != (order.ShipmentClientOrderId ?? ShippingGateway.ShadowfaxClientOrderId(order))) return;
        var code = (Str("status_id") ?? "").ToLowerInvariant();
        if (!EventMap.TryGetValue(code, out var mapped)) return;
        var rawTime = Str("last_updated") ?? Str("created");
        var time = ParseTime(rawTime);
        var location = Str("current_location") ?? Str("location");
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{awb}|{code}|{rawTime}|{location}|{Str("remarks")}")));
        if (order.DeliveryTrackingEvents.Any(e => e.EventKey == key)) return;
        var stale = time.HasValue && order.LastShippingEventAt.HasValue && time <= order.LastShippingEventAt;
        var terminal = order.FulfillmentStatus is "DELIVERED" or "CANCELLED" or "RTO";
        var regression = mapped.Status is not null && Rank(mapped.Status) < Rank(order.FulfillmentStatus);
        var previous = order.FulfillmentStatus;
        var apply = !stale && !terminal && !regression;
        order.DeliveryTrackingEvents = [.. order.DeliveryTrackingEvents, new TrackingEventDto
        {
            EventKey = key, Status = mapped.Text, Location = location,
            Time = time?.ToString("O") ?? Mapping.TimelineStamp(), Done = true, Active = apply
        }];
        if (apply)
        {
            foreach (var item in order.DeliveryTrackingEvents.Take(order.DeliveryTrackingEvents.Count - 1)) item.Active = false;
            if (mapped.Status is not null) order.FulfillmentStatus = mapped.Status;
            order.DeliveryStatus = mapped.Text;
            if (time.HasValue) order.LastShippingEventAt = time;
            if (mapped.Status == "PICKED_UP") order.PickedUpAt ??= time ?? DateTime.UtcNow;
            if (mapped.Status == "DELIVERED")
            {
                order.DeliveredAt ??= time ?? DateTime.UtcNow;
                if (order.PaymentMethod == "COD") order.PaymentStatus = "COD_COLLECTED";
            }
            if (mapped.Status == "CANCELLED") order.ShippingLabelUrl = null;
        }
        order.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        var notify = apply ? EmailService.TransitionEmail(mapped.Status, previous) : null;
        if (notify is not null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<EmailService>().SendStatusUpdateAsync(order, notify);
                    var whatsapp = scope.ServiceProvider.GetRequiredService<WhatsAppService>();
                    if (notify == "SHIPPED") await whatsapp.NotifyShippedAsync(order);
                    if (notify == "DELIVERED") await whatsapp.NotifyDeliveredAsync(order);
                }
                catch (Exception ex) { log.LogWarning(ex, "Shipment notification failed for {Order}", id); }
            });
        }
    }

    public static DateTime? ParseTime(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // Callback placeholders are IST unless an explicit offset is supplied.
        if (System.Text.RegularExpressions.Regex.IsMatch(raw, @"(?:Z|[+-]\d{2}:?\d{2})$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var offset) ? offset.UtcDateTime : null;
        return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            ? new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeSpan.FromHours(5.5)).UtcDateTime : null;
    }

    private static int Rank(string status) => status switch
    {
        "PENDING" => 0, "SHIPMENT_CREATED" or "AWB_ASSIGNED" => 1, "PICKED_UP" => 2,
        "IN_TRANSIT" => 3, "OUT_FOR_DELIVERY" => 4, "DELIVERED" or "CANCELLED" or "RTO" => 5, _ => 0
    };
}