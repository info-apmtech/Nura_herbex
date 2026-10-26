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
public class LeadService(NuraDbContext db, IOptions<WhatsAppOptions> options, ILogger<LeadService> log, StaffAvailability availability)
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

    /// <summary>The online sales employee with the least open work, or null when nobody is signed in (the lead then waits in the admin queue).</summary>
    public Task<Employee?> PickNextEmployeeAsync(long? excludeId = null, bool whatsAppOnly = true) => availability.PickAsync("leads", null, excludeId, whatsAppOnly);

    /// <summary>WhatsApp leads only go to staff who switched "WhatsApp active" on; leads from other sources (website orders) go to anyone signed in.</summary>
    private static bool NeedsWhatsAppSwitch(Lead lead) => lead.Source == "whatsapp";

    /// <summary>Gives leads that arrived while nobody was online to whoever is signed in now, least busy first. Called by the background worker.</summary>
    public async Task<int> AssignWaitingAsync()
    {
        if (!await IsAutoAssignEnabledAsync()) return 0;
        var waiting = await db.Leads.Where(l => l.AssignedToEmployeeId == null && l.Status != LeadStatuses.Converted && l.Status != LeadStatuses.NotInterested)
            .OrderBy(l => l.CreatedAt).Take(50).ToListAsync();
        var given = 0;
        var noTakerFor = new HashSet<bool>(); // stop asking once nobody can take a kind of lead (WhatsApp / other) in this pass
        foreach (var lead in waiting)
        {
            var wa = NeedsWhatsAppSwitch(lead);
            if (noTakerFor.Contains(wa)) continue;
            var next = await PickNextEmployeeAsync(null, wa);
            if (next is null) { noTakerFor.Add(wa); continue; } // nobody online (or nobody WhatsApp-active) for this kind of lead; the other kind may still have a taker
            await AssignAsync(lead, next.Id, "Auto-assigned when a team member came online", "auto");
            await db.SaveChangesAsync(); // so the next pick sees the new workload
            given++;
        }
        return given;
    }

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
        var next = await PickNextEmployeeAsync(null, NeedsWhatsAppSwitch(lead));
        if (next is null) return;
        await AssignAsync(lead, next.Id, NeedsWhatsAppSwitch(lead) ? "Auto-assigned (online, WhatsApp active, least busy)" : "Auto-assigned (online and least busy)", "auto");
    }

    /// <summary>
    /// Leads an employee has not started on yet: still "new" and no reply from a team member. These are the ones that can safely move to someone else
    /// when the owner is away; leads already being worked stay with their owner.
    /// </summary>
    private IQueryable<Lead> PendingOf(long employeeId) =>
        db.Leads.Where(l => l.AssignedToEmployeeId == employeeId && l.Status == LeadStatuses.New
            && !db.WhatsAppMessages.Any(m => m.LeadId == l.Id && m.Direction == "outbound" && m.SentBy != null && m.SentBy != "system")); // the automatic welcome message is sent by "system" and does not count

    /// <summary>Gives the pending leads of <paramref name="employeeId"/> to other WhatsApp-active people; with nobody available they go back to the admin queue until someone is.</summary>
    public async Task<int> MovePendingLeadsAsync(long employeeId, string reason, string changedBy)
    {
        var pending = await PendingOf(employeeId).OrderBy(l => l.CreatedAt).ToListAsync();
        foreach (var lead in pending)
        {
            var target = await PickNextEmployeeAsync(employeeId, NeedsWhatsAppSwitch(lead));
            await AssignAsync(lead, target?.Id, $"{reason} ({(target is null ? "returned to admin queue" : "moved to an active person")})", changedBy);
            await db.SaveChangesAsync(); // so the next pick sees the new workload
        }
        return pending.Count;
    }

    /// <summary>Turns "WhatsApp active" off for a person and moves their pending leads to others. Returns how many leads moved.</summary>
    public async Task<int> SwitchOffWhatsAppAsync(long employeeId, string reason, string changedBy)
    {
        var emp = await db.Employees.FirstOrDefaultAsync(e => e.Id == employeeId);
        if (emp is null) return 0;
        emp.WhatsAppActive = false;
        await db.SaveChangesAsync();
        return await MovePendingLeadsAsync(employeeId, reason, changedBy);
    }

    /// <summary>
    /// Anyone marked WhatsApp active who is no longer around (signed out, stopped checking in, or deactivated) is switched off,
    /// and their pending leads move to someone who is. Called by the background worker.
    /// </summary>
    public async Task<int> ReleaseAwayStaffAsync()
    {
        var cutoff = StaffAvailability.OnlineCutoff();
        var away = await db.Employees.Where(e => e.WhatsAppActive && (!e.IsActive || e.LastSeenAt == null || e.LastSeenAt < cutoff)).Select(e => e.Id).ToListAsync();
        var moved = 0;
        foreach (var id in away) moved += await SwitchOffWhatsAppAsync(id, "Owner went offline", "auto");
        if (away.Count > 0) log.LogInformation("[Lead] {Away} staff went away; {Moved} pending lead(s) moved", away.Count, moved);
        return moved;
    }

    /// <summary>Moves every open lead of a leaving employee: spread across the remaining team, or back to the admin queue.</summary>
    public async Task<int> ReassignOpenLeadsAsync(long fromEmployeeId, bool spreadAcrossTeam, string changedBy)
    {
        var open = await db.Leads.Where(l => l.AssignedToEmployeeId == fromEmployeeId
            && l.Status != LeadStatuses.Converted && l.Status != LeadStatuses.NotInterested).ToListAsync();
        foreach (var lead in open)
        {
            var target = spreadAcrossTeam ? await PickNextEmployeeAsync(fromEmployeeId, NeedsWhatsAppSwitch(lead)) : null;
            await AssignAsync(lead, target?.Id, $"Employee removed ({(target is null ? "returned to admin queue" : "redistributed")})", changedBy);
            await db.SaveChangesAsync(); // persists LastAssignedAt so the rotation advances per lead
        }
        return open.Count;
    }
    /// <summary>
    /// Makes sure a customer who ordered has a lead, marked Converted and owned by someone. Idempotent: safe to call every cycle.
    /// A lead that already exists (e.g. from WhatsApp) is reused so the whole history stays on one record.
    /// </summary>
    public async Task<Lead> UpsertFromOrderAsync(Order o)
    {
        var norm = NormalizePhone(o.CustomerPhone);
        var now = DateTime.UtcNow;
        var lead = await db.Leads.FirstOrDefaultAsync(l => l.Phone == norm);
        if (lead is null)
        {
            lead = new Lead
            {
                Phone = norm, Name = string.IsNullOrWhiteSpace(o.CustomerName) ? norm : o.CustomerName.Trim(), Email = o.CustomerEmail,
                Source = "website", SourceDetail = $"Order {o.Id}", Status = LeadStatuses.Converted, ConvertedAt = now,
                CustomerId = o.CustomerId, CreatedAt = now, UpdatedAt = now, LastContactAt = now,
            };
            db.Leads.Add(lead);
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                db.Entry(lead).State = EntityState.Detached;
                lead = await db.Leads.FirstAsync(l => l.Phone == norm);
            }
            if (lead.Status == LeadStatuses.Converted && lead.SourceDetail == $"Order {o.Id}")
                db.LeadStatusHistory.Add(new LeadStatusHistory { LeadId = lead.Id, ToStatus = LeadStatuses.Converted, Note = $"Customer placed order {o.Id}", CreatedAt = now });
        }
        else
        {
            if (lead.Status != LeadStatuses.Converted)
            {
                db.LeadStatusHistory.Add(new LeadStatusHistory { LeadId = lead.Id, FromStatus = lead.Status, ToStatus = LeadStatuses.Converted, Note = $"Customer placed order {o.Id}", CreatedAt = now });
                lead.Status = LeadStatuses.Converted;
                lead.ConvertedAt ??= now;
                lead.UpdatedAt = now;
            }
            if (lead.CustomerId is null && o.CustomerId is not null) lead.CustomerId = o.CustomerId;
            if (string.IsNullOrEmpty(lead.Email) && !string.IsNullOrEmpty(o.CustomerEmail)) lead.Email = o.CustomerEmail;
            if (lead.Name == lead.Phone && !string.IsNullOrWhiteSpace(o.CustomerName)) lead.Name = o.CustomerName.Trim();
        }
        await AutoAssignAsync(lead);
        await db.SaveChangesAsync();
        return lead;
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
