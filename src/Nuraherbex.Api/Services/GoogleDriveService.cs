using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Nuraherbex.Api.Data;
using Nuraherbex.Api.Options;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Services;

/// <summary>
/// Google Drive integration via Google Cloud Service Account JWT + Drive v3 REST API.
/// Lists product videos/PDFs/catalogs from the configured root folder and generates shareable reader links for WhatsApp.
/// </summary>
public class GoogleDriveService(
    NuraDbContext db,
    HttpClient http,
    IMemoryCache cache,
    IOptionsMonitor<GoogleDriveOptions> defaults,
    ILogger<GoogleDriveService> log)
{
    public const string SettingsKey = "drive.settings";

    private sealed class StoredDriveSettings
    {
        public string ServiceAccountEmail { get; set; } = "";
        public string PrivateKeyPem { get; set; } = "";
        public string RootFolderId { get; set; } = "";
        public bool MakePublicOnShare { get; set; } = true;
    }

    private async Task<StoredDriveSettings> LoadConfigAsync()
    {
        var row = await db.SiteSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == SettingsKey);
        if (row is not null && !string.IsNullOrWhiteSpace(row.Value))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<StoredDriveSettings>(row.Value, NuraJson.Options);
                if (parsed is not null)
                {
                    return new StoredDriveSettings
                    {
                        ServiceAccountEmail = string.IsNullOrWhiteSpace(parsed.ServiceAccountEmail) ? defaults.CurrentValue.ServiceAccountEmail : parsed.ServiceAccountEmail,
                        PrivateKeyPem = string.IsNullOrWhiteSpace(parsed.PrivateKeyPem) ? defaults.CurrentValue.PrivateKeyPem : parsed.PrivateKeyPem,
                        RootFolderId = string.IsNullOrWhiteSpace(parsed.RootFolderId) ? defaults.CurrentValue.RootFolderId : parsed.RootFolderId,
                        MakePublicOnShare = parsed.MakePublicOnShare,
                    };
                }
            }
            catch { }
        }
        var d = defaults.CurrentValue;
        return new StoredDriveSettings
        {
            ServiceAccountEmail = d.ServiceAccountEmail,
            PrivateKeyPem = d.PrivateKeyPem,
            RootFolderId = d.RootFolderId,
            MakePublicOnShare = d.MakePublicOnShare,
        };
    }

    public async Task<DriveSettingsDto> GetSettingsAsync()
    {
        var cfg = await LoadConfigAsync();
        return new DriveSettingsDto
        {
            Success = true,
            Configured = !string.IsNullOrWhiteSpace(cfg.ServiceAccountEmail) && !string.IsNullOrWhiteSpace(cfg.PrivateKeyPem),
            ServiceAccountEmail = cfg.ServiceAccountEmail,
            HasPrivateKey = !string.IsNullOrWhiteSpace(cfg.PrivateKeyPem),
            RootFolderId = cfg.RootFolderId,
            MakePublicOnShare = cfg.MakePublicOnShare,
        };
    }

    public async Task<(bool Ok, string Message)> SaveSettingsAsync(DriveSettingsSaveRequest r)
    {
        var current = await LoadConfigAsync();
        var email = (r.ServiceAccountEmail ?? "").Trim();
        var key = (r.PrivateKeyPem ?? "").Trim();

        if (!string.IsNullOrWhiteSpace(r.ServiceAccountJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(r.ServiceAccountJson);
                if (doc.RootElement.TryGetProperty("client_email", out var em)) email = em.GetString()?.Trim() ?? email;
                if (doc.RootElement.TryGetProperty("private_key", out var pk)) key = pk.GetString()?.Trim() ?? key;
            }
            catch
            {
                return (false, "Service account JSON could not be parsed. Paste the complete JSON key file downloaded from Google Cloud.");
            }
        }

        if (string.IsNullOrWhiteSpace(email)) email = current.ServiceAccountEmail;
        if (string.IsNullOrWhiteSpace(key)) key = current.PrivateKeyPem;

        if (!string.IsNullOrWhiteSpace(key))
        {
            try
            {
                using var rsa = RSA.Create();
                rsa.ImportFromPem(key.Replace("\\n", "\n"));
            }
            catch
            {
                return (false, "The Private Key is not a valid PKCS#8 PEM RSA key.");
            }
        }

        var next = new StoredDriveSettings
        {
            ServiceAccountEmail = email,
            PrivateKeyPem = key.Replace("\\n", "\n"),
            RootFolderId = (r.RootFolderId ?? "").Trim(),
            MakePublicOnShare = r.MakePublicOnShare,
        };
        var json = JsonSerializer.Serialize(next, NuraJson.Options);
        var row = await db.SiteSettings.FirstOrDefaultAsync(s => s.Key == SettingsKey);
        if (row is null) db.SiteSettings.Add(new SiteSetting { Key = SettingsKey, Value = json });
        else { row.Value = json; row.UpdatedAt = DateTime.UtcNow; }
        await db.SaveChangesAsync();
        cache.Remove("drive:access_token");
        return (true, "Google Drive settings saved.");
    }

    private async Task<string> GetAccessTokenAsync(StoredDriveSettings cfg, CancellationToken ct)
    {
        if (cache.TryGetValue("drive:access_token", out string? cached) && !string.IsNullOrEmpty(cached))
            return cached;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var headerJson = "{\"alg\":\"RS256\",\"typ\":\"JWT\"}";
        var payloadJson = JsonSerializer.Serialize(new
        {
            iss = cfg.ServiceAccountEmail,
            scope = "https://www.googleapis.com/auth/drive",
            aud = "https://oauth2.googleapis.com/token",
            exp = now + 3600,
            iat = now,
        });

        string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var signingInput = B64(Encoding.UTF8.GetBytes(headerJson)) + "." + B64(Encoding.UTF8.GetBytes(payloadJson));

        using var rsa = RSA.Create();
        rsa.ImportFromPem(cfg.PrivateKeyPem.Replace("\\n", "\n"));
        var sig = rsa.SignData(Encoding.UTF8.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var assertion = signingInput + "." + B64(sig);

        using var form = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("grant_type", "urn:ietf:params:oauth:grant-type:jwt-bearer"),
            new KeyValuePair<string, string>("assertion", assertion),
        ]);
        using var res = await http.PostAsync("https://oauth2.googleapis.com/token", form, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            log.LogWarning("[Drive] OAuth token error {Status}: {Body}", (int)res.StatusCode, body);
            throw new InvalidOperationException("Google Drive authentication failed. Check the service account email and private key.");
        }
        using var doc = JsonDocument.Parse(body);
        var token = doc.RootElement.GetProperty("access_token").GetString() ?? "";
        var expires = doc.RootElement.TryGetProperty("expires_in", out var ex) ? Math.Max(60, ex.GetInt32() - 120) : 3000;
        cache.Set("drive:access_token", token, TimeSpan.FromSeconds(expires));
        return token;
    }

    public async Task<DriveListResponse> ListFilesAsync(string? query, string? kind, CancellationToken ct = default)
    {
        var cfg = await LoadConfigAsync();
        if (string.IsNullOrWhiteSpace(cfg.ServiceAccountEmail) || string.IsNullOrWhiteSpace(cfg.PrivateKeyPem))
        {
            return new DriveListResponse
            {
                Success = true,
                Configured = false,
                RootFolderId = cfg.RootFolderId,
                Message = "Google Drive is not configured yet. Add Service Account credentials in Drive settings.",
            };
        }

        try
        {
            var token = await GetAccessTokenAsync(cfg, ct);
            var filters = new List<string> { "trashed = false", "mimeType != 'application/vnd.google-apps.folder'" };
            if (!string.IsNullOrWhiteSpace(cfg.RootFolderId))
                filters.Add($"'{cfg.RootFolderId.Replace("'", "\\'")}' in parents");
            if (!string.IsNullOrWhiteSpace(query))
                filters.Add($"name contains '{query.Trim().Replace("'", "\\'")}'");
            if (kind == LibraryKinds.Video) filters.Add("mimeType contains 'video/'");
            else if (kind == LibraryKinds.Image) filters.Add("mimeType contains 'image/'");
            else if (kind == LibraryKinds.Pdf) filters.Add("mimeType = 'application/pdf'");

            var q = Uri.EscapeDataString(string.Join(" and ", filters));
            var url = $"https://www.googleapis.com/drive/v3/files?q={q}&pageSize=60&orderBy=modifiedTime desc&supportsAllDrives=true&includeItemsFromAllDrives=true&fields=files(id,name,mimeType,size,webViewLink,webContentLink,modifiedTime)";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var res = await http.SendAsync(req, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                log.LogWarning("[Drive] List failed {Status}: {Body}", (int)res.StatusCode, text);
                return new DriveListResponse { Success = false, Configured = true, RootFolderId = cfg.RootFolderId, Message = $"Google Drive returned error {(int)res.StatusCode}. Ensure the folder is shared with {cfg.ServiceAccountEmail}." };
            }

            using var doc = JsonDocument.Parse(text);
            var list = new List<DriveFileDto>();
            if (doc.RootElement.TryGetProperty("files", out var files))
            {
                foreach (var f in files.EnumerateArray())
                {
                    var id = f.GetProperty("id").GetString() ?? "";
                    var name = f.GetProperty("name").GetString() ?? "";
                    var mime = f.TryGetProperty("mimeType", out var m) ? (m.GetString() ?? "") : "";
                    var size = f.TryGetProperty("size", out var sz) && long.TryParse(sz.GetString(), out var parsedSize) ? parsedSize : 0;
                    var wvl = f.TryGetProperty("webViewLink", out var wv) ? wv.GetString() : $"https://drive.google.com/file/d/{id}/view?usp=sharing";
                    var wcl = f.TryGetProperty("webContentLink", out var wc) ? wc.GetString() : null;
                    DateTime? mod = f.TryGetProperty("modifiedTime", out var mt) && DateTime.TryParse(mt.GetString(), out var dt) ? dt.ToUniversalTime() : null;
                    list.Add(new DriveFileDto
                    {
                        Id = id, Name = name, MimeType = mime, Kind = MapKind(mime), Size = size,
                        WebViewLink = wvl, WebContentLink = wcl, ModifiedTime = mod,
                    });
                }
            }
            return new DriveListResponse { Success = true, Configured = true, RootFolderId = cfg.RootFolderId, Files = list };
        }
        catch (Exception ex)
        {
            return new DriveListResponse { Success = false, Configured = true, RootFolderId = cfg.RootFolderId, Message = ex.Message };
        }
    }

    public async Task<DriveShareResponse> EnsureShareLinkAsync(string fileId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fileId))
            return new DriveShareResponse { Success = false, Message = "Select a Drive file first." };

        var cfg = await LoadConfigAsync();
        if (string.IsNullOrWhiteSpace(cfg.ServiceAccountEmail) || string.IsNullOrWhiteSpace(cfg.PrivateKeyPem))
            return new DriveShareResponse { Success = false, Message = "Google Drive is not configured yet." };

        try
        {
            var token = await GetAccessTokenAsync(cfg, ct);
            if (cfg.MakePublicOnShare)
            {
                using var permReq = new HttpRequestMessage(HttpMethod.Post, $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(fileId)}/permissions?supportsAllDrives=true")
                {
                    Content = new StringContent("{\"role\":\"reader\",\"type\":\"anyone\"}", Encoding.UTF8, "application/json"),
                };
                permReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var permRes = await http.SendAsync(permReq, ct);
                if (!permRes.IsSuccessStatusCode)
                    log.LogInformation("[Drive] Could not set public reader permission on {FileId}: {Status}", fileId, (int)permRes.StatusCode);
            }

            using var metaReq = new HttpRequestMessage(HttpMethod.Get, $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(fileId)}?supportsAllDrives=true&fields=id,name,mimeType,size,webViewLink,webContentLink,modifiedTime");
            metaReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var metaRes = await http.SendAsync(metaReq, ct);
            var text = await metaRes.Content.ReadAsStringAsync(ct);
            if (!metaRes.IsSuccessStatusCode)
                return new DriveShareResponse { Success = false, Message = "Could not open that file on Google Drive." };

            using var doc = JsonDocument.Parse(text);
            var f = doc.RootElement;
            var id = f.GetProperty("id").GetString() ?? fileId;
            var name = f.GetProperty("name").GetString() ?? "Product file";
            var mime = f.TryGetProperty("mimeType", out var m) ? (m.GetString() ?? "") : "";
            var size = f.TryGetProperty("size", out var sz) && long.TryParse(sz.GetString(), out var ps) ? ps : 0;
            var shareUrl = (f.TryGetProperty("webViewLink", out var wv) ? wv.GetString() : null) ?? $"https://drive.google.com/file/d/{id}/view?usp=sharing";
            return new DriveShareResponse
            {
                Success = true,
                ShareUrl = shareUrl,
                File = new DriveFileDto { Id = id, Name = name, MimeType = mime, Kind = MapKind(mime), Size = size, WebViewLink = shareUrl },
            };
        }
        catch (Exception ex)
        {
            return new DriveShareResponse { Success = false, Message = ex.Message };
        }
    }

    private static string MapKind(string mime)
    {
        mime = (mime ?? "").ToLowerInvariant();
        if (mime.Contains("video/")) return LibraryKinds.Video;
        if (mime.Contains("image/")) return LibraryKinds.Image;
        if (mime == "application/pdf") return LibraryKinds.Pdf;
        return LibraryKinds.Document;
    }
}

