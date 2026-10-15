using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nuraherbex.Api.Data;
using Nuraherbex.Api.Options;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Services;

/// <summary>
/// CRM lead capture. One lead per normalised phone number; repeat enquiries append a source event and
/// refresh the conversation instead of creating a duplicate.
/// </summary>
public class LeadService(NuraDbContext db, IOptions<WhatsAppOptions> options, ILogger<LeadService> log)
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase) { "stop", "unsubscribe", "stop all", "opt out", "optout" };
    private static readonly HashSet<string> StartWords = new(StringComparer.OrdinalIgnoreCase) { "start", "subscribe", "opt in", "optin" };

    /// <summary>E.164 digits without '+': 9876543210 → 919876543210, 09876543210 → 919876543210.</summary>
    public string NormalizePhone(string? phone)
    {
        var digits = new string((phone ?? "").Where(char.IsDigit).ToArray());
        var cc = options.Value.DefaultCountryCode;
        if (digits.Length == 10) return cc + digits;
        if (digits.Length == 11 && digits[0] == '0') return cc + digits[1..];
        return digits;
    }

    public static bool IsStopKeyword(string? text) => text is not null && StopWords.Contains(text.Trim());
    public static bool IsStartKeyword(string? text) => text is not null && StartWords.Contains(text.Trim());

    public sealed record InboundResult(Lead Lead, bool Created);

    /// <summary>Creates or updates the lead for an inbound WhatsApp message and saves. Safe against concurrent duplicate webhooks.</summary>
    public async Task<InboundResult> UpsertFromWhatsAppAsync(string phone, string? profileName, string? body, string? referralJson, string? referralDetail)
    {
        var norm = NormalizePhone(phone);
        var now = DateTime.UtcNow;

        var lead = await db.Leads.FirstOrDefaultAsync(l => l.Phone == norm);
        var created = false;
        if (lead is null)
        {
            lead = new Lead
            {
                Phone = norm, Name = string.IsNullOrWhiteSpace(profileName) ? norm : profileName.Trim(),
                Source = "whatsapp", SourceDetail = referralDetail, FirstMessage = Truncate(body, 1000),
                CreatedAt = now, UpdatedAt = now, LastContactAt = now,
            };
            db.Leads.Add(lead);
            try
            {
                await db.SaveChangesAsync();
                created = true;
            }
            catch (DbUpdateException)
            {
                // Another webhook delivery inserted the same phone first — fall back to that row.
                db.Entry(lead).State = EntityState.Detached;
                lead = await db.Leads.FirstAsync(l => l.Phone == norm);
            }
        }

        if (created)
        {
            db.LeadStatusHistory.Add(new LeadStatusHistory { LeadId = lead.Id, ToStatus = LeadStatuses.New, Note = "Lead captured from WhatsApp", CreatedAt = now });
            log.LogInformation("[Lead] New WhatsApp lead {Id} ({Phone})", lead.Id, norm);
        }
        else
        {
            lead.LastContactAt = now;
            lead.UpdatedAt = now;
            // Replace a placeholder name (the bare number) once WhatsApp shows the real profile name.
            if (!string.IsNullOrWhiteSpace(profileName) && lead.Name == lead.Phone) lead.Name = profileName.Trim();
            if (lead.SourceDetail is null && referralDetail is not null) lead.SourceDetail = referralDetail;
        }

        // Keep attribution: the first contact, and any later Click-to-WhatsApp ad referral.
        if (created || referralJson is not null)
            db.LeadSourceEvents.Add(new LeadSourceEvent { LeadId = lead.Id, Source = "whatsapp", Detail = referralDetail, ReferralJson = referralJson, CreatedAt = now });

        if (IsStopKeyword(body)) SetConsent(lead, "opted_out", now);
        else if (IsStartKeyword(body) && lead.ConsentStatus != "opted_in") SetConsent(lead, "opted_in", now);

        // New leads (and older ones nobody owns) go to the next sales employee in the rotation.
        await AutoAssignAsync(lead);

        var convo = await db.Conversations.FirstOrDefaultAsync(c => c.LeadId == lead.Id);
        if (convo is null) { convo = new Conversation { LeadId = lead.Id }; db.Conversations.Add(convo); }
        convo.LastInboundAt = now;
        convo.ServiceWindowExpiresAt = now.AddHours(24);
        convo.UnreadCount++;

        await db.SaveChangesAsync();
        return new InboundResult(lead, created);
    }


    // ---------------------------------------------------------------------
    // Assignment
    // ---------------------------------------------------------------------

    public const string AutoAssignKey = "leads.autoAssign";

    /// <summary>Round-robin is on unless an admin switched it off.</summary>
    public async Task<bool> IsAutoAssignEnabledAsync() =>
        !string.Equals((await db.SiteSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == AutoAssignKey))?.Value, "false", StringComparison.OrdinalIgnoreCase);

    public async Task SetAutoAssignAsync(bool enabled)
    {
        var row = await db.SiteSettings.FirstOrDefaultAsync(s => s.Key == AutoAssignKey);
        if (row is null) db.SiteSettings.Add(new SiteSetting { Key = AutoAssignKey, Value = enabled ? "true" : "false" });
        else { row.Value = enabled ? "true" : "false"; row.UpdatedAt = DateTime.UtcNow; }
        await db.SaveChangesAsync();
    }

    /// <summary>The active sales employee who was given a lead the longest ago (never-assigned first), excluding <paramref name="excludeId"/>.</summary>
    public Task<Employee?> PickNextEmployeeAsync(long? excludeId = null) =>
        db.Employees.Where(e => e.IsActive && e.Role == EmployeeRoles.Sales && e.Id != excludeId)
            .OrderBy(e => e.LastAssignedAt == null ? 0 : 1).ThenBy(e => e.LastAssignedAt).ThenBy(e => e.Id)
            .FirstOrDefaultAsync();

    /// <summary>Assigns the lead (null = unassign) and records history. Caller saves.</summary>
    public async Task AssignAsync(Lead lead, long? employeeId, string? reason, string changedBy)
    {
        if (lead.AssignedToEmployeeId == employeeId) return;
        var now = DateTime.UtcNow;
        db.LeadAssignmentHistory.Add(new LeadAssignmentHistory
        {
            LeadId = lead.Id, FromEmployeeId = lead.AssignedToEmployeeId, ToEmployeeId = employeeId,
            Reason = Truncate(reason, 500), ChangedBy = changedBy, CreatedAt = now,
        });
        lead.AssignedToEmployeeId = employeeId;
        lead.AssignedAt = employeeId is null ? null : now;
        lead.UpdatedAt = now;
        if (employeeId is { } id)
        {
            var emp = await db.Employees.FirstOrDefaultAsync(e => e.Id == id);
            if (emp is not null) emp.LastAssignedAt = now;
        }
    }

    /// <summary>Gives an unassigned lead to the next employee in the rotation when auto-assign is on. Caller saves.</summary>
    public async Task AutoAssignAsync(Lead lead)
    {
        if (lead.AssignedToEmployeeId is not null || !await IsAutoAssignEnabledAsync()) return;
        var next = await PickNextEmployeeAsync();
        if (next is null) return;
        await AssignAsync(lead, next.Id, "Auto-assigned (round-robin)", "auto");
    }

    /// <summary>Moves every open lead of a leaving employee: spread across the remaining team, or back to the admin queue.</summary>
    public async Task<int> ReassignOpenLeadsAsync(long fromEmployeeId, bool spreadAcrossTeam, string changedBy)
    {
        var open = await db.Leads.Where(l => l.AssignedToEmployeeId == fromEmployeeId
            && l.Status != LeadStatuses.Converted && l.Status != LeadStatuses.NotInterested).ToListAsync();
        foreach (var lead in open)
        {
            var target = spreadAcrossTeam ? await PickNextEmployeeAsync(fromEmployeeId) : null;
            await AssignAsync(lead, target?.Id, $"Employee removed ({(target is null ? "returned to admin queue" : "redistributed")})", changedBy);
            await db.SaveChangesAsync(); // persists LastAssignedAt so the rotation advances per lead
        }
        return open.Count;
    }
    /// <summary>Links an outbound message to an existing lead and stamps the conversation (caller saves). Never creates a lead.</summary>
    public async Task<long?> RecordOutboundAsync(string phone)
    {
        var norm = NormalizePhone(phone);
        var leadId = await db.Leads.Where(l => l.Phone == norm).Select(l => (long?)l.Id).FirstOrDefaultAsync();
        if (leadId is null) return null;
        var convo = await db.Conversations.FirstOrDefaultAsync(c => c.LeadId == leadId);
        if (convo is null) { convo = new Conversation { LeadId = leadId.Value }; db.Conversations.Add(convo); }
        convo.LastOutboundAt = DateTime.UtcNow;
        return leadId;
    }

    public async Task<bool> ChangeStatusAsync(long id, string status, string? note, string changedBy)
    {
        if (!LeadStatuses.All.Contains(status)) return false;
        var lead = await db.Leads.FirstOrDefaultAsync(l => l.Id == id);
        if (lead is null) return false;
        if (lead.Status == status && string.IsNullOrWhiteSpace(note)) return true;

        db.LeadStatusHistory.Add(new LeadStatusHistory { LeadId = id, FromStatus = lead.Status, ToStatus = status, Note = Truncate(note, 500), ChangedBy = changedBy });
        lead.Status = status;
        lead.UpdatedAt = DateTime.UtcNow;
        lead.ConvertedAt = status == LeadStatuses.Converted ? lead.ConvertedAt ?? DateTime.UtcNow : null;
        await db.SaveChangesAsync();
        return true;
    }

    private static void SetConsent(Lead lead, string status, DateTime now)
    {
        lead.ConsentStatus = status;
        lead.ConsentAt = now;
        lead.ConsentSource = "whatsapp-keyword";
    }

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}
