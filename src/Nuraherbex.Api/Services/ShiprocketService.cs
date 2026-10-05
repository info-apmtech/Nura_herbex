using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Nuraherbex.Api.Data;
using Nuraherbex.Api.Options;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Services;

public class ShiprocketResult
{
    public bool Success { get; set; }
    public bool Simulated { get; set; }
    public string ShiprocketOrderId { get; set; } = "";
    public string ShiprocketShipmentId { get; set; } = "";
    public string ShiprocketAwb { get; set; } = "";
    public string ShiprocketCourier { get; set; } = "";
    public string DeliveryStatus { get; set; } = "";
    public List<TrackingEventDto> TrackingEvents { get; set; } = new();
}

public class ShiprocketService(HttpClient http, IOptions<ShiprocketOptions> options, ILogger<ShiprocketService> log)
{
    private static string? _token;
    private static DateTime _tokenExpires = DateTime.MinValue;
    private static readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ShiprocketOptions _o = options.Value;

    private async Task<string?> GetTokenAsync()
    {
        if (!string.IsNullOrWhiteSpace(_o.Token)) return _o.Token;
        if (!_o.IsConfigured) return null;
        if (_token is not null && DateTime.UtcNow < _tokenExpires) return _token;

        await _lock.WaitAsync();
        try
        {
            if (_token is not null && DateTime.UtcNow < _tokenExpires) return _token;
            var res = await http.PostAsJsonAsync($"{_o.ApiUrl}/auth/login", new { email = _o.Email, password = _o.Password });
            if (!res.IsSuccessStatusCode) throw new InvalidOperationException("Failed to authenticate with Shiprocket API");
            var json = JsonNode.Parse(await res.Content.ReadAsStringAsync());
            _token = json?["token"]?.GetValue<string>();
            _tokenExpires = DateTime.UtcNow.AddDays(9); // tokens last 10 days
            return _token;
        }
        finally { _lock.Release(); }
    }

    public async Task<ShiprocketResult> CreateOrderAsync(Order order, ParcelSpecs parcel)
    {
        var isCod = order.PaymentMethod == "COD";
        var token = await GetTokenAsync();

        if (token is null)
        {
            log.LogWarning("[Shiprocket] Credentials not configured — simulating shipment for {Order}", order.Id);
            var rnd = Random.Shared;
            return new ShiprocketResult
            {
                Success = true, Simulated = true,
                ShiprocketOrderId = rnd.Next(10_000_000, 99_999_999).ToString(),
                ShiprocketShipmentId = rnd.Next(10_000_000, 99_999_999).ToString(),
                ShiprocketAwb = $"SR{rnd.NextInt64(1_000_000_000, 9_999_999_999)}",
                ShiprocketCourier = "Shiprocket (Blue Dart Express Air)",
                DeliveryStatus = "Order Placed & Waybill Generated",
                TrackingEvents = [new() { Status = "Shipment Scheduled & Waybill Created", Time = Mapping.TimelineStamp(), Done = true, Active = true }],
            };
        }

        var payload = new
        {
            order_id = order.Id,
            order_date = order.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
            pickup_location = _o.PickupLocation,
            billing_customer_name = order.CustomerName,
            billing_last_name = "",
            billing_address = order.ShippingAddress.AddressLine1,
            billing_address_2 = order.ShippingAddress.AddressLine2 ?? "",
            billing_city = order.ShippingAddress.City,
            billing_pincode = order.ShippingAddress.Pincode,
            billing_state = order.ShippingAddress.State,
            billing_country = string.IsNullOrWhiteSpace(order.ShippingAddress.Country) ? "India" : order.ShippingAddress.Country,
            billing_email = order.CustomerEmail,
            billing_phone = order.CustomerPhone,
            shipping_is_billing = true,
            order_items = order.Items.Select(i => new { name = i.Name, sku = i.Sku, units = i.Quantity, selling_price = i.Price, discount = 0, tax = 0, hsn = long.TryParse(i.Hsn, out var h) ? h : 21069099 }),
            payment_method = isCod ? "COD" : "Prepaid",
            sub_total = order.TotalAmount,
            length = parcel.LengthCm, breadth = parcel.BreadthCm, height = parcel.HeightCm, weight = parcel.WeightKg,
        };

        var created = await PostAsync($"{_o.ApiUrl}/orders/create/adhoc", payload, token)
            ?? throw new InvalidOperationException("Shiprocket API Order Creation Failed");
        var shipmentId = created["shipment_id"]?.ToString() ?? "";
        var srOrderId = created["order_id"]?.ToString() ?? "";

        string? awb = null;
        var courier = "Shiprocket Express";
        try
        {
            var awbRes = await PostAsync($"{_o.ApiUrl}/courier/assign/awb", new { shipment_id = shipmentId }, token);
            awb = awbRes?["response"]?["data"]?["awb_code"]?.ToString();
            courier = awbRes?["response"]?["data"]?["courier_name"]?.ToString() ?? courier;
        }
        catch (Exception ex) { log.LogWarning("[Shiprocket] AWB auto-assign deferred: {Msg}", ex.Message); }

        return new ShiprocketResult
        {
            Success = true, ShiprocketOrderId = srOrderId, ShiprocketShipmentId = shipmentId,
            ShiprocketAwb = string.IsNullOrWhiteSpace(awb) ? $"SR-PENDING-{shipmentId}" : awb,
            ShiprocketCourier = courier,
            DeliveryStatus = string.IsNullOrWhiteSpace(awb) ? "Shipment Created" : "AWB Assigned",
            TrackingEvents = [new() { Status = string.IsNullOrWhiteSpace(awb) ? "Shipment Created in Shiprocket" : $"AWB Assigned via {courier}", Time = Mapping.TimelineStamp(), Done = true, Active = true }],
        };
    }

    /// <summary>Returns Shiprocket's raw <c>tracking_data</c> object, or null when unavailable.</summary>
    public async Task<JsonElement?> FetchLiveTrackingAsync(string? awb)
    {
        if (string.IsNullOrWhiteSpace(awb) || awb.StartsWith("SR-PENDING-")) return null;
        try
        {
            var token = await GetTokenAsync();
            if (token is null) return null;
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_o.ApiUrl}/courier/track/awb/{awb}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var res = await http.SendAsync(req);
            if (!res.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            return doc.RootElement.TryGetProperty("tracking_data", out var td) ? td.Clone() : null;
        }
        catch { return null; }
    }

    private async Task<JsonNode?> PostAsync(string url, object body, string token)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
        {
            log.LogError("[Shiprocket] {Url} failed: {Body}", url, text);
            var msg = JsonNode.Parse(text)?["message"]?.ToString();
            throw new InvalidOperationException(msg ?? "Shiprocket API request failed");
        }
        return JsonNode.Parse(text);
    }
}
