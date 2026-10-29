using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nuraherbex.Api.Data;
using Nuraherbex.Api.Options;
using Nuraherbex.Shared.Models;

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
    LeadService leads,
    IOptions<WhatsAppOptions> options,
    ILogger<WhatsAppService> log,
    Microsoft.Extensions.Hosting.IHostEnvironment? env = null)
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
        var from = m.GetProperty("from").GetString() ?? "";
        var type = m.TryGetProperty("type", out var t) ? t.GetString() ?? "text" : "text";

        // A reaction is not a new message: show the emoji on the message it was left on.
        if (type == "reaction") { await ApplyReactionAsync(m); return; }

        if (await db.WhatsAppMessages.AnyAsync(x => x.WaMessageId == id && x.Direction == "inbound")) return; // Meta retries; stay idempotent

        // Photos, stickers, videos, voice notes and documents carry a media id (downloaded below) and an optional caption.
        string? mediaId = null, mediaMime = null, mediaName = null;
        string? caption = null;
        if (Mapping.MediaTypes.Contains(type) && m.TryGetProperty(type, out var media) && media.ValueKind == JsonValueKind.Object)
        {
            mediaId = media.TryGetProperty("id", out var mid) ? mid.GetString() : null;
            mediaMime = media.TryGetProperty("mime_type", out var mm) ? mm.GetString() : null;
            mediaName = media.TryGetProperty("filename", out var fn) ? fn.GetString() : null;
            caption = media.TryGetProperty("caption", out var cap) ? cap.GetString() : null;
        }

        var body = type switch
        {
            "text" => m.GetProperty("text").GetProperty("body").GetString(),
            "button" => m.GetProperty("button").GetProperty("text").GetString(),
            "interactive" => ExtractInteractive(m.GetProperty("interactive")),
            "location" => LocationText(m),
            "unsupported" => UnsupportedText(m),
            _ when Mapping.MediaTypes.Contains(type) => caption,
            _ => $"[{type}]",
        };

        // Click-to-WhatsApp ads attach a "referral" object (ad headline, source url/id) to the first message.
        string? referralJson = null, referralDetail = null;
        if (m.TryGetProperty("referral", out var referral) && referral.ValueKind == JsonValueKind.Object)
        {
            referralJson = referral.GetRawText();
            referralDetail = referral.TryGetProperty("headline", out var h) && !string.IsNullOrWhiteSpace(h.GetString()) ? h.GetString()
                : referral.TryGetProperty("source_url", out var u) ? u.GetString() : null;
        }

        var profileName = names.GetValueOrDefault(from);
        var lead = (await leads.UpsertFromWhatsAppAsync(from, profileName, body ?? Mapping.MessageLabel(type), referralJson, referralDetail)).Lead;

        var saved = new WhatsAppMessage
        {
            Direction = "inbound", WaMessageId = id, Phone = from, ProfileName = profileName,
            Type = type, Body = body, Status = "received", RawPayload = m.GetRawText(), LeadId = lead.Id,
            MediaId = mediaId, MediaMime = mediaMime, MediaName = mediaName,
        };
        db.WhatsAppMessages.Add(saved);
        await db.SaveChangesAsync();

        // Meta deletes media after about 30 days, so keep a copy now. A failure here never loses the message; the file is fetched again when someone opens it.
        if (mediaId is not null)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await DownloadMediaAsync(saved, cts.Token);
        }

        if (LeadService.IsStopKeyword(body))
        {
            if (_o.CanSend) await SendTextAsync(from, "You have been unsubscribed from Nura Herbex messages. Reply START any time to opt back in. 🌿");
            return;
        }

        if (_o.CanSend)
        {
            try { if (await leads.AfterHoursReplyAsync(lead) is { } away) await SendTextAsync(from, away, null, "auto"); }
            catch (Exception ex) { log.LogWarning(ex, "[WhatsApp] After-hours reply failed for {Phone}", from); }
        }

        if (_o.AutoReplyEnabled && _o.CanSend && type is "text" or "button" or "interactive")
        {
            try { await AutoReplyAsync(from, body ?? ""); }
            catch (Exception ex) { log.LogWarning(ex, "[WhatsApp] Auto-reply failed for {Phone}", from); }
        }
    }

    private async Task ApplyReactionAsync(JsonElement m)
    {
        if (!m.TryGetProperty("reaction", out var r) || !r.TryGetProperty("message_id", out var mid)) return;
        var target = mid.GetString();
        if (string.IsNullOrEmpty(target)) return;
        var emoji = r.TryGetProperty("emoji", out var e) ? e.GetString() : null; // no emoji = the reaction was removed
        var msg = await db.WhatsAppMessages.FirstOrDefaultAsync(x => x.WaMessageId == target);
        if (msg is null) return;
        msg.Reaction = string.IsNullOrWhiteSpace(emoji) ? null : emoji.Length > 16 ? emoji[..16] : emoji;
        await db.SaveChangesAsync();
    }

    /// <summary>Meta sends type "unsupported" (error 131051) when it cannot pass the content on; say so instead of showing a bare tag.</summary>
    private static string UnsupportedText(JsonElement m)
    {
        string? why = null;
        if (m.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array && errs.GetArrayLength() > 0)
        {
            var e = errs[0];
            why = e.TryGetProperty("details", out var d) && !string.IsNullOrWhiteSpace(d.GetString()) ? d.GetString()
                : e.TryGetProperty("title", out var t) ? t.GetString() : null;
        }
        return "WhatsApp could not deliver this message's content" + (string.IsNullOrWhiteSpace(why) ? "." : $" ({why}).") + " Ask the customer to send it again, or as a document.";
    }

    private static string LocationText(JsonElement m)
    {
        if (!m.TryGetProperty("location", out var l)) return "[location shared]";
        var lat = l.TryGetProperty("latitude", out var la) ? la.GetDouble() : (double?)null;
        var lng = l.TryGetProperty("longitude", out var lo) ? lo.GetDouble() : (double?)null;
        var name = l.TryGetProperty("name", out var n) ? n.GetString() : null;
        var address = l.TryGetProperty("address", out var a) ? a.GetString() : null;
        var label = string.Join(", ", new[] { name, address }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return lat is null || lng is null ? "[location shared]"
            : $"Location: {label}{(label.Length > 0 ? " - " : "")}https://maps.google.com/?q={lat.Value.ToString(inv)},{lng.Value.ToString(inv)}";
    }

    // ---------------------------------------------------------------------
    // Customer media (photos, stickers, voice notes, documents)
    // ---------------------------------------------------------------------

    private const long MaxMediaBytes = 16 * 1024 * 1024;

    public string MediaDirectory => string.IsNullOrWhiteSpace(_o.MediaPath)
        ? Path.Combine(env?.ContentRootPath ?? AppContext.BaseDirectory, "App_Data", "whatsapp-media")
        : _o.MediaPath;

    private static bool IsMetaHost(Uri u) =>
        u.Scheme == Uri.UriSchemeHttps && (u.Host.EndsWith(".fbsbx.com") || u.Host.EndsWith(".facebook.com") || u.Host.EndsWith(".whatsapp.net") || u.Host.EndsWith(".fbcdn.net") || u.Host.EndsWith(".whatsapp.com"));

    private static string ExtensionFor(string? mime, string? fileName)
    {
        var fromName = Path.GetExtension(fileName ?? "");
        if (fromName is { Length: > 1 and <= 8 } && fromName.Skip(1).All(char.IsLetterOrDigit)) return fromName.ToLowerInvariant();
        return (mime ?? "").Split(';')[0].Trim().ToLowerInvariant() switch
        {
            "image/jpeg" => ".jpg", "image/png" => ".png", "image/webp" => ".webp", "image/gif" => ".gif",
            "video/mp4" => ".mp4", "video/3gpp" => ".3gp", "audio/ogg" => ".ogg", "audio/mpeg" => ".mp3", "audio/aac" => ".aac",
            "audio/amr" => ".amr", "audio/mp4" => ".m4a", "application/pdf" => ".pdf",
            _ => ".bin",
        };
    }

    /// <summary>Fetches the customer's file from Meta and keeps it on disk. Safe to call again: returns true if the file is already stored.</summary>
    public async Task<bool> DownloadMediaAsync(WhatsAppMessage msg, CancellationToken ct = default)
    {
        if (msg.MediaId is null) return false;
        if (msg.MediaFile is not null && File.Exists(Path.Combine(MediaDirectory, Path.GetFileName(msg.MediaFile)))) return true;
        if (!_o.CanSend) return false;
        try
        {
            using var metaReq = new HttpRequestMessage(HttpMethod.Get, $"https://graph.facebook.com/{_o.ApiVersion}/{Uri.EscapeDataString(msg.MediaId)}");
            metaReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _o.AccessToken);
            using var metaRes = await http.SendAsync(metaReq, ct);
            if (!metaRes.IsSuccessStatusCode)
            {
                log.LogWarning("[WhatsApp] Media lookup for message {Id} failed with HTTP {Status}", msg.Id, (int)metaRes.StatusCode);
                return false;
            }
            using var info = JsonDocument.Parse(await metaRes.Content.ReadAsStringAsync(ct));
            var url = info.RootElement.TryGetProperty("url", out var u) ? u.GetString() : null;
            var mime = info.RootElement.TryGetProperty("mime_type", out var mt) ? mt.GetString() : null;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsMetaHost(uri)) { log.LogWarning("[WhatsApp] Media url for message {Id} was not a Meta address", msg.Id); return false; }
            if (info.RootElement.TryGetProperty("file_size", out var fs) && fs.TryGetInt64(out var size) && size > MaxMediaBytes) return false;

            using var fileReq = new HttpRequestMessage(HttpMethod.Get, uri);
            fileReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _o.AccessToken);
            using var fileRes = await http.SendAsync(fileReq, ct);
            if (!fileRes.IsSuccessStatusCode) { log.LogWarning("[WhatsApp] Media download for message {Id} failed with HTTP {Status}", msg.Id, (int)fileRes.StatusCode); return false; }
            var bytes = await fileRes.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length == 0 || bytes.Length > MaxMediaBytes) return false;

            Directory.CreateDirectory(MediaDirectory);
            var name = $"{msg.Id}{ExtensionFor(mime ?? msg.MediaMime, msg.MediaName)}";
            await File.WriteAllBytesAsync(Path.Combine(MediaDirectory, name), bytes, ct);
            msg.MediaMime = mime ?? msg.MediaMime;
            msg.MediaFile = name;
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            log.LogWarning(ex, "[WhatsApp] Could not download media for message {Id}", msg.Id);
            return false;
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
        {
            log.LogWarning("[WhatsApp] Message {Id} failed: {Err}", id, errs[0].GetRawText());
            msg.RawPayload = errs[0].GetRawText();
            await NotifyFailureAsync("WhatsApp message could not be delivered", $"WhatsApp reported a delivery failure to +{msg.Phone}. Open the WhatsApp log to see which messages failed.");
        }
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

    public Task<bool> SendTextAsync(string phone, string text, string? orderId = null, string? sentBy = null) =>
        SendAsync(phone, new
        {
            messaging_product = "whatsapp", recipient_type = "individual", to = NormalizePhone(phone),
            type = "text", text = new { preview_url = false, body = text },
        }, "text", text, orderId, sentBy);

    public Task<bool> SendTemplateAsync(string phone, string template, IEnumerable<string> bodyParams, string? orderId = null, string? sentBy = null) =>
        SendAsync(phone, new
        {
            messaging_product = "whatsapp", to = NormalizePhone(phone), type = "template",
            template = new
            {
                name = template, language = new { code = _o.TemplateLanguage },
                components = new[] { new { type = "body", parameters = bodyParams.Select(p => new { type = "text", text = p }).ToArray() } },
            },
        }, "template", $"[template:{template}] {string.Join(" | ", bodyParams)}", orderId, sentBy);

    // ---------------------------------------------------------------------
    // Monthly sending limit (admin-controlled)
    // ---------------------------------------------------------------------

    private const string KeyCostTemplate = "whatsapp.costTemplate", KeyCostText = "whatsapp.costText", KeyBudget = "whatsapp.monthlyBudget";
    private const string KeyLimit = "whatsapp.monthlyLimit", KeyWarnPercent = "whatsapp.warnPercent", KeyWarnedMonth = "whatsapp.warnedMonth", KeyReachedMonth = "whatsapp.reachedMonth";

    /// <summary>Why the last send did not go out, when the reason is something staff can act on (for example the monthly limit).</summary>
    public string? LastError { get; private set; }

    /// <summary>True when the last send was refused because the monthly limit is used up.</summary>
    public bool LimitReached { get; private set; }

    private async Task<string?> GetSettingAsync(string key) => (await db.SiteSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key))?.Value;

    private async Task SetSettingAsync(string key, string value)
    {
        var row = await db.SiteSettings.FirstOrDefaultAsync(s => s.Key == key);
        if (row is null) db.SiteSettings.Add(new SiteSetting { Key = key, Value = value });
        else { row.Value = value; row.UpdatedAt = DateTime.UtcNow; }
    }

    /// <summary>The current Indian calendar month as UTC bounds plus a label such as "October 2026".</summary>
    private static (DateTime Start, DateTime End, string Label) MonthRange()
    {
        var ist = FollowUpService.IstNow;
        var start = new DateTime(ist.Year, ist.Month, 1);
        return (FollowUpService.ToUtc(start), FollowUpService.ToUtc(start.AddMonths(1)), ist.ToString("MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture));
    }

    public async Task<WhatsAppLimitDto> GetLimitStatusAsync()
    {
        var (start, end, label) = MonthRange();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var limit = int.TryParse(await GetSettingAsync(KeyLimit), out var l) ? Math.Max(0, l) : 0;
        var warn = int.TryParse(await GetSettingAsync(KeyWarnPercent), out var w) ? Math.Clamp(w, 1, 100) : 80;
        var costTemplate = decimal.TryParse(await GetSettingAsync(KeyCostTemplate), System.Globalization.NumberStyles.Number, inv, out var ct) ? Math.Max(0, ct) : 0m;
        var costText = decimal.TryParse(await GetSettingAsync(KeyCostText), System.Globalization.NumberStyles.Number, inv, out var cx) ? Math.Max(0, cx) : 0m;
        var budget = decimal.TryParse(await GetSettingAsync(KeyBudget), System.Globalization.NumberStyles.Number, inv, out var b) ? Math.Max(0, b) : 0m;

        var rows = await db.WhatsAppMessages.AsNoTracking().Where(m => m.Direction == "outbound" && m.CreatedAt >= start && m.CreatedAt < end)
            .Select(m => new { m.Type, m.Status }).ToListAsync();
        // Failed attempts never reached the customer, so they use neither the allowance nor the budget.
        var delivered = rows.Where(r => r.Status != "failed").ToList();
        var used = delivered.Count;
        var templates = delivered.Count(r => r.Type == "template");
        var texts = used - templates;
        var spent = templates * costTemplate + texts * costText;

        var msgPct = limit == 0 ? 0 : (int)Math.Min(100, Math.Round(100.0 * used / limit));
        var spendPct = budget == 0 ? 0 : (int)Math.Min(100, Math.Round(100.0 * (double)spent / (double)budget));
        var percent = Math.Max(msgPct, spendPct);
        var reached = (limit > 0 && used >= limit) || (budget > 0 && spent >= budget);
        return new WhatsAppLimitDto
        {
            Success = true, Limit = limit, Used = used, Remaining = limit == 0 ? 0 : Math.Max(0, limit - used), Percent = percent, WarnPercent = warn, Month = label,
            Reached = reached, Warning = !reached && ((limit > 0 && msgPct >= warn) || (budget > 0 && spendPct >= warn)),
            CostTemplate = costTemplate, CostText = costText, Budget = budget, Spent = spent, SpentPercent = spendPct,
            BudgetRemaining = budget == 0 ? 0 : Math.Max(0, budget - spent), TemplateCount = templates, TextCount = texts,
            DeliveredCount = rows.Count(r => r.Status is "delivered" or "read"), ReadCount = rows.Count(r => r.Status == "read"), FailedCount = rows.Count(r => r.Status == "failed"),
            LimitHit = limit > 0 && used >= limit, BudgetHit = budget > 0 && spent >= budget,
        };
    }

    public async Task SaveLimitAsync(WhatsAppLimitRequest r)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        await SetSettingAsync(KeyLimit, Math.Clamp(r.Limit, 0, 10_000_000).ToString(inv));
        await SetSettingAsync(KeyWarnPercent, Math.Clamp(r.WarnPercent, 1, 100).ToString(inv));
        await SetSettingAsync(KeyCostTemplate, Math.Clamp(r.CostTemplate, 0m, 10_000m).ToString(inv));
        await SetSettingAsync(KeyCostText, Math.Clamp(r.CostText, 0m, 10_000m).ToString(inv));
        await SetSettingAsync(KeyBudget, Math.Clamp(r.Budget, 0m, 100_000_000m).ToString(inv));
        // New allowances start a fresh round of warnings.
        await SetSettingAsync(KeyWarnedMonth, "");
        await SetSettingAsync(KeyReachedMonth, "");
        await db.SaveChangesAsync();
    }

    public async Task<bool> IsLimitReachedAsync() => (await GetLimitStatusAsync()).Reached;

    private void NotifyAdmins(string title, string body) =>
        db.Notifications.Add(new Notification { EmployeeId = null, Kind = "warning", Title = title, Body = body, Link = "/admin/whatsapp" });

    private static string UsageText(WhatsAppLimitDto s)
    {
        var parts = new List<string>();
        if (s.Limit > 0) parts.Add($"{s.Used} of {s.Limit} messages");
        if (s.Budget > 0) parts.Add($"₹{s.Spent:N2} of ₹{s.Budget:N2} budget");
        return string.Join(" and ", parts) + $" used for {s.Month}";
    }

    /// <summary>Tells every administrator once per month when usage (messages or rupees) passes the warning level, and once when a limit is hit.</summary>
    private async Task RaiseLimitNoticesAsync(WhatsAppLimitDto s)
    {
        if (s.Limit <= 0 && s.Budget <= 0) return;
        if (s.Reached && await GetSettingAsync(KeyReachedMonth) != s.Month)
        {
            var what = s.BudgetHit && s.LimitHit ? "message limit and budget reached" : s.BudgetHit ? "monthly budget reached" : "monthly limit reached";
            NotifyAdmins($"WhatsApp {what}", $"{UsageText(s)}. Sending is paused until next month or until you raise the allowance.");
            await SetSettingAsync(KeyReachedMonth, s.Month);
            await SetSettingAsync(KeyWarnedMonth, s.Month);
            await db.SaveChangesAsync();
        }
        else if (s.Warning && await GetSettingAsync(KeyWarnedMonth) != s.Month)
        {
            NotifyAdmins($"WhatsApp usage at {s.Percent}%", $"{UsageText(s)}. Sending pauses when the allowance runs out.");
            await SetSettingAsync(KeyWarnedMonth, s.Month);
            await db.SaveChangesAsync();
        }
    }

    /// <summary>One notification per ten minutes is enough when several messages fail together.</summary>
    private async Task NotifyFailureAsync(string title, string body)
    {
        var since = DateTime.UtcNow.AddMinutes(-10);
        if (await db.Notifications.AnyAsync(n => n.EmployeeId == null && n.Kind == "warning" && n.Title == title && n.CreatedAt >= since)) return;
        NotifyAdmins(title, body);
        await db.SaveChangesAsync();
    }

    private async Task<bool> WithinLimitAsync()
    {
        var s = await GetLimitStatusAsync();
        if (!s.Reached) return true;
        LimitReached = true;
        LastError = s.BudgetHit && !s.LimitHit
            ? $"The monthly WhatsApp budget of ₹{s.Budget:N2} has been used for {s.Month}. Sending resumes next month, or an administrator can raise the budget."
            : $"The monthly WhatsApp limit of {s.Limit} messages has been reached for {s.Month}. Sending resumes next month, or an administrator can raise the limit.";
        log.LogWarning("[WhatsApp] Monthly allowance reached ({Used} messages, ₹{Spent}); outbound skipped", s.Used, s.Spent);
        await RaiseLimitNoticesAsync(s);
        return false;
    }

    private async Task<bool> SendAsync(string phone, object payload, string type, string logBody, string? orderId, string? sentBy)
    {
        LastError = null; LimitReached = false;
        if (!_o.CanSend)
        {
            log.LogWarning("[WhatsApp] Access token / phone number id not configured — outbound skipped");
            LastError = "WhatsApp is not configured on the server (access token or phone number id missing).";
            return false;
        }
        if (!await WithinLimitAsync()) return false;

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

            var leadId = await leads.RecordOutboundAsync(phone);
            db.WhatsAppMessages.Add(new WhatsAppMessage
            {
                Direction = "outbound", WaMessageId = waId ?? $"failed-{Guid.NewGuid():N}", Phone = NormalizePhone(phone),
                Type = type, Body = logBody, Status = res.IsSuccessStatusCode ? "sent" : "failed", OrderId = orderId, LeadId = leadId, SentBy = sentBy ?? "system",
                RawPayload = res.IsSuccessStatusCode ? null : (text.Length > 4000 ? text[..4000] : text),
            });
            await db.SaveChangesAsync();
            if (res.IsSuccessStatusCode) await RaiseLimitNoticesAsync(await GetLimitStatusAsync());
            else
            {
                LastError = WhatsAppErrors.Friendly(text, (int)res.StatusCode);
                await NotifyFailureAsync("WhatsApp message failed to send", $"A message to +{NormalizePhone(phone)} was rejected: {LastError}");
            }
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "[WhatsApp] Send exception");
            LastError = "WhatsApp could not be reached. Check the server connection and try again.";
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
