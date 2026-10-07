using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Nuraherbex.Api.Data;
using Nuraherbex.Api.Options;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Services;

/// <summary>
/// Shadowfax Unified Forward API (https://sfxunifiedapi.docs.apiary.io): order creation, tracking, cancellation, pincode serviceability.
/// Results reuse <see cref="ShiprocketResult"/> so the order pipeline treats every courier the same way.
/// </summary>
public class ShadowfaxService(HttpClient http, IOptions<ShadowfaxOptions> options, ILogger<ShadowfaxService> log)
{
    private readonly ShadowfaxOptions _o = options.Value;

    public bool IsConfigured => _o.IsConfigured;

    public async Task<ShiprocketResult> CreateOrderAsync(Order order, ParcelSpecs parcel)
    {
        if (!_o.IsConfigured) throw new InvalidOperationException("Shadowfax token is not configured.");
        if (!_o.Pickup.IsComplete) throw new InvalidOperationException("Shadowfax:Pickup address is not configured.");

        var warehouse = _o.OrderType.Equals("warehouse", StringComparison.OrdinalIgnoreCase);
        var isCod = order.PaymentMethod == "COD";
        var ship = order.ShippingAddress;
        var ret = _o.Return.IsComplete ? _o.Return : _o.Pickup;

        var volumetricGrams = parcel.LengthCm * parcel.BreadthCm * parcel.HeightCm / 5000m * 1000m;
        var productValue = Math.Max(order.Subtotal - order.DiscountAmount, 0);

        var body = new Dictionary<string, object?>
        {
            ["order_type"] = warehouse ? "warehouse" : "marketplace",
            ["order_details"] = new
            {
                client_order_id = order.Id,
                actual_weight = (int)Math.Round(parcel.WeightKg * 1000m),
                volumetric_weight = (int)Math.Round(volumetricGrams),
                product_value = productValue,
                payment_mode = isCod ? "COD" : "Prepaid",
                cod_amount = isCod ? order.TotalAmount : 0m,
                total_amount = order.TotalAmount,
                order_service = "regular",
            },
            ["customer_details"] = new
            {
                name = order.CustomerName,
                contact = Phone10(order.CustomerPhone),
                address_line_1 = ship.AddressLine1,
                address_line_2 = ship.AddressLine2 ?? "",
                city = ship.City,
                state = ship.State,
                pincode = int.TryParse(ship.Pincode, out var pin) ? pin : 0,
            },
            ["pickup_details"] = Address(_o.Pickup, withEmail: false),
            [warehouse ? "rto_details" : "rts_details"] = Address(ret, withEmail: !warehouse),
            ["product_details"] = order.Items.Select(i => new
            {
                sku_name = i.Name,
                sku_id = i.Sku,
                hsn_code = i.Hsn ?? "",
                invoice_no = order.Id,
                price = i.Price,
                additional_details = new { quantity = i.Quantity },
            }).ToArray(),
        };

        var res = await SendAsync(HttpMethod.Post, "/v3/clients/orders/", body);
        // Shadowfax reports validation failures as HTTP 200 with { message: "Failure", errors: "..." }.
        var data = res?["data"];
        var awb = data?["awb_number"]?.ToString();
        if (string.IsNullOrWhiteSpace(awb))
            throw new InvalidOperationException($"Shadowfax: {res?["errors"]?.ToString() ?? res?["message"]?.ToString() ?? "no AWB number returned"}");

        var id = data?["id"]?.ToString() ?? "";
        return new ShiprocketResult
        {
            Success = true,
            ShiprocketOrderId = id,
            ShiprocketShipmentId = id,
            ShiprocketAwb = awb,
            ShiprocketCourier = "Shadowfax",
            DeliveryStatus = "AWB Assigned",
            TrackingEvents = [new() { Status = "Shipment Created with Shadowfax", Time = Mapping.TimelineStamp(), Done = true, Active = true }],
        };
    }

    /// <summary>
    /// Returns tracking in the Shiprocket-shaped object the storefront already renders:
    /// <c>{ track_url, shipment_track_activities: [{ date, activity, location }] }</c>, newest scan first. Null when unavailable.
    /// </summary>
    public async Task<JsonElement?> FetchLiveTrackingAsync(string? awb)
    {
        if (string.IsNullOrWhiteSpace(awb) || !_o.IsConfigured) return null;
        try
        {
            var res = await SendAsync(HttpMethod.Get, $"/v4/clients/orders/{Uri.EscapeDataString(awb)}/track/", null);
            var scans = (res?["tracking_details"] as JsonArray)?.OfType<JsonObject>()
                .OrderByDescending(s => s["created"]?.ToString())
                .Select(s => new
                {
                    date = FormatScanTime(s["created"]?.ToString()),
                    activity = s["remarks"]?.ToString() is { Length: > 0 } r ? r : s["status"]?.ToString() ?? "Courier Scan",
                    location = s["location"]?.ToString() ?? "",
                }).ToArray() ?? [];
            return JsonSerializer.SerializeToElement(new
            {
                track_url = res?["order_details"]?["customer_track_url"]?.ToString(),
                current_status = res?["order_details"]?["status_display"]?.ToString(),
                shipment_track_activities = scans,
            });
        }
        catch (Exception ex)
        {
            log.LogWarning("[Shadowfax] Tracking unavailable for {Awb}: {Msg}", awb, ex.Message);
            return null;
        }
    }

    /// <summary>Cancels by AWB. Returns Shadowfax's message ("Request has been marked as cancelled" / "queued for cancellation").</summary>
    public async Task<string> CancelAsync(string awb, string reason)
    {
        if (!_o.IsConfigured) throw new InvalidOperationException("Shadowfax token is not configured.");
        var res = await SendAsync(HttpMethod.Post, "/v3/clients/orders/cancel/", new { request_id = awb, cancel_remarks = reason });
        var code = res?["responseCode"]?.GetValue<int>() ?? 200;
        var msg = res?["responseMsg"]?.ToString() ?? "Cancelled";
        // Shadowfax reports failures inside a 200 body (responseCode 400/500); 304 means queued, which is fine.
        if (code >= 400) throw new InvalidOperationException(msg);
        return msg;
    }

    /// <summary>True when Shadowfax delivers to this pincode. Fails open (null) when the API cannot be reached.</summary>
    public async Task<bool?> IsServiceableAsync(string pincode)
    {
        if (!_o.IsConfigured || pincode.Length != 6 || !pincode.All(char.IsDigit)) return null;
        try
        {
            var res = await SendAsync(HttpMethod.Get, $"/v1/clients/serviceability/?service=customer_delivery&page=1&count=10&pincodes={pincode}", null);
            return FindPincode(res, pincode);
        }
        catch (Exception ex)
        {
            log.LogWarning("[Shadowfax] Serviceability check failed for {Pin}: {Msg}", pincode, ex.Message);
            return null;
        }
    }

    private static bool FindPincode(JsonNode? node, string pincode) => node switch
    {
        JsonArray a => a.Any(n => FindPincode(n, pincode)),
        JsonObject o => o.Any(kv => FindPincode(kv.Value, pincode)),
        JsonValue v => v.ToString() == pincode,
        _ => false,
    };

    private static object Address(ShadowfaxAddress a, bool withEmail) => withEmail
        ? new
        {
            name = a.Name, contact = Phone10(a.Contact), address_line_1 = a.AddressLine1, address_line_2 = a.AddressLine2,
            city = a.City, state = a.State, pincode = int.Parse(a.Pincode), email = a.Email, unique_code = a.UniqueCode,
        }
        : new
        {
            name = a.Name, contact = Phone10(a.Contact), address_line_1 = a.AddressLine1, address_line_2 = a.AddressLine2,
            city = a.City, state = a.State, pincode = int.Parse(a.Pincode), unique_code = a.UniqueCode,
        };

    private static string Phone10(string? phone)
    {
        var digits = new string((phone ?? "").Where(char.IsDigit).ToArray());
        return digits.Length > 10 ? digits[^10..] : digits;
    }

    private static string FormatScanTime(string? iso) =>
        DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t)
            ? t.ToLocalTime().ToString("dd MMM yyyy, hh:mm tt") : iso ?? "";

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, object? body)
    {
        using var req = new HttpRequestMessage(method, _o.ApiUrl + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Token", _o.ActiveToken);
        if (body is not null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var res = await http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
        {
            log.LogError("[Shadowfax] {Method} {Path} -> {Status}: {Body}", method, path, (int)res.StatusCode, text);
            string? msg = null;
            try
            {
                var j = JsonNode.Parse(text);
                msg = j?["message"]?.ToString() ?? j?["responseMsg"]?.ToString() ?? j?["errors"]?.ToString() ?? j?["detail"]?.ToString();
            }
            catch (JsonException) { }
            throw new InvalidOperationException($"Shadowfax: {msg ?? $"request failed ({(int)res.StatusCode})"}");
        }
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }
}
