using System.Text.Json;
using Nuraherbex.Api.Data;
using Nuraherbex.Shared.Models;
using Nuraherbex.Api.Options;

namespace Nuraherbex.Api.Services;

/// <summary>
/// Routes new confirmed orders through the courier the admin enabled (Shiprocket or Shadowfax); orders already booked stay with their courier.
/// Tracking and cancellation follow the courier recorded on each order.
/// </summary>
public class ShippingGateway(ShiprocketService shiprocket, ShadowfaxService shadowfax, ShippingSettingsStore store,
    Microsoft.Extensions.Options.IOptions<ShippingOptions> shippingOptions, Microsoft.Extensions.Options.IOptions<ShadowfaxOptions> shadowfaxOptions)
{
    public const string Shiprocket = "Shiprocket";
    public const string Shadowfax = "Shadowfax";

    public string ActiveProvider => store.ActiveProvider(shippingOptions.Value);

    public bool AutoBook => store.Apply(shadowfaxOptions.Value).AutoBook;

    public bool IsConfigured(string provider) => provider == Shiprocket ? shiprocket.IsConfigured : shadowfax.IsConfigured;

    public string ProviderFor(Order order)
    {
        var realAwb = !string.IsNullOrWhiteSpace(order.ShiprocketAwb) && !order.ShiprocketAwb.StartsWith("SR-PENDING-");
        var hasBooking = realAwb
            || !string.IsNullOrWhiteSpace(order.ShiprocketOrderId)
            || !string.IsNullOrWhiteSpace(order.ShiprocketShipmentId)
            || !string.IsNullOrWhiteSpace(order.ShiprocketCourier);
        if (!hasBooking) return ActiveProvider; // nothing booked yet: follow the courier the admin has enabled now
        if (IsShadowfax(order.Courier)) return Shadowfax;
        return Shiprocket;
    }

    public static bool IsShadowfax(string? courier) => Shadowfax.Equals(courier, StringComparison.OrdinalIgnoreCase);

    /// <summary>Stable Shadowfax idempotency key for the current shipment attempt.</summary>
    public static string ShadowfaxClientOrderId(Order order)
    {
        var cancelledShipments = order.DeliveryTrackingEvents?.Count(e =>
            e.Status.Contains("Shipment Cancelled", StringComparison.OrdinalIgnoreCase)) ?? 0;
        return cancelledShipments == 0 ? order.Id : $"{order.Id}-R{cancelledShipments}";
    }

    /// <summary>Maps a Shadowfax retry reference such as NH-123456-R2 back to the store order id.</summary>
    public static string? ShadowfaxBaseOrderId(string? clientOrderId)
    {
        if (string.IsNullOrWhiteSpace(clientOrderId)) return null;
        var separator = clientOrderId.LastIndexOf("-R", StringComparison.OrdinalIgnoreCase);
        return separator > 0 && int.TryParse(clientOrderId[(separator + 2)..], out _)
            ? clientOrderId[..separator]
            : clientOrderId;
    }

    public async Task<(ShiprocketResult Result, string Courier)> CreateOrderAsync(Order order, ParcelSpecs parcel, string? clientOrderId = null)
    {
        if (ProviderFor(order) == Shadowfax) return (await shadowfax.CreateOrderAsync(order, parcel, clientOrderId), Shadowfax);

        // The shipment already exists in Shiprocket but has no courier/AWB yet: finish that one instead of creating a duplicate order.
        if (!string.IsNullOrWhiteSpace(order.ShiprocketShipmentId) && order.ShiprocketAwb is { } pending && pending.StartsWith("SR-PENDING-"))
        {
            var assigned = await shiprocket.AssignPendingAsync(order.Id, order.ShiprocketShipmentId, order.ShiprocketOrderId);
            return (assigned ?? new ShiprocketResult
            {
                Success = true, ShiprocketOrderId = order.ShiprocketOrderId ?? "", ShiprocketShipmentId = order.ShiprocketShipmentId, ShiprocketAwb = pending,
                ShiprocketCourier = order.ShiprocketCourier ?? "Shiprocket Express", DeliveryStatus = "Shipment Created",
            }, Shiprocket);
        }
        return (await shiprocket.CreateOrderAsync(order, parcel), Shiprocket);
    }

    public Task<string> GenerateLabelAsync(string awb) => shadowfax.GenerateLabelAsync(awb);

    public Task<JsonElement?> FetchLiveTrackingAsync(string? courier, string? awb) =>
        IsShadowfax(courier) ? shadowfax.FetchLiveTrackingAsync(awb) : shiprocket.FetchLiveTrackingAsync(awb);

    public async Task<string> CancelAsync(Order order, string reason)
    {
        if (IsShadowfax(order.Courier))
        {
            if (string.IsNullOrWhiteSpace(order.ShiprocketAwb) || order.ShiprocketAwb.StartsWith("SR-PENDING-"))
                throw new InvalidOperationException("This order has no shipment to cancel.");
            return await shadowfax.CancelAsync(order.ShiprocketAwb, reason);
        }
        return await shiprocket.CancelAsync(order.ShiprocketAwb, order.ShiprocketOrderId);
    }

    /// <summary>Public tracking page for a shipment (used when the courier's own link is not returned).</summary>
    public static string? TrackUrl(string? courier, string? awb, string? platformOrderId)
    {
        var real = !string.IsNullOrWhiteSpace(awb) && !awb.StartsWith("SR-PENDING-");
        if (IsShadowfax(courier)) return null; // Shadowfax returns its own customer_track_url with live tracking
        if (real) return $"https://shiprocket.co/tracking/{awb}";
        return string.IsNullOrWhiteSpace(platformOrderId) ? null : $"https://shiprocket.co/tracking?order_id={platformOrderId}";
    }

    /// <summary>Fills the storefront tracking fields (live scans + public track link) for whichever courier booked the order.</summary>
    public async Task EnrichTrackingAsync(OrderDto dto)
    {
        dto.ShiprocketTrackUrl = TrackUrl(dto.Courier, dto.ShiprocketAwb, dto.ShiprocketOrderId);
        if (string.IsNullOrWhiteSpace(dto.ShiprocketAwb) || dto.ShiprocketAwb.StartsWith("SR-PENDING-")) return;

        var live = await FetchLiveTrackingAsync(dto.Courier, dto.ShiprocketAwb);
        if (live is not { } td) return;
        dto.ShiprocketTracking = td;
        if (td.TryGetProperty("track_url", out var u) && u.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(u.GetString()))
            dto.ShiprocketTrackUrl = u.GetString();
    }

    public Task<bool?> IsServiceableAsync(string pincode) => ActiveProvider == Shadowfax ? shadowfax.IsServiceableAsync(pincode) : Task.FromResult<bool?>(null); // unknown, never blocks checkout
}
