using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nuraherbex.Api.Data;
using Nuraherbex.Api.Options;

namespace Nuraherbex.Api.Services;

/// <summary>
/// WhatsApp Business Cloud API integration:
///  - webhook verification handshake + X-Hub-Signature-256 validation
///  - inbound message / delivery-status ingestion
///  - outbound text + template messages (order updates)
/// </summary>
public partial class WhatsAppService(
    NuraDbContext db,
    HttpClient http,
    IOptions<WhatsAppOptions> options,
    ILogger<WhatsAppService> log)
{
    private readonly WhatsAppOptions _o = options.Value;

    // ---------------------------------------------------------------------
    // Webhook verification (GET)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Meta calls GET with hub.mode, hub.verify_token and hub.challenge when you save the webhook.
    /// We must echo hub.challenge (plain text, HTTP 200) iff the verify token matches.
    /// </summary>
    public bool TryVerifyHandshake(string? mode, string? verifyToken, out bool misconfigured)
    {
        misconfigured = string.IsNullOrWhiteSpace(_o.VerifyToken);
        if (misconfigured) return false;
        return string.Equals(mode, "subscribe", StringComparison.Ordinal)
            && FixedTimeEquals(verifyToken ?? "", _o.VerifyToken);
    }

    // ---------------------------------------------------------------------
    // Signature validation (POST)
    // ---------------------------------------------------------------------

    /// <summary>Validates <c>X-Hub-Signature-256: sha256=&lt;hex&gt;</c> against HMAC-SHA256(AppSecret, rawBody).</summary>
    public bool IsSignatureValid(byte[] rawBody, string? signatureHeader)
    {
        if (string.IsNullOrWhiteSpace(_o.AppSecret) || string.IsNullOrWhiteSpace(signatureHeader)) return false;
        const string prefix = "sha256=";
        if (!signatureHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(_o.AppSecret), rawBody);
        byte[] received;
        try { received = Convert.FromHexString(signatureHeader[prefix.Length..]); }
        catch (FormatException) { return false; }

        return CryptographicOperations.FixedTimeEquals(expected, received);
    }

    public bool HasAppSecret => !string.IsNullOrWhiteSpace(_o.AppSecret);

    private static bool FixedTimeEquals(string a, string b)
    {
        var x = Encoding.UTF8.GetBytes(a);
        var y = Encoding.UTF8.GetBytes(b);
        return x.Length == y.Length && CryptographicOperations.FixedTimeEquals(x, y);
    }

    // ---------------------------------------------------------------------
    // Inbound processing
    // ---------------------------------------------------------------------

    public async Task ProcessWebhookAsync(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        if (!doc.RootElement.TryGetProperty("entry", out var entries)) return;

        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("changes", out var changes)) continue;
            foreach (var change in changes.EnumerateArray())
            {
                if (!change.TryGetProperty("value", out var value)) continue;

                var names = new Dictionary<string, string>();
                if (value.TryGetProperty("contacts", out var contacts))
                    foreach (var c in contacts.EnumerateArray())
                        if (c.TryGetProperty("wa_id", out var wa) && c.TryGetProperty("profile", out var prof) && prof.TryGetProperty("name", out var n))
                            names[wa.GetString() ?? ""] = n.GetString() ?? "";

                if (value.TryGetProperty("messages", out var messages))
                    foreach (var m in messages.EnumerateArray())
                        await HandleInboundMessageAsync(m, names);

                if (value.TryGetProperty("statuses", out var statuses))
                    foreach (var s in statuses.EnumerateArray())
                        await HandleStatusAsync(s);
            }
        }
        await db.SaveChangesAsync();
    }

    private async Task HandleInboundMessageAsync(JsonElement m, Dictionary<string, string> names)
    {
        var id = m.GetProperty("id").GetString() ?? "";
        if (await db.WhatsAppMessages.AnyAsync(x => x.WaMessageId == id && x.Direction == "inbound")) return; // Meta retries; stay idempotent

        var from = m.GetProperty("from").GetString() ?? "";
        var type = m.TryGetProperty("type", out var t) ? t.GetString() ?? "text" : "text";
        var body = type switch
        {
            "text" => m.GetProperty("text").GetProperty("body").GetString(),
            "button" => m.GetProperty("button").GetProperty("text").GetString(),
            "interactive" => ExtractInteractive(m.GetProperty("interactive")),
            "image" or "video" or "audio" or "document" or "sticker" =>
                m.GetProperty(type).TryGetProperty("caption", out var cap) ? cap.GetString() : $"[{type}]",
            "location" => "[location shared]",
            _ => $"[{type}]",
        };

        db.WhatsAppMessages.Add(new WhatsAppMessage
        {
            Direction = "inbound", WaMessageId = id, Phone = from, ProfileName = names.GetValueOrDefault(from),
            Type = type, Body = body, Status = "received", RawPayload = m.GetRawText(),
        });
        await db.SaveChangesAsync();

        if (_o.AutoReplyEnabled && _o.CanSend && type is "text" or "button" or "interactive")
        {
            try { await AutoReplyAsync(from, body ?? ""); }
            catch (Exception ex) { log.LogWarning(ex, "[WhatsApp] Auto-reply failed for {Phone}", from); }
        }
    }

    private static string? ExtractInteractive(JsonElement i) =>
        i.TryGetProperty("button_reply", out var b) ? b.GetProperty("title").GetString()
        : i.TryGetProperty("list_reply", out var l) ? l.GetProperty("title").GetString() : null;

    private async Task HandleStatusAsync(JsonElement s)
    {
        var id = s.GetProperty("id").GetString() ?? "";
        var status = s.GetProperty("status").GetString() ?? "";
        var msg = await db.WhatsAppMessages.FirstOrDefaultAsync(x => x.WaMessageId == id && x.Direction == "outbound");
        if (msg is null) return;
        msg.Status = status; // sent | delivered | read | failed
        if (status == "failed" && s.TryGetProperty("errors", out var errs) && errs.GetArrayLength() > 0)
            log.LogWarning("[WhatsApp] Message {Id} failed: {Err}", id, errs[0].GetRawText());
    }

    // ---------------------------------------------------------------------
    // Auto-reply: "NH-123456" or "track" returns the live order status
    // ---------------------------------------------------------------------

    [GeneratedRegex(@"NH-\d{6}", RegexOptions.IgnoreCase)]
    private static partial Regex OrderIdRegex();

    private async Task AutoReplyAsync(string phone, string text)
    {
        var lowered = text.Trim().ToLowerInvariant();
        Order? order = null;

        var match = OrderIdRegex().Match(text);
        if (match.Success)
        {
            var oid = match.Value.ToUpperInvariant();
            order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == oid);
        }
        else if (lowered is "track" or "status" or "order" or "my order" or "where is my order")
        {
            var tail = phone.Length >= 10 ? phone[^10..] : phone;
            order = await db.Orders.AsNoTracking().Where(o => o.CustomerPhone.EndsWith(tail)).OrderByDescending(o => o.CreatedAt).FirstOrDefaultAsync();
            if (order is null)
            {
                await SendTextAsync(phone, "We couldn't find an order linked to this WhatsApp number. Reply with your order ID (e.g. NH-123456) and we'll check it for you.");
                return;
            }
        }
        else if (lowered is "hi" or "hello" or "hey" or "help")
        {
            await SendTextAsync(phone, "Welcome to Nura Herbex 🌿\nReply with your *order ID* (e.g. NH-123456) or send *track* to get your latest order status.");
            return;
        }

        if (order is not null) await SendTextAsync(phone, BuildStatusText(order), order.Id);
    }

    public static string BuildStatusText(Order o)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"📦 *Order {o.Id}*");
        sb.AppendLine($"Status: *{o.DeliveryStatus}*");
        sb.AppendLine($"Total: ₹{o.TotalAmount:N0} ({(o.PaymentMethod == "COD" ? "Cash on Delivery" : o.PaymentStatus)})");
        if (!string.IsNullOrWhiteSpace(o.ShiprocketAwb) && !o.ShiprocketAwb.StartsWith("SR-PENDING-"))
            sb.AppendLine($"Courier: {o.ShiprocketCourier} · AWB {o.ShiprocketAwb}");
        return sb.ToString().TrimEnd();
    }

    // ---------------------------------------------------------------------
    // Outbound
    // ---------------------------------------------------------------------

    /// <summary>Normalises Indian numbers to E.164 digits without '+', e.g. 9876543210 → 919876543210.</summary>
    public string NormalizePhone(string phone)
    {
        var digits = new string((phone ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length == 10) return _o.DefaultCountryCode + digits;
        if (digits.Length == 11 && digits[0] == '0') return _o.DefaultCountryCode + digits[1..];
        return digits;
    }

    public Task<bool> SendTextAsync(string phone, string text, string? orderId = null) =>
        SendAsync(phone, new
        {
            messaging_product = "whatsapp", recipient_type = "individual", to = NormalizePhone(phone),
            type = "text", text = new { preview_url = false, body = text },
        }, "text", text, orderId);

    public Task<bool> SendTemplateAsync(string phone, string template, IEnumerable<string> bodyParams, string? orderId = null) =>
        SendAsync(phone, new
        {
            messaging_product = "whatsapp", to = NormalizePhone(phone), type = "template",
            template = new
            {
                name = template, language = new { code = _o.TemplateLanguage },
                components = new[] { new { type = "body", parameters = bodyParams.Select(p => new { type = "text", text = p }).ToArray() } },
            },
        }, "template", $"[template:{template}] {string.Join(" | ", bodyParams)}", orderId);

    private async Task<bool> SendAsync(string phone, object payload, string type, string logBody, string? orderId)
    {
        if (!_o.CanSend)
        {
            log.LogWarning("[WhatsApp] Access token / phone number id not configured — outbound skipped");
            return false;
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://graph.facebook.com/{_o.ApiVersion}/{_o.PhoneNumberId}/messages")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _o.AccessToken);

        try
        {
            using var res = await http.SendAsync(req);
            var text = await res.Content.ReadAsStringAsync();
            string? waId = null;
            if (res.IsSuccessStatusCode)
            {
                using var d = JsonDocument.Parse(text);
                waId = d.RootElement.GetProperty("messages")[0].GetProperty("id").GetString();
            }
            else log.LogWarning("[WhatsApp] Send failed {Status}: {Body}", (int)res.StatusCode, text);

            db.WhatsAppMessages.Add(new WhatsAppMessage
            {
                Direction = "outbound", WaMessageId = waId ?? $"failed-{Guid.NewGuid():N}", Phone = NormalizePhone(phone),
                Type = type, Body = logBody, Status = res.IsSuccessStatusCode ? "sent" : "failed", OrderId = orderId,
            });
            await db.SaveChangesAsync();
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "[WhatsApp] Send exception");
            return false;
        }
    }

    // ---------------------------------------------------------------------
    // Order lifecycle notifications
    // ---------------------------------------------------------------------

    public Task NotifyOrderConfirmedAsync(Order o) =>
        !string.IsNullOrWhiteSpace(_o.OrderConfirmationTemplate)
            ? SendTemplateAsync(o.CustomerPhone, _o.OrderConfirmationTemplate, [o.CustomerName.Split(' ')[0], o.Id, $"₹{o.TotalAmount:N0}"], o.Id)
            : SendTextAsync(o.CustomerPhone, $"✅ Hi {o.CustomerName.Split(' ')[0]}, your Stamix™ order *{o.Id}* (₹{o.TotalAmount:N0}) is confirmed. We'll message you once it ships. 🌿", o.Id);

    public Task NotifyShippedAsync(Order o) =>
        !string.IsNullOrWhiteSpace(_o.OrderShippedTemplate)
            ? SendTemplateAsync(o.CustomerPhone, _o.OrderShippedTemplate, [o.CustomerName.Split(' ')[0], o.Id, o.ShiprocketCourier ?? "Courier", o.ShiprocketAwb ?? "-"], o.Id)
            : SendTextAsync(o.CustomerPhone, $"🚚 Your order *{o.Id}* is on its way via {o.ShiprocketCourier}. AWB: {o.ShiprocketAwb}", o.Id);

    public Task NotifyDeliveredAsync(Order o) =>
        !string.IsNullOrWhiteSpace(_o.OrderDeliveredTemplate)
            ? SendTemplateAsync(o.CustomerPhone, _o.OrderDeliveredTemplate, [o.CustomerName.Split(' ')[0], o.Id], o.Id)
            : SendTextAsync(o.CustomerPhone, $"🎉 Your Stamix™ order *{o.Id}* has been delivered. Enjoy — and we'd love your review!", o.Id);
}
