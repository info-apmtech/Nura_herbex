using System.Text.Json;
using Microsoft.Extensions.Options;
using Nuraherbex.Api.Data;
using Nuraherbex.Shared.Models;
using Nuraherbex.Api.Options;

namespace Nuraherbex.Api.Services;

/// <summary>
/// Routes shipping work to the right courier platform. New orders default to Shadowfax and can be set with <c>Shipping:Provider</c>;
/// tracking and cancellation follow the courier recorded on the order (null = Shiprocket, for orders booked before multi-courier).
/// </summary>
public class ShippingGateway(ShiprocketService shiprocket, ShadowfaxService shadowfax, IOptions<ShippingOptions> options)
{
    public const string Shiprocket = "Shiprocket";
    public const string Shadowfax = "Shadowfax";

    public string ActiveProvider => string.Equals(options.Value.Provider, Shiprocket, StringComparison.OrdinalIgnoreCase) ? Shiprocket : Shadowfax;

    public static bool IsShadowfax(string? courier) => Shadowfax.Equals(courier, StringComparison.OrdinalIgnoreCase);

    public async Task<(ShiprocketResult Result, string Courier)> CreateOrderAsync(Order order, ParcelSpecs parcel) =>
        ActiveProvider == Shadowfax
            ? (await shadowfax.CreateOrderAsync(order, parcel), Shadowfax)
            : (await shiprocket.CreateOrderAsync(order, parcel), Shiprocket);

    public Task<JsonElement?> FetchLiveTrackingAsync(string? courier, string? awb) =>
        IsShadowfax(courier) ? shadowfax.FetchLiveTrackingAsync(awb) : shiprocket.FetchLiveTrackingAsync(awb);

    public async Task<string> CancelAsync(Order order, string reason)
    {
        if (string.IsNullOrWhiteSpace(order.ShiprocketAwb) || order.ShiprocketAwb.StartsWith("SR-PENDING-"))
            throw new InvalidOperationException("This order has no shipment to cancel.");
        if (!IsShadowfax(order.Courier))
            throw new InvalidOperationException("Cancel Shiprocket shipments from the Shiprocket dashboard.");
        return await shadowfax.CancelAsync(order.ShiprocketAwb, reason);
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

    public Task<bool?> IsServiceableAsync(string pincode) =>
        ActiveProvider == Shadowfax ? shadowfax.IsServiceableAsync(pincode) : Task.FromResult<bool?>(null);
}
