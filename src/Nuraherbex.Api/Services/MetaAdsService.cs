using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nuraherbex.Api.Data;
using Nuraherbex.Api.Options;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Services;

/// <summary>
/// Copies daily ad numbers (campaign, ad set and ad names, spend, reach) from the Meta Marketing API into <c>ad_insights_daily</c>.
/// Optional: with no connection (entered in the app, or MetaAds settings on the server) the Ad campaigns report simply works from the referral details on each lead.
/// The access token is sent in a header and never written to logs or errors; when it is entered in the app it is stored encrypted.
/// </summary>
public class MetaAdsService(HttpClient http, NuraDbContext db, IOptionsMonitor<MetaAdsOptions> options, ILogger<MetaAdsService> log, IOptions<JwtOptions>? jwt = null)
{
    private const string StatusKey = "metaads.status", ConnectionKey = "metaads.connection";
    private const string Fields = "ad_id,ad_name,adset_id,adset_name,campaign_id,campaign_name,spend,impressions,clicks,actions,date_start";
    private const int MaxPages = 200; // 200 pages x 500 rows: far beyond any real account, only a guard against a paging loop
    /// <summary>Automatic syncs only re-read the last days (Meta restates recent numbers); a full window is re-read every few hours.</summary>
    private const int QuickDays = 3;
    private static readonly TimeSpan FullEvery = TimeSpan.FromHours(6);

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static volatile bool _running;

    public sealed record SyncResult(bool Ok, string Message, int Rows = 0);

    private sealed class SavedStatus
    {
        public DateTime? LastSyncAt { get; set; }
        public string? LastError { get; set; }
        public DateTime? LastAttemptAt { get; set; }
        public DateTime? LastFullAt { get; set; }
        public string? AccountName { get; set; }
    }

    private sealed class SavedConnection
    {
        public string AdAccountId { get; set; } = "";
        public string ProtectedToken { get; set; } = "";
        public string ApiVersion { get; set; } = "";
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public string UpdatedBy { get; set; } = "";
    }

    // ---- connection (entered in the app; the token is stored encrypted) ---------------------------

    private byte[] Key => System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("meta-ads-token:" + (jwt?.Value.Secret is { Length: > 0 } s ? s : "nura-local")));

    /// <summary>AES-GCM with a key derived from the server's signing secret: a copy of the database alone does not reveal the token.</summary>
    private string Protect(string plain)
    {
        var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);
        var data = System.Text.Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[data.Length]; var tag = new byte[16];
        using (var aes = new System.Security.Cryptography.AesGcm(Key, 16)) aes.Encrypt(nonce, data, cipher, tag);
        return "v1:" + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    /// <summary>The token, or null when it cannot be read (for example the signing secret was changed): the person is asked to enter it again.</summary>
    private string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith("v1:", StringComparison.Ordinal)) return null;
        try
        {
            var raw = Convert.FromBase64String(stored[3..]);
            if (raw.Length < 28) return null;
            var plain = new byte[raw.Length - 28];
            using var aes = new System.Security.Cryptography.AesGcm(Key, 16);
            aes.Decrypt(raw.AsSpan(0, 12), raw.AsSpan(28), raw.AsSpan(12, 16), plain);
            return System.Text.Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException) { return null; }
    }

    private async Task<SavedConnection?> ReadConnectionAsync()
    {
        var row = await db.SiteSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == ConnectionKey);
        if (row is null) return null;
        try { return JsonSerializer.Deserialize<SavedConnection>(row.Value, NuraJson.Options); }
        catch (JsonException) { return null; }
    }

    private static string Pick(string? saved, string fallback) => string.IsNullOrWhiteSpace(saved) ? fallback : saved.Trim();

    /// <summary>What is in effect: what was saved in the app wins, blank values fall back to the server settings.</summary>
    public async Task<MetaAdsOptions> EffectiveAsync()
    {
        var o = options.CurrentValue;
        var saved = await ReadConnectionAsync();
        if (saved is null) return o;
        return new MetaAdsOptions
        {
            AdAccountId = Pick(saved.AdAccountId, o.AdAccountId), AccessToken = Pick(Unprotect(saved.ProtectedToken), o.AccessToken), ApiVersion = Pick(saved.ApiVersion, o.ApiVersion),
            SyncDays = o.SyncDays, WorkerEnabled = o.WorkerEnabled, SyncMinutes = o.SyncMinutes,
        };
    }

    public async Task<bool> IsConfiguredAsync() => (await EffectiveAsync()).IsConfigured;

    public static int ClampMinutes(int minutes) => Math.Clamp(minutes <= 0 ? 10 : minutes, 5, 360);

    public async Task<int> IntervalMinutesAsync() => ClampMinutes((await EffectiveAsync()).SyncMinutes);

    public async Task<MetaConnectionDto> ConnectionAsync()
    {
        var eff = await EffectiveAsync();
        var saved = await ReadConnectionAsync();
        var token = eff.AccessToken.Trim();
        var fromApp = saved is not null && !string.IsNullOrWhiteSpace(saved.AdAccountId) && Unprotect(saved.ProtectedToken) is not null;
        return new MetaConnectionDto
        {
            AdAccountId = eff.AdAccountId.Trim().Replace("act_", "", StringComparison.OrdinalIgnoreCase), ApiVersion = eff.ApiVersion, HasToken = token.Length > 0,
            TokenHint = token.Length >= 4 ? token[^4..] : "", Source = !eff.IsConfigured ? "none" : fromApp ? "app" : "server",
            UpdatedAt = saved?.UpdatedAt, AccountName = (await ReadStatusAsync()).AccountName,
        };
    }

    /// <summary>Saves the ad account and token entered in the app. A blank token keeps the saved one. Returns an error message when something is wrong.</summary>
    public async Task<string?> SaveConnectionAsync(MetaConnectionSaveRequest r, string by)
    {
        var account = new string((r.AdAccountId ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());
        account = account.StartsWith("act_", StringComparison.OrdinalIgnoreCase) ? account[4..] : account;
        if (account.Length < 5 || account.Length > 24 || !account.All(char.IsDigit)) return "The ad account ID is the number shown in Ads Manager (digits only, for example 1234567890123456).";
        var version = string.IsNullOrWhiteSpace(r.ApiVersion) ? "v21.0" : r.ApiVersion.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(version, @"^v\d{1,2}\.\d{1,2}$")) return "The API version looks like v21.0.";

        var saved = await ReadConnectionAsync();
        var token = (r.AccessToken ?? "").Trim();
        string protectedToken;
        if (token.Length > 0)
        {
            if (token.Length < 20 || token.Any(char.IsWhiteSpace)) return "That does not look like an access token. Paste the whole token, with no spaces.";
            protectedToken = Protect(token);
        }
        else if (saved is not null && Unprotect(saved.ProtectedToken) is not null) protectedToken = saved.ProtectedToken;
        else if (!string.IsNullOrWhiteSpace(options.CurrentValue.AccessToken)) protectedToken = ""; // keeps using the server's token
        else return "Paste the access token.";

        var json = JsonSerializer.Serialize(new SavedConnection { AdAccountId = account, ProtectedToken = protectedToken, ApiVersion = version, UpdatedAt = DateTime.UtcNow, UpdatedBy = by }, NuraJson.Options);
        var row = await db.SiteSettings.FirstOrDefaultAsync(s => s.Key == ConnectionKey);
        if (row is null) db.SiteSettings.Add(new SiteSetting { Key = ConnectionKey, Value = json });
        else { row.Value = json; row.UpdatedAt = DateTime.UtcNow; }
        await db.SaveChangesAsync();
        log.LogInformation("[MetaAds] {Who} saved the Meta connection for ad account {Account}", by, account);
        return null;
    }

    /// <summary>Removes what was saved in the app (server settings, if any, apply again) and the account name that was learned from it.</summary>
    public async Task ClearConnectionAsync()
    {
        var row = await db.SiteSettings.FirstOrDefaultAsync(s => s.Key == ConnectionKey);
        if (row is not null) { db.SiteSettings.Remove(row); await db.SaveChangesAsync(); }
        var st = await ReadStatusAsync();
        st.AccountName = null; st.LastError = null;
        await WriteStatusAsync(st);
    }

    /// <summary>Asks Meta for the ad account with the saved details, so a wrong ID or token is found before the first sync.</summary>
    public async Task<(bool Ok, string Message, string? AccountName)> TestAsync(CancellationToken ct = default)
    {
        var o = await EffectiveAsync();
        if (!o.IsConfigured) return (false, "Enter the ad account ID and the access token first.", null);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://graph.facebook.com/{o.ApiVersion.Trim()}/{o.AccountPath}?fields=name,currency,account_status");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.AccessToken.Trim());
            using var res = await http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) return (false, Redact(MetaError(body, (int)res.StatusCode), o.AccessToken), null);
            using var doc = JsonDocument.Parse(body);
            var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var currency = doc.RootElement.TryGetProperty("currency", out var c) ? c.GetString() ?? "" : "";
            var state = doc.RootElement.TryGetProperty("account_status", out var a) && a.TryGetInt32(out var code)
                ? code switch { 1 => "active", 2 => "disabled", 3 => "has an unsettled balance", 7 => "under risk review", 9 => "in a grace period", 101 => "closed", _ => $"status {code}" }
                : "active";
            var label = string.IsNullOrWhiteSpace(name) ? o.AccountPath : name;
            var st = await ReadStatusAsync();
            st.AccountName = Cut(label, 120);
            await WriteStatusAsync(st);
            return (true, $"Connected to “{label}”{(currency.Length > 0 ? $" ({currency})" : "")}. The account is {state}.", label);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return (false, "Meta could not be reached: " + Redact(ex.Message, o.AccessToken), null); }
    }

    // ---- status -------------------------------------------------------------------------------

    public async Task<AdsStatusResponse> StatusAsync()
    {
        var saved = await ReadStatusAsync();
        var eff = await EffectiveAsync();
        var conn = await ConnectionAsync();
        var rows = await db.AdInsights.AsNoTracking().CountAsync();
        var first = rows == 0 ? (DateTime?)null : await db.AdInsights.AsNoTracking().MinAsync(x => x.Date);
        var last = rows == 0 ? (DateTime?)null : await db.AdInsights.AsNoTracking().MaxAsync(x => x.Date);
        var minutes = ClampMinutes(eff.SyncMinutes);
        DateTime? next = null;
        if (eff.IsConfigured && eff.WorkerEnabled)
        {
            next = (saved.LastAttemptAt ?? saved.LastSyncAt ?? DateTime.UtcNow).AddMinutes(minutes);
            if (next < DateTime.UtcNow) next = DateTime.UtcNow.AddSeconds(30); // overdue: the worker picks it up on its next pass
        }
        return new AdsStatusResponse
        {
            Success = true, Configured = eff.IsConfigured, LastSyncAt = saved.LastSyncAt, LastAttemptAt = saved.LastAttemptAt, LastError = saved.LastError, Rows = rows,
            FirstDate = first?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), LastDate = last?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Syncing = _running, IntervalMinutes = minutes, NextSyncAt = next, AdAccountId = conn.AdAccountId, AccountName = saved.AccountName, Source = conn.Source,
        };
    }

    private async Task<SavedStatus> ReadStatusAsync()
    {
        var row = await db.SiteSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == StatusKey);
        if (row is null) return new SavedStatus();
        try { return JsonSerializer.Deserialize<SavedStatus>(row.Value, NuraJson.Options) ?? new SavedStatus(); }
        catch (JsonException) { return new SavedStatus(); }
    }

    private async Task WriteStatusAsync(SavedStatus s)
    {
        var json = JsonSerializer.Serialize(s, NuraJson.Options);
        var row = await db.SiteSettings.FirstOrDefaultAsync(x => x.Key == StatusKey);
        if (row is null) db.SiteSettings.Add(new SiteSetting { Key = StatusKey, Value = json });
        else { row.Value = json; row.UpdatedAt = DateTime.UtcNow; }
        await db.SaveChangesAsync();
    }

    // ---- sync ---------------------------------------------------------------------------------

    /// <summary>
    /// Reads ad numbers from Meta and stores them, replacing what was stored for those days. <paramref name="full"/>: true reads the whole window
    /// (MetaAds:SyncDays), false only the last few days, null lets the schedule decide (a full read every few hours, quick ones in between).
    /// Only one sync runs at a time; a second request while one is running is refused politely.
    /// </summary>
    public async Task<SyncResult> SyncAsync(CancellationToken ct = default, bool? full = true)
    {
        var o = await EffectiveAsync();
        if (!o.IsConfigured) return new SyncResult(false, "Meta is not connected. Enter the ad account ID and access token first.");
        if (!await Gate.WaitAsync(0, ct)) return new SyncResult(false, "A sync is already running. Give it a minute.");
        _running = true;
        try
        {
            var before = await ReadStatusAsync();
            var attempt = DateTime.UtcNow;
            var haveRows = await db.AdInsights.AsNoTracking().AnyAsync(ct);
            var doFull = full ?? (!haveRows || before.LastFullAt is null || attempt - before.LastFullAt > FullEvery);
            var until = FollowUpService.IstNow.Date;
            var since = until.AddDays(-(doFull ? Math.Clamp(o.SyncDays, 1, 400) : QuickDays));
            try
            {
                var rows = await FetchAsync(o, since, until, ct);
                await UpsertAsync(rows, since, until, ct);
                before.LastSyncAt = attempt; before.LastAttemptAt = attempt; before.LastError = null; if (doFull) before.LastFullAt = attempt;
                await WriteStatusAsync(before);
                log.LogInformation("[MetaAds] Synced {Rows} ad-day row(s) for {From:yyyy-MM-dd}..{To:yyyy-MM-dd}", rows.Count, since, until);
                return new SyncResult(true, $"Synced {rows.Count:N0} ad-day rows from Meta{(doFull ? "" : " (last " + QuickDays + " days)")}.", rows.Count);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                var msg = Redact(ex.Message, o.AccessToken);
                log.LogWarning("[MetaAds] Sync failed: {Message}", msg);
                try { before.LastAttemptAt = attempt; before.LastError = msg; await WriteStatusAsync(before); } catch { /* the failure itself is already logged */ }
                return new SyncResult(false, msg);
            }
        }
        finally { _running = false; Gate.Release(); }
    }

    private sealed record Row(DateTime Date, string AdId, string AdName, string AdSetId, string AdSetName, string CampaignId, string CampaignName, decimal Spend, long Impressions, long Clicks, long Conversations);

    private async Task<List<Row>> FetchAsync(MetaAdsOptions o, DateTime since, DateTime until, CancellationToken ct)
    {
        var range = Uri.EscapeDataString($"{{\"since\":\"{since:yyyy-MM-dd}\",\"until\":\"{until:yyyy-MM-dd}\"}}");
        var url = $"https://graph.facebook.com/{o.ApiVersion.Trim()}/{o.AccountPath}/insights?level=ad&time_increment=1&fields={Fields}&time_range={range}&limit=500";
        var all = new List<Row>();
        for (var page = 0; url is not null && page < MaxPages; page++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.AccessToken.Trim());
            using var res = await http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) throw new InvalidOperationException(MetaError(body, (int)res.StatusCode));

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                foreach (var d in data.EnumerateArray()) if (ParseRow(d) is { } r) all.Add(r);
            url = doc.RootElement.TryGetProperty("paging", out var paging) && paging.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
        }
        return all;
    }

    private static Row? ParseRow(JsonElement d)
    {
        string S(string name) => d.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        long L(string name) => long.TryParse(S(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
        var adId = S("ad_id");
        if (adId.Length == 0 || !DateTime.TryParseExact(S("date_start"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return null;
        var spend = decimal.TryParse(S("spend"), NumberStyles.Number, CultureInfo.InvariantCulture, out var sp) ? sp : 0m;

        long conversations = 0;
        if (d.TryGetProperty("actions", out var actions) && actions.ValueKind == JsonValueKind.Array)
            foreach (var a in actions.EnumerateArray())
                if (a.TryGetProperty("action_type", out var t) && (t.GetString() ?? "").StartsWith("onsite_conversion.messaging_conversation_started", StringComparison.Ordinal)
                    && a.TryGetProperty("value", out var v) && long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                    conversations += n;

        return new Row(date.Date, adId, Cut(S("ad_name"), 300), S("adset_id"), Cut(S("adset_name"), 300), S("campaign_id"), Cut(S("campaign_name"), 300), spend, L("impressions"), L("clicks"), conversations);
    }

    private async Task UpsertAsync(List<Row> rows, DateTime since, DateTime until, CancellationToken ct)
    {
        var existing = await db.AdInsights.Where(x => x.Date >= since && x.Date <= until).ToListAsync(ct);
        var byKey = existing.ToDictionary(x => (x.Date.Date, x.AdId));
        var now = DateTime.UtcNow;
        foreach (var r in rows)
        {
            if (!byKey.TryGetValue((r.Date, r.AdId), out var e)) { e = new AdInsightDaily { Date = r.Date, AdId = r.AdId }; db.AdInsights.Add(e); byKey[(r.Date, r.AdId)] = e; }
            e.AdName = r.AdName; e.AdSetId = r.AdSetId; e.AdSetName = r.AdSetName; e.CampaignId = r.CampaignId; e.CampaignName = r.CampaignName;
            e.Spend = r.Spend; e.Impressions = r.Impressions; e.Clicks = r.Clicks; e.Conversations = r.Conversations; e.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>Meta's own error text (never the request URL), with the token removed in case it was echoed.</summary>
    private static string MetaError(string body, int status)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err) && err.TryGetProperty("message", out var m) && m.GetString() is { Length: > 0 } text)
                return $"Meta refused the request ({status}): {text}";
        }
        catch (JsonException) { /* not JSON */ }
        return $"Meta refused the request ({status}).";
    }

    public static string Redact(string text, string token) =>
        string.IsNullOrWhiteSpace(token) ? text : text.Replace(token.Trim(), "***", StringComparison.Ordinal);
}


/// <summary>
/// Syncs ad numbers from Meta every few minutes (MetaAds:SyncMinutes, 10 by default) while a connection exists, whether it was entered in the app or set on the server.
/// In between full reads it only re-reads the last few days, so a short interval stays light. Set MetaAds:WorkerEnabled=false on any other process sharing the database.
/// </summary>
public sealed class MetaAdsWorker(IServiceScopeFactory scopes, IOptionsMonitor<MetaAdsOptions> options, ILogger<MetaAdsWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.CurrentValue.WorkerEnabled) { log.LogInformation("[MetaAds] Worker disabled by configuration"); return; }
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            var minutes = 10;
            try
            {
                using var scope = scopes.CreateScope();
                var meta = scope.ServiceProvider.GetRequiredService<MetaAdsService>();
                if (await meta.IsConfiguredAsync()) await meta.SyncAsync(stoppingToken, full: null);
                minutes = await meta.IntervalMinutesAsync();
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { log.LogWarning("[MetaAds] Cycle failed: {Type}", ex.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromMinutes(minutes), stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }
}
