using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nuraherbex.Api.Data;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Services;

/// <summary>Shiprocket courier status codes mapped to order fulfillment status (shared by the webhook and the polling worker).</summary>
public static class ShiprocketStatus
{
    public static readonly Dictionary<string, (string Status, string Text)> Map = new()
    {
        ["6"] = ("IN_TRANSIT", "Shipped & In Transit"),
        ["7"] = ("DELIVERED", "Delivered to Recipient"),
        ["8"] = ("CANCELLED", "Shipment Cancelled"),
        ["9"] = ("RTO", "RTO Initiated"),
        ["10"] = ("RTO", "RTO Delivered"),
        ["14"] = ("RTO", "RTO Acknowledged"),
        ["17"] = ("OUT_FOR_DELIVERY", "Out for Delivery"),
        ["18"] = ("IN_TRANSIT", "In Transit between Hubs"),
        ["38"] = ("IN_TRANSIT", "Reached Destination Hub"),
        ["51"] = ("PICKED_UP", "Handed over to Courier"),
        ["45"] = ("CANCELLED", "Cancelled Before Dispatch"),
        ["40"] = ("RTO", "RTO Out for Delivery Attempt"),
        ["41"] = ("RTO", "RTO Out for Delivery"),
        ["46"] = ("RTO", "RTO In Transit"),
        ["42"] = ("PICKED_UP", "Picked Up by Courier"),
    };

    /// <summary>Relative order of states, so a late or repeated poll can never move an order backwards.</summary>
    public static int Rank(string? status) => status switch
    {
        "SHIPMENT_CREATED" => 1, "AWB_ASSIGNED" => 2, "PICKED_UP" => 3, "IN_TRANSIT" => 4,
        "OUT_FOR_DELIVERY" => 5, "DELIVERED" or "RTO" or "CANCELLED" => 6, _ => 0,
    };
}

/// <summary>
/// Shiprocket webhooks are best-effort, so poll live tracking for active Shiprocket shipments in bounded batches
/// (least recently checked first) and apply any newer status plus the courier scan history.
/// </summary>
public sealed class ShiprocketTrackingWorker(IServiceScopeFactory scopes, ILogger<ShiprocketTrackingWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { log.LogError(ex, "Shiprocket tracking cycle failed"); }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NuraDbContext>();
        var shiprocket = scope.ServiceProvider.GetRequiredService<ShiprocketService>();
        if (!shiprocket.IsConfigured) return;
        await AssignPendingAwbsAsync(db, shiprocket, scope, ct);


        var ids = await db.Orders.AsNoTracking()
            .Where(o => o.Courier == ShippingGateway.Shiprocket && o.ShiprocketAwb != null && !o.ShiprocketAwb.StartsWith("SR-PENDING-")
                && o.FulfillmentStatus != "CANCELLED" && o.FulfillmentStatus != "DELIVERED" && o.FulfillmentStatus != "RTO")
            .OrderBy(o => o.LastTrackingCheckedAt).Select(o => o.Id).Take(50).ToListAsync(ct);

        foreach (var id in ids)
        {
            if (ct.IsCancellationRequested) return;
            db.ChangeTracker.Clear();
            var awb = await db.Orders.AsNoTracking().Where(o => o.Id == id).Select(o => o.ShiprocketAwb).SingleAsync(ct);
            var live = await shiprocket.FetchLiveTrackingAsync(awb);

            await using var lease = await scope.ServiceProvider.GetRequiredService<OrderOperationLock>().AcquireAsync(id);
            db.ChangeTracker.Clear();
            var order = await db.Orders.SingleAsync(o => o.Id == id, ct);
            order.LastTrackingCheckedAt = DateTime.UtcNow; // rotate failed lookups too so one bad AWB cannot starve the queue
            string? notify = null;
            if (live is { } td) notify = Apply(order, td);
            await db.SaveChangesAsync(ct);

            if (notify is not null) Notify(order, notify);
        }
    }

    /// <summary>Shipments created in Shiprocket whose courier/AWB could not be assigned at booking: keep retrying so they get an AWB and start tracking.</summary>
    private async Task AssignPendingAwbsAsync(NuraDbContext db, ShiprocketService shiprocket, IServiceScope scope, CancellationToken ct)
    {
        var ids = await db.Orders.AsNoTracking()
            .Where(o => o.Courier == ShippingGateway.Shiprocket && o.ShiprocketAwb != null && o.ShiprocketAwb.StartsWith("SR-PENDING-") && o.ShiprocketShipmentId != null
                && o.FulfillmentStatus != "CANCELLED" && o.FulfillmentStatus != "DELIVERED" && o.FulfillmentStatus != "RTO")
            .OrderBy(o => o.LastTrackingCheckedAt).Select(o => o.Id).Take(20).ToListAsync(ct);
        foreach (var id in ids)
        {
            if (ct.IsCancellationRequested) return;
            db.ChangeTracker.Clear();
            var o = await db.Orders.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.ShiprocketShipmentId, x.ShiprocketOrderId }).SingleAsync(ct);
            var res = await shiprocket.AssignPendingAsync(id, o.ShiprocketShipmentId!, o.ShiprocketOrderId);

            await using var lease = await scope.ServiceProvider.GetRequiredService<OrderOperationLock>().AcquireAsync(id);
            db.ChangeTracker.Clear();
            var order = await db.Orders.SingleAsync(x => x.Id == id, ct);
            order.LastTrackingCheckedAt = DateTime.UtcNow;
            if (res is not null && order.ShiprocketAwb!.StartsWith("SR-PENDING-"))
            {
                order.ShiprocketAwb = res.ShiprocketAwb;
                order.ShiprocketCourier = res.ShiprocketCourier;
                order.FulfillmentStatus = "AWB_ASSIGNED";
                order.DeliveryStatus = res.DeliveryStatus;
                order.DeliveryTrackingEvents = [.. order.DeliveryTrackingEvents, .. res.TrackingEvents];
                order.UpdatedAt = DateTime.UtcNow;
                log.LogInformation("[Shiprocket] Order {Order} got its AWB {Awb} on retry", id, res.ShiprocketAwb);
            }
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Applies tracking_data to the order; returns the notification kind when the status moved forward.</summary>
    private static string? Apply(Order order, JsonElement td)
    {
        var code = td.TryGetProperty("shipment_status", out var sc) ? sc.ToString() : "";
        if (!ShiprocketStatus.Map.TryGetValue(code, out var mapped)) return null;

        // Append only scans we have not recorded yet (activities arrive newest-first).
        var known = order.DeliveryTrackingEvents.Select(e => e.Status).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fresh = new List<TrackingEventDto>();
        if (td.TryGetProperty("shipment_track_activities", out var acts) && acts.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in acts.EnumerateArray().Reverse())
            {
                var text = a.TryGetProperty("activity", out var act) && act.ValueKind == JsonValueKind.String ? act.GetString() : null;
                if (string.IsNullOrWhiteSpace(text) || !known.Add(text)) continue;
                fresh.Add(new TrackingEventDto
                {
                    Status = text,
                    Location = a.TryGetProperty("location", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null,
                    Time = a.TryGetProperty("date", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? Mapping.TimelineStamp() : Mapping.TimelineStamp(),
                    Done = true,
                });
            }
        }
        if (fresh.Count > 0) order.DeliveryTrackingEvents = [.. order.DeliveryTrackingEvents, .. fresh];

        if (ShiprocketStatus.Rank(mapped.Status) <= ShiprocketStatus.Rank(order.FulfillmentStatus)) { if (fresh.Count > 0) order.UpdatedAt = DateTime.UtcNow; return null; }

        var previous = order.FulfillmentStatus;
        order.FulfillmentStatus = mapped.Status;
        order.DeliveryStatus = mapped.Text;
        order.UpdatedAt = DateTime.UtcNow;
        if (mapped.Status == "DELIVERED" && order.PaymentMethod == "COD") order.PaymentStatus = "COD_COLLECTED";
        return EmailService.TransitionEmail(mapped.Status, previous);
    }

    private void Notify(Order order, string notify)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopes.CreateScope();
                var wa = scope.ServiceProvider.GetRequiredService<WhatsAppService>();
                var mail = scope.ServiceProvider.GetRequiredService<EmailService>();
                try { await mail.SendStatusUpdateAsync(order, notify); }
                catch (Exception ex) { log.LogWarning(ex, "Order {Order} {Notify} email failed", order.Id, notify); }
                if (notify == "DELIVERED") await wa.NotifyDeliveredAsync(order);
                else if (notify == "SHIPPED") await wa.NotifyShippedAsync(order);
            }
            catch (Exception ex) { log.LogWarning(ex, "Shiprocket tracking notification failed"); }
        });
    }
}
