using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Nuraherbex.Api.Data;

namespace Nuraherbex.Api.Services;

// Callbacks are not retried by Shadowfax. Reconcile the least recently checked active
// shipments in bounded batches, including a callback that arrived before AWB persistence.
public sealed class ShadowfaxReconciliationWorker(IServiceScopeFactory scopes, ILogger<ShadowfaxReconciliationWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NuraDbContext>();
                var courier = scope.ServiceProvider.GetRequiredService<ShadowfaxService>();
                if (!courier.IsConfigured) continue;
                var processor = scope.ServiceProvider.GetRequiredService<ShadowfaxTrackingProcessor>();
                var ids = await db.Orders.AsNoTracking().Where(o => o.Courier == "Shadowfax" && o.ShiprocketAwb != null
                    && o.FulfillmentStatus != "CANCELLED" && o.FulfillmentStatus != "DELIVERED" && o.FulfillmentStatus != "RTO")
                    .OrderBy(o => o.LastTrackingCheckedAt).Select(o => o.Id).Take(50).ToListAsync(stoppingToken);
                foreach (var id in ids)
                {
                    if (stoppingToken.IsCancellationRequested) return;
                    db.ChangeTracker.Clear();
                    var order = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == id, stoppingToken);
                    try
                    {
                        var raw = await courier.TrackRawAsync(order.ShiprocketAwb!);
                        var scans = (raw["tracking_details"] as JsonArray)?.OfType<JsonObject>().OrderBy(s => s["created"]?.ToString()).ToList() ?? [];
                        foreach (var scan in scans)
                        {
                            scan["awb_number"] = order.ShiprocketAwb;
                            await processor.ProcessAsync(JsonSerializer.SerializeToElement(scan));
                        }
                    }
                    catch (Exception ex) { log.LogWarning(ex, "Shadowfax reconciliation failed for {Order}", id); }
                    // Rotate failed lookups too, so one unavailable AWB cannot starve the queue.
                    await using var lease = await scope.ServiceProvider.GetRequiredService<OrderOperationLock>().AcquireAsync(id);
                    db.ChangeTracker.Clear();
                    var current = await db.Orders.SingleAsync(o => o.Id == id, stoppingToken);
                    current.LastTrackingCheckedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { log.LogError(ex, "Shadowfax reconciliation cycle failed"); }
        }
    }
}