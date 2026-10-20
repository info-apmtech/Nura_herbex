using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nuraherbex.Api.Data;

namespace Nuraherbex.Api.Services;

/// <summary>
/// Photos and stickers received before media support was added were stored as "[sticker]" text, but the original WhatsApp
/// payload (with the media id) was kept. Re-attach the ids so those messages can show their file when Meta still has it
/// (about 30 days). Runs at startup, touches only rows that still lack a media id, and never fails the app.
/// </summary>
public static class WhatsAppMediaBackfill
{
    public static async Task RunAsync(NuraDbContext db, ILogger log)
    {
        try
        {
            var rows = await db.WhatsAppMessages
                .Where(m => m.Direction == "inbound" && m.MediaId == null && m.RawPayload != null
                    && (m.Type == "image" || m.Type == "sticker" || m.Type == "video" || m.Type == "audio" || m.Type == "document"))
                .OrderByDescending(m => m.Id).Take(500).ToListAsync();
            var fixedCount = 0;
            foreach (var m in rows)
            {
                try
                {
                    using var doc = JsonDocument.Parse(m.RawPayload!);
                    if (!doc.RootElement.TryGetProperty(m.Type, out var media) || media.ValueKind != JsonValueKind.Object) continue;
                    var id = media.TryGetProperty("id", out var i) ? i.GetString() : null;
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    m.MediaId = id;
                    m.MediaMime ??= media.TryGetProperty("mime_type", out var mm) ? mm.GetString() : null;
                    m.MediaName ??= media.TryGetProperty("filename", out var fn) ? fn.GetString() : null;
                    if (m.Body is { } b && b == $"[{m.Type}]") m.Body = null; // the old placeholder text, not a real caption
                    fixedCount++;
                }
                catch (JsonException) { }
            }
            if (fixedCount > 0) { await db.SaveChangesAsync(); log.LogInformation("[WhatsApp] Re-linked {Count} earlier media message(s)", fixedCount); }
        }
        catch (Exception ex) { log.LogWarning(ex, "[WhatsApp] Media backfill skipped"); }
    }
}
