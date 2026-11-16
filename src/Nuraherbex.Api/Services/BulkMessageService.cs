using Microsoft.EntityFrameworkCore;
using Nuraherbex.Api.Data;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Services;

/// <summary>
/// 4-category WhatsApp bulk messaging: Customers, No Answer, Hot Leads, All Leads.
/// Previews audience + 24h window counts, queues campaigns, and delivers within WhatsApp limits.
/// </summary>
public class BulkMessageService(
    NuraDbContext db,
    WhatsAppService whatsapp,
    MessageTemplateService templates,
    IServiceScopeFactory scopes,
    ILogger<BulkMessageService> log)
{
    public IQueryable<Lead> QueryCategory(string category)
    {
        var q = db.Leads.AsNoTracking();
        return category switch
        {
            BulkCategories.Customers => q.Where(l => l.CustomerSince != null),
            BulkCategories.NoAnswer => q.Where(l => l.CustomerSince == null && l.Status != LeadStatus.NotInterested)
                .Where(LeadService.NoAnswerOf(db, true)),
            BulkCategories.HotLeads => q.Where(l => l.CustomerSince == null && l.Status == LeadStatus.Interested)
                .OrderByDescending(l => l.Priority).ThenByDescending(l => l.LastContactAt),
            _ => q.Where(l => l.CustomerSince == null && l.Status != LeadStatus.NotInterested),
        };
    }

    public async Task<BulkPreviewResponse> PreviewAsync(string category)
    {
        if (!BulkCategories.All.Contains(category)) category = BulkCategories.AllLeads;
        var now = FollowUpService.Clock();
        var matched = await QueryCategory(category)
            .Select(l => new { l.Id, l.Name, l.Phone, l.Status, l.ConsentStatus })
            .ToListAsync();

        var totalMatched = matched.Count;
        var optedOut = matched.Count(m => m.ConsentStatus == "opted_out");
        var eligible = matched.Where(m => m.ConsentStatus != "opted_out").ToList();
        var ids = eligible.Select(e => e.Id).ToList();
        var windows = ids.Count == 0
            ? new Dictionary<long, DateTime?>()
            : (await db.Conversations.AsNoTracking()
                .Where(c => ids.Contains(c.LeadId))
                .Select(c => new { c.LeadId, c.ServiceWindowExpiresAt })
                .ToListAsync())
                .GroupBy(c => c.LeadId)
                .ToDictionary(g => g.Key, g => g.Max(x => x.ServiceWindowExpiresAt));

        var inside = eligible.Count(e => windows.GetValueOrDefault(e.Id) is { } w && w > now);
        var outside = eligible.Count - inside;

        return new BulkPreviewResponse
        {
            Success = true,
            Category = category,
            TotalMatched = totalMatched,
            OptedOutExcluded = optedOut,
            EligibleCount = eligible.Count,
            InsideWindowCount = inside,
            OutsideWindowCount = outside,
            Sample = eligible.Take(10).Select(e => new BulkAudienceSampleDto
            {
                LeadId = e.Id,
                Name = e.Name,
                Phone = e.Phone,
                Status = e.Status.ToCode(),
                InsideWindow = windows.GetValueOrDefault(e.Id) is { } w && w > now,
            }).ToList(),
        };
    }

    public async Task<(BulkCampaignDto? Campaign, string? Error)> CreateAndStartAsync(BulkSendRequest req, string actor, bool runInBackground = true)
    {
        if (!BulkCategories.All.Contains(req.Category))
            return (null, "Choose Customers, No Answer, Hot Leads or All Leads.");

        var custom = string.IsNullOrWhiteSpace(req.CustomMessage) ? null : req.CustomMessage.Trim();
        var driveUrl = string.IsNullOrWhiteSpace(req.DriveFileUrl) ? null : req.DriveFileUrl.Trim();
        var tplName = string.IsNullOrWhiteSpace(req.TemplateName) ? null : req.TemplateName.Trim();

        if (custom is null && tplName is null && driveUrl is null)
            return (null, "Enter a message, select an approved template, or attach a Google Drive file link.");

        if (tplName is not null)
        {
            var def = MetaTemplates.Find(tplName);
            if (def is null) return (null, "Unknown Meta template.");
            if (!(await templates.GetAsync()).ApprovedMetaTemplates.Contains(def.Name))
                return (null, "That Meta template is not marked approved yet.");
        }

        var leads = await QueryCategory(req.Category).Where(l => l.ConsentStatus != "opted_out").ToListAsync();
        if (leads.Count == 0)
            return (null, "No eligible recipients (all matched leads are either opted out or empty).");

        var title = string.IsNullOrWhiteSpace(req.Title)
            ? $"{BulkCategories.Label(req.Category)} · {DateTime.UtcNow:dd MMM HH:mm}"
            : req.Title.Trim();

        var campaign = new BulkCampaign
        {
            Title = title[..Math.Min(title.Length, 200)],
            Category = req.Category,
            TemplateName = tplName,
            TemplateParams = req.TemplateParams ?? new(),
            CustomMessage = custom,
            DriveFileUrl = driveUrl,
            DriveFileName = string.IsNullOrWhiteSpace(req.DriveFileName) ? null : req.DriveFileName.Trim(),
            Status = "queued",
            TotalTarget = leads.Count,
            CreatedBy = actor,
        };
        db.BulkCampaigns.Add(campaign);
        await db.SaveChangesAsync();

        foreach (var l in leads)
        {
            db.BulkCampaignRecipients.Add(new BulkCampaignRecipient
            {
                CampaignId = campaign.Id,
                LeadId = l.Id,
                Name = l.Name,
                Phone = l.Phone,
                Status = "pending",
            });
        }
        await db.SaveChangesAsync();

        if (runInBackground)
        {
            var id = campaign.Id;
            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<BulkMessageService>().ProcessCampaignAsync(id);
                }
                catch (Exception ex) { log.LogWarning(ex, "[Bulk] Background campaign {Id} failed", id); }
            });
        }

        return (ToDto(campaign), null);
    }

    public async Task ProcessCampaignAsync(long campaignId)
    {
        var campaign = await db.BulkCampaigns.FirstOrDefaultAsync(c => c.Id == campaignId);
        if (campaign is null) return;
        campaign.Status = "running";
        await db.SaveChangesAsync();

        var recipients = await db.BulkCampaignRecipients.Where(r => r.CampaignId == campaignId && r.Status == "pending").OrderBy(r => r.Id).ToListAsync();
        var ids = recipients.Select(r => r.LeadId).ToList();
        var windows = await db.Conversations.AsNoTracking().Where(c => ids.Contains(c.LeadId)).ToDictionaryAsync(c => c.LeadId, c => c.ServiceWindowExpiresAt);
        var tplDef = campaign.TemplateName is not null ? MetaTemplates.Find(campaign.TemplateName) : null;
        var now = DateTime.UtcNow;

        foreach (var r in recipients)
        {
            var inside = windows.GetValueOrDefault(r.LeadId) is { } w && w > now;
            var first = LeadService.FirstName(r.Name, r.Phone);
            bool ok;

            if (inside && (!string.IsNullOrWhiteSpace(campaign.CustomMessage) || !string.IsNullOrWhiteSpace(campaign.DriveFileUrl)))
            {
                r.Mode = "text";
                var text = MessageDefaults.Fill(campaign.CustomMessage ?? "", first, campaign.CreatedBy);
                if (!string.IsNullOrWhiteSpace(campaign.DriveFileUrl))
                {
                    var label = string.IsNullOrWhiteSpace(campaign.DriveFileName) ? "Shared file" : campaign.DriveFileName;
                    text = (string.IsNullOrWhiteSpace(text) ? "" : text + "\n\n") + $"📎 {label}: {campaign.DriveFileUrl}";
                }
                ok = await whatsapp.SendTextAsync(r.Phone, text, sentBy: campaign.CreatedBy);
            }
            else if (tplDef is not null)
            {
                r.Mode = "template";
                var values = new List<string>();
                for (var i = 0; i < tplDef.Params.Length; i++)
                {
                    var p = tplDef.Params[i];
                    var v = p.Kind switch
                    {
                        "name" => first,
                        "agent" => campaign.CreatedBy,
                        "order" => "",
                        _ => i < campaign.TemplateParams.Count && !string.IsNullOrWhiteSpace(campaign.TemplateParams[i])
                            ? campaign.TemplateParams[i]
                            : (campaign.DriveFileUrl ?? p.Sample),
                    };
                    values.Add(string.IsNullOrWhiteSpace(v) ? "-" : v.Trim());
                }
                ok = await whatsapp.SendTemplateAsync(r.Phone, tplDef.Name, values, sentBy: campaign.CreatedBy, preview: tplDef.Filled(values));
            }
            else
            {
                r.Mode = "skipped";
                r.Status = "skipped";
                r.Error = "24h reply window closed and no approved template selected.";
                campaign.SkippedCount++;
                await db.SaveChangesAsync();
                continue;
            }

            if (ok)
            {
                r.Status = "sent";
                r.SentAt = DateTime.UtcNow;
                campaign.SentCount++;
            }
            else
            {
                if (whatsapp.LimitReached)
                {
                    r.Status = "skipped";
                    r.Error = whatsapp.LastError ?? "Monthly WhatsApp limit reached.";
                    campaign.SkippedCount++;
                    foreach (var rest in recipients.Where(x => x.Id > r.Id && x.Status == "pending"))
                    {
                        rest.Status = "skipped";
                        rest.Error = "Monthly WhatsApp limit reached.";
                        campaign.SkippedCount++;
                    }
                    campaign.Status = "stopped";
                    campaign.CompletedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                    return;
                }
                r.Status = "failed";
                r.Error = whatsapp.LastError ?? "Delivery failed";
                campaign.FailedCount++;
            }
            await db.SaveChangesAsync();
        }

        campaign.Status = "completed";
        campaign.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    public async Task<BulkCampaignListResponse> ListAsync() => new()
    {
        Success = true,
        Campaigns = (await db.BulkCampaigns.AsNoTracking().OrderByDescending(c => c.CreatedAt).Take(40).ToListAsync()).Select(ToDto).ToList(),
    };

    public async Task<BulkCampaignDetailResponse?> DetailAsync(long id)
    {
        var c = await db.BulkCampaigns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return null;
        var recips = await db.BulkCampaignRecipients.AsNoTracking().Where(r => r.CampaignId == id).OrderBy(r => r.Id).Take(300).ToListAsync();
        return new BulkCampaignDetailResponse
        {
            Success = true,
            Campaign = ToDto(c),
            Recipients = recips.Select(r => new BulkCampaignRecipientDto
            {
                Id = r.Id, LeadId = r.LeadId, Name = r.Name, Phone = r.Phone, Status = r.Status, Mode = r.Mode, Error = r.Error, SentAt = r.SentAt,
            }).ToList(),
        };
    }

    private static BulkCampaignDto ToDto(BulkCampaign c) => new()
    {
        Id = c.Id, Title = c.Title, Category = c.Category, TemplateName = c.TemplateName, CustomMessage = c.CustomMessage,
        DriveFileUrl = c.DriveFileUrl, DriveFileName = c.DriveFileName, Status = c.Status, TotalTarget = c.TotalTarget,
        SentCount = c.SentCount, FailedCount = c.FailedCount, SkippedCount = c.SkippedCount, CreatedBy = c.CreatedBy,
        CreatedAt = c.CreatedAt, CompletedAt = c.CompletedAt,
    };
}

