using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nuraherbex.Api.Data;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Services;

/// <summary>
/// The message library: automatic replies (after hours, greetings, delivered, customer welcome) and ready-made replies per stage.
/// Stored as one JSON setting. Automatic messages always exist (missing ones are filled from the defaults); only their text and on/off can change.
/// </summary>
public class MessageTemplateService(NuraDbContext db)
{
    private const string Key = "messages.library", LegacyAfterHoursKey = "leads.afterhours";
    private const int MaxBody = 1000, MaxReplies = 60;

    private sealed class Legacy { public bool Enabled { get; set; } = true; public string? Message { get; set; } }

    public async Task<MessageTemplatesSettings> GetAsync()
    {
        var row = await db.SiteSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == Key);
        MessageTemplatesSettings? saved = null;
        if (row is not null)
        {
            try { saved = JsonSerializer.Deserialize<MessageTemplatesSettings>(row.Value, NuraJson.Options); }
            catch (JsonException) { saved = null; }
        }

        if (saved is null)
        {
            // First use: start from the defaults, keeping what the company set for the old single after-hours message.
            saved = new MessageTemplatesSettings { Items = [.. MessageDefaults.Automatic(), .. MessageDefaults.Replies()] };
            var old = await db.SiteSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == LegacyAfterHoursKey);
            if (old is not null)
            {
                try
                {
                    var l = JsonSerializer.Deserialize<Legacy>(old.Value, NuraJson.Options);
                    if (l is not null)
                    {
                        saved.AfterHoursEnabled = l.Enabled;
                        // A message that was changed on purpose is kept for leads; the old standard text offered tracking, which leads should not get.
                        if (!string.IsNullOrWhiteSpace(l.Message) && l.Message.Trim() != MessageDefaults.OldAfterHoursText)
                            saved.Items.First(i => i.Key == MessageKeys.AfterHoursLead).Body = l.Message.Trim();
                    }
                }
                catch (JsonException) { /* ignore a damaged legacy value */ }
            }
            return saved;
        }

        // Make sure every automatic message is there and carries its fixed labels.
        var defaults = MessageDefaults.Automatic();
        foreach (var d in defaults)
        {
            var have = saved.Items.FirstOrDefault(i => i.Key == d.Key);
            if (have is null) { saved.Items.Add(d); continue; }
            have.Group = "auto"; have.Stage = d.Stage; have.Title = d.Title; have.Hint = d.Hint;
            if (string.IsNullOrWhiteSpace(have.Body)) have.Body = d.Body;
        }
        saved.Items = [.. saved.Items.Where(i => i.Group == "auto" ? defaults.Any(d => d.Key == i.Key) : true)];
        return saved;
    }

    /// <summary>Returns an error message, or null when saved.</summary>
    public async Task<string?> SaveAsync(MessageTemplatesSettings r)
    {
        var defaults = MessageDefaults.Automatic();
        var items = new List<MessageTemplateDto>();

        foreach (var d in defaults)
        {
            var sent = r.Items.FirstOrDefault(i => i.Key == d.Key && i.Group == "auto");
            var body = (sent?.Body ?? d.Body).Trim();
            if (body.Length == 0) return $"Write the text for “{d.Title}”.";
            if (body.Length > MaxBody) return $"“{d.Title}” is too long ({MaxBody} characters at most).";
            items.Add(new MessageTemplateDto { Key = d.Key, Group = "auto", Stage = d.Stage, Title = d.Title, Hint = d.Hint, Body = body, Enabled = sent?.Enabled ?? true });
        }

        var replies = r.Items.Where(i => i.Group == "reply").ToList();
        if (replies.Count > MaxReplies) return $"Keep it to {MaxReplies} ready-made replies or fewer.";
        foreach (var t in replies)
        {
            var title = (t.Title ?? "").Trim(); var body = (t.Body ?? "").Trim();
            if (title.Length == 0 || body.Length == 0) return "Every ready-made reply needs a title and a message.";
            if (title.Length > 80) return $"The title “{title[..20]}…” is too long (80 characters at most).";
            if (body.Length > MaxBody) return $"“{title}” is too long ({MaxBody} characters at most).";
            if (!MessageStages.All.Contains(t.Stage)) return $"“{title}” has an unknown stage.";
            items.Add(new MessageTemplateDto { Key = string.IsNullOrWhiteSpace(t.Key) ? "reply-" + Guid.NewGuid().ToString("N")[..8] : t.Key, Group = "reply", Stage = t.Stage, Title = title, Body = body, Enabled = true });
        }

        var approved = r.ApprovedMetaTemplates.Where(n => MetaTemplates.Find(n) is not null).Distinct().ToList();
        var json = JsonSerializer.Serialize(new MessageTemplatesSettings { AfterHoursEnabled = r.AfterHoursEnabled, Items = items, ApprovedMetaTemplates = approved }, NuraJson.Options);
        var row = await db.SiteSettings.FirstOrDefaultAsync(s => s.Key == Key);
        if (row is null) db.SiteSettings.Add(new SiteSetting { Key = Key, Value = json });
        else { row.Value = json; row.UpdatedAt = DateTime.UtcNow; }
        await db.SaveChangesAsync();
        return null;
    }

    /// <summary>One automatic message, or null when it is switched off.</summary>
    public async Task<string?> BodyAsync(string key)
    {
        var all = await GetAsync();
        var t = all.Items.FirstOrDefault(i => i.Key == key);
        return t is { Enabled: true } && !string.IsNullOrWhiteSpace(t.Body) ? t.Body : null;
    }
}
