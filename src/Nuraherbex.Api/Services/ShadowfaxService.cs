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
/// Shadowfax Forward Logistics API: order creation, tracking, cancellation, and pincode serviceability.
/// Results reuse <see cref="ShiprocketResult"/> so the order pipeline treats every courier the same way.
/// </summary>
public class ShadowfaxService(HttpClient http, IOptions<ShadowfaxOptions> options, ILogger<ShadowfaxService> log)
{
    private readonly ShadowfaxOptions _o = options.Value;

    public bool IsConfigured => _o.IsConfigured;

    public async Task<ShiprocketResult> CreateOrderAsync(Order order, ParcelSpecs parcel, string? clientOrderId = null)
    {
        if (!_o.IsConfigured) throw new InvalidOperationException("Shadowfax token is not configured.");
        if (!_o.Pickup.IsComplete)
            throw new InvalidOperationException("Shadowfax live shipping is selected, but the pickup address is missing. Configure Shadowfax:Pickup with Name, Contact, AddressLine1, City, and Pincode in appsettings.Local.json.");

        var warehouse = _o.OrderType.Equals("warehouse", StringComparison.OrdinalIgnoreCase);
        var isCod = order.PaymentMethod == "COD";
        var ship = order.ShippingAddress;
        var ret = _o.Return.IsComplete ? _o.Return : _o.Pickup;
        ValidateAddress(_o.Pickup, "pickup", requireContact: !warehouse);
        ValidateAddress(ret, "return", requireContact: !warehouse);
        if (!warehouse && string.IsNullOrWhiteSpace(_o.Pickup.UniqueCode))
            throw new InvalidOperationException("Shadowfax marketplace bookings require the seller pickup unique code registered in Shadowfax. Set Shadowfax:Pickup:UniqueCode in appsettings.Local.json.");
        if (string.IsNullOrWhiteSpace(order.CustomerName) || Phone10(order.CustomerPhone).Length != 10
            || string.IsNullOrWhiteSpace(ship.AddressLine1) || string.IsNullOrWhiteSpace(ship.City) || string.IsNullOrWhiteSpace(ship.State))
            throw new InvalidOperationException("Shadowfax: customer name, 10-digit phone, address, city, and state are required.");
        if (order.Items.Count == 0)
            throw new InvalidOperationException("Shadowfax requires at least one order item.");

        var volumetricWeightKg = parcel.LengthCm * parcel.BreadthCm * parcel.HeightCm / 5000m;
        var productValue = Math.Max(order.Subtotal - order.DiscountAmount, 0);
        if (!int.TryParse(ship.Pincode, out var deliveryPincode) || deliveryPincode is < 100000 or > 999999)
            throw new InvalidOperationException("Shadowfax: customer delivery pincode must be a valid 6-digit Indian pincode.");

        var requestedClientOrderId = string.IsNullOrWhiteSpace(clientOrderId) ? order.Id : clientOrderId.Trim();
        var body = new Dictionary<string, object?>
        {
            ["order_type"] = warehouse ? "warehouse" : "marketplace",
            ["order_details"] = new
            {
                client_order_id = requestedClientOrderId,
                actual_weight = parcel.WeightKg,
                volumetric_weight = volumetricWeightKg,
                product_value = productValue,
                payment_mode = isCod ? "cod" : "prepaid",
                cod_amount = isCod ? order.TotalAmount : 0m,
                total_amount = order.TotalAmount,
                order_service = _o.ServiceTier,
            },
            ["customer_details"] = new
            {
                name = order.CustomerName,
                contact = Phone10(order.CustomerPhone),
                address_line_1 = ship.AddressLine1,
                address_line_2 = ship.AddressLine2 ?? "",
                city = ship.City,
                state = ship.State,
                pincode = deliveryPincode,
            },
            ["pickup_details"] = PickupAddress(_o.Pickup, warehouse ? "warehouse" : "seller"),
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
        body[warehouse ? "rto_details" : "rts_details"] = ReturnAddress(ret, warehouse ? "origin" : "seller");

        var res = await SendAsync(HttpMethod.Post, "/v3/clients/orders/", body);
        var data = res?["data"];
        var responseMessage = NodeText(res, "message");
        var responseText = res?.ToJsonString() ?? "";
        var responseCoid = NodeText(res, "COID", "client_order_id") ?? NodeText(data, "COID", "client_order_id");
        var duplicateAwb = NodeText(res, "AWB", "awb_number", "awb") ?? NodeText(data, "AWB", "awb_number", "awb");
        var isDuplicateBooking = string.Equals(responseMessage, "Failure", StringComparison.OrdinalIgnoreCase)
            && string.Equals(responseCoid, requestedClientOrderId, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(duplicateAwb)
            && responseText.Contains("already created", StringComparison.OrdinalIgnoreCase);
        if (!string.Equals(responseMessage, "Success", StringComparison.OrdinalIgnoreCase) && !isDuplicateBooking)
            throw new InvalidOperationException($"Shadowfax: {NodeText(res, "errors") ?? responseMessage ?? "order creation was rejected"}");

        var awb = isDuplicateBooking ? duplicateAwb : NodeText(data, "awb_number", "AWB", "awb") ?? NodeText(res, "awb_number", "AWB", "awb");
        if (string.IsNullOrWhiteSpace(awb))
            throw new InvalidOperationException($"Shadowfax: {NodeText(res, "errors") ?? responseMessage ?? "no AWB number returned"}");

        var id = NodeText(data, "id", "order_id") ?? NodeText(res, "id", "order_id", "COID") ?? "";
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

    /// <summary>Generates a printable PDF label for a booked shipment that has not been picked up.</summary>
    public async Task<string> GenerateLabelAsync(string awb)
    {
        if (!_o.IsConfigured) throw new InvalidOperationException("Shadowfax token is not configured.");
        if (string.IsNullOrWhiteSpace(awb)) throw new InvalidOperationException("A Shadowfax AWB is required to generate a label.");

        var res = await SendAsync(HttpMethod.Post, "/client/generate_label/", new { awb_number = awb.Trim(), file_type = "pdf" });
        if (!string.Equals(res?["message"]?.ToString(), "Success", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Shadowfax: {res?["errors"]?.ToString() ?? res?["message"]?.ToString() ?? "label generation was rejected"}");

        var labelUrl = res?["data"]?["label_url"]?.ToString();
        if (!Uri.TryCreate(labelUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Shadowfax did not return a valid HTTPS shipping label URL.");

        return uri.ToString();
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
            return HasServiceForPincode(res, pincode, _o.ServiceTier);
        }
        catch (Exception ex)
        {
            log.LogWarning("[Shadowfax] Serviceability check failed for {Pin}: {Msg}", pincode, ex.Message);
            return null;
        }
    }

    private static string? NodeText(JsonNode? node, params string[] names)
    {
        if (node is not JsonObject obj) return null;
        foreach (var pair in obj)
            if (names.Any(name => string.Equals(name, pair.Key, StringComparison.OrdinalIgnoreCase)))
                return pair.Value?.ToString();
        return null;
    }

    private static bool HasServiceForPincode(JsonNode? node, string pincode, string tier) => node is JsonArray rows
        && rows.OfType<JsonObject>().Any(row =>
            row["code"]?.ToString() == pincode
            && row["services"] is JsonArray services
            && services.Any(service => string.Equals(service?.ToString(), tier, StringComparison.OrdinalIgnoreCase)));

    private static void ValidateAddress(ShadowfaxAddress address, string label, bool requireContact)
    {
        if (!address.IsComplete)
            throw new InvalidOperationException($"Shadowfax:{label} address needs a street address, city, and valid 6-digit pincode.");
        if (requireContact && (string.IsNullOrWhiteSpace(address.Name) || Phone10(address.Contact).Length != 10))
            throw new InvalidOperationException($"Shadowfax:{label} address needs a contact name and valid 10-digit phone.");
        if (!int.TryParse(address.Pincode, out var pincode) || pincode is < 100000 or > 999999)
            throw new InvalidOperationException($"Shadowfax:{label} address must use a valid 6-digit Indian pincode.");
    }

    private static object PickupAddress(ShadowfaxAddress a, string type)
    {
        var address = new Dictionary<string, object?>
        {
            ["pickup_type"] = type,
            ["address_line_1"] = a.AddressLine1,
            ["city"] = a.City,
            ["pincode"] = int.Parse(a.Pincode),
        };
        if (type == "seller" || !string.IsNullOrWhiteSpace(a.Name)) address["name"] = a.Name;
        if (type == "seller" || Phone10(a.Contact).Length > 0) address["contact"] = Phone10(a.Contact);
        if (!string.IsNullOrWhiteSpace(a.AddressLine2)) address["address_line_2"] = a.AddressLine2;
        if (!string.IsNullOrWhiteSpace(a.State)) address["state"] = a.State;
        if (!string.IsNullOrWhiteSpace(a.UniqueCode)) address["unique_code"] = a.UniqueCode;
        return address;
    }

    private static object ReturnAddress(ShadowfaxAddress a, string type)
    {
        var address = new Dictionary<string, object?>
        {
            ["return_type"] = type,
            ["address_line_1"] = a.AddressLine1,
            ["pincode"] = int.Parse(a.Pincode),
        };
        if (type == "seller" || !string.IsNullOrWhiteSpace(a.Name)) address["name"] = a.Name;
        if (type == "seller" || Phone10(a.Contact).Length > 0) address["contact"] = Phone10(a.Contact);
        if (!string.IsNullOrWhiteSpace(a.AddressLine2)) address["address_line_2"] = a.AddressLine2;
        if (!string.IsNullOrWhiteSpace(a.City)) address["city"] = a.City;
        if (!string.IsNullOrWhiteSpace(a.State)) address["state"] = a.State;
        if (!string.IsNullOrWhiteSpace(a.UniqueCode)) address["unique_code"] = a.UniqueCode;
        if (!string.IsNullOrWhiteSpace(a.Email)) address["email"] = a.Email;
        return address;
    }

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
