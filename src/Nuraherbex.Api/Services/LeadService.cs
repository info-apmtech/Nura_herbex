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
public class LeadService(NuraDbContext db, IOptions<WhatsAppOptions> options, ILogger<LeadService> log, StaffAvailability availability, MessageTemplateService templates)
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

    /// <summary>True (or false when <paramref name="want"/> is false) when the lead's most recent finished follow-up ended with "no answer". One rule shared by the Leads tabs and the reports.</summary>
    public static System.Linq.Expressions.Expression<Func<Lead, bool>> NoAnswerOf(NuraDbContext db, bool want)
    {
        System.Linq.Expressions.Expression<Func<Lead, bool>> e = l => (db.FollowUps.Where(f => f.LeadId == l.Id && f.Status == FollowUpStatus.Done && f.Outcome != null)
            .OrderByDescending(f => f.CompletedAt).ThenByDescending(f => f.Id).Select(f => f.Outcome).FirstOrDefault() ?? "").StartsWith(FollowUpOutcomes.NoAnswer);
        return want ? e : System.Linq.Expressions.Expression.Lambda<Func<Lead, bool>>(System.Linq.Expressions.Expression.Not(e.Body), e.Parameters);
    }

    /// <summary>
    /// Leads that count as a new enquiry. A customer who orders without ever messaging gets a Converted "Website" lead stamped with the
    /// order time (see <see cref="UpsertFromOrderAsync"/>); that is not a new lead and must not inflate the New leads figure.
    /// </summary>
    public static System.Linq.Expressions.Expression<Func<Lead, bool>> IsNewEnquiry { get; } =
        l => !(l.Source == LeadSource.Website && l.SourceDetail != null && l.SourceDetail.StartsWith("Order "));

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
                Source = LeadSource.WhatsApp, SourceDetail = referralDetail, FirstMessage = Truncate(body, 1000),
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
            db.LeadStatusHistory.Add(new LeadStatusHistory { LeadId = lead.Id, ToStatus = LeadStatus.New, Note = "Lead captured from WhatsApp", CreatedAt = now });
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
            db.LeadSourceEvents.Add(new LeadSourceEvent { LeadId = lead.Id, Source = LeadSource.WhatsApp, Detail = referralDetail, ReferralJson = referralJson, CreatedAt = now });

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
    // ---------------------------------------------------------------------
    // After-hours reply: nobody active (Sunday, holiday, night) -> the customer still gets an answer
    // ---------------------------------------------------------------------

    private static readonly TimeSpan AfterHoursRepeat = TimeSpan.FromHours(12);

    /// <summary>Who is writing: a lead (with its stage) or a paying customer, and the order a customer is still waiting for.</summary>
    public sealed record Audience(string Stage, Order? OpenOrder, Order? LatestOrder)
    {
        public bool IsCustomer => Stage == MessageStages.Customer;
    }

    /// <summary>Orders match by phone as typed (10 digits, or with 91 / 0 in front).</summary>
    public static string[] PhoneForms(string phone)
    {
        var d = new string((phone ?? "").Where(char.IsDigit).ToArray());
        var l = d.Length > 10 ? d[^10..] : d;
        return [l, "91" + l, "0" + l, "+91" + l, phone ?? ""];
    }

    /// <summary>Confirmed orders (paid or cash on delivery) that are not cancelled or returned.</summary>
    public static bool IsConfirmed(Order o) => o.PaymentStatus is OrderPaymentStatus.Paid or OrderPaymentStatus.CodPending or OrderPaymentStatus.CodCollected && o.FulfillmentStatus is not ("CANCELLED" or "RTO");

    /// <summary>The first word of a name, or "there" when the lead only has a phone number.</summary>
    public static string FirstName(string? name, string? phone) =>
        string.IsNullOrWhiteSpace(name) || name == phone ? "there" : name.Trim().Split(' ')[0];

    public async Task<Audience> AudienceAsync(Lead lead)
    {
        var stage = MessageStages.Of(lead.Status, lead.CustomerSince is not null);
        if (stage != MessageStages.Customer) return new Audience(stage, null, null);
        var forms = PhoneForms(lead.Phone);
        var orders = (await db.Orders.AsNoTracking().Where(o => forms.Contains(o.CustomerPhone)).OrderByDescending(o => o.CreatedAt).Take(20).ToListAsync()).Where(IsConfirmed).ToList();
        return new Audience(stage, orders.FirstOrDefault(o => o.FulfillmentStatus != "DELIVERED"), orders.FirstOrDefault());
    }

    /// <summary>
    /// The text to send for a new inbound WhatsApp message, or null when none is needed: the feature is off, the customer opted out,
    /// somebody is signed in and WhatsApp active (they will answer), or this customer already got it in the last 12 hours.
    /// The text depends on who is writing: leads never get order tracking, customers do while an order is on its way.
    /// </summary>
    public async Task<string?> AfterHoursReplyAsync(Lead lead)
    {
        var all = await templates.GetAsync();
        if (!all.AfterHoursEnabled || lead.ConsentStatus == "opted_out") return null;
        if ((await availability.EligibleAsync("leads", null, null, true)).Count > 0) return null;
        var since = DateTime.UtcNow - AfterHoursRepeat;
        if (await db.WhatsAppMessages.AnyAsync(m => m.LeadId == lead.Id && m.Direction == "outbound" && m.SentBy == "auto" && m.CreatedAt >= since)) return null;

        var who = await AudienceAsync(lead);
        var key = who.Stage switch
        {
            MessageStages.Customer => who.OpenOrder is not null ? MessageKeys.AfterHoursCustomerOpen : MessageKeys.AfterHoursCustomer,
            MessageStages.Interested => MessageKeys.AfterHoursInterested,
            _ => MessageKeys.AfterHoursLead,
        };
        var body = all.Items.FirstOrDefault(i => i.Key == key) is { Enabled: true, Body: { Length: > 0 } b } ? b : null;
        if (body is null) return null;
        var name = lead.Name == lead.Phone ? null : lead.Name;
        return MessageDefaults.Fill(body, name, null, who.OpenOrder?.Id ?? who.LatestOrder?.Id);
    }

    /// <summary>The greeting for "hi" (or a track request from someone who has nothing to track), fitted to who is writing.</summary>
    public async Task<string?> GreetingAsync(Lead lead)
    {
        var who = await AudienceAsync(lead);
        var key = who.IsCustomer ? (who.OpenOrder is not null ? MessageKeys.GreetingCustomerOpen : MessageKeys.GreetingCustomer) : MessageKeys.GreetingLead;
        if (await templates.BodyAsync(key) is not { } body) return null;
        return MessageDefaults.Fill(body, lead.Name == lead.Phone ? null : lead.Name, null, who.OpenOrder?.Id ?? who.LatestOrder?.Id);
    }

    /// <summary>The reply for a customer who asks to track an order that is already delivered.</summary>
    public async Task<string?> DeliveredReplyAsync(Lead lead, Order latest) =>
        await templates.BodyAsync(MessageKeys.Delivered) is { } body ? MessageDefaults.Fill(body, lead.Name == lead.Phone ? null : lead.Name, null, latest.Id) : null;

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
    private static bool NeedsWhatsAppSwitch(Lead lead) => lead.Source == LeadSource.WhatsApp;

    /// <summary>Gives leads that arrived while nobody was online to whoever is signed in now, least busy first. Called by the background worker.</summary>
    public async Task<int> AssignWaitingAsync()
    {
        if (!await IsAutoAssignEnabledAsync()) return 0;
        var waiting = await db.Leads.Where(l => l.AssignedToEmployeeId == null && l.Status != LeadStatus.Converted && l.Status != LeadStatus.NotInterested)
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
        return given + await AssignCustomersAsync();
    }

    // ---------------------------------------------------------------------
    // Customer care: paid customers stay with the sales person who converted them; when that person is away, an available person takes over
    // ---------------------------------------------------------------------

    /// <summary>An available Customer care person: WhatsApp-active people first, then anyone online who may open Customer care.</summary>
    private async Task<Employee?> PickCareOwnerAsync(long? excludeId) =>
        await availability.PickAsync("care", null, excludeId, true) ?? await availability.PickAsync("care", null, excludeId, false);

    /// <summary>
    /// Customers nobody owns go to an available person. Customers who wrote and are still waiting after 10 minutes while their owner is away
    /// move to an available person. The sales person who converted them stays recorded and shown. Called by the background worker.
    /// </summary>
    public async Task<int> AssignCustomersAsync()
    {
        if (!await IsAutoAssignEnabledAsync()) return 0;
        var moved = 0;

        foreach (var lead in await db.Leads.Where(l => l.CustomerSince != null && l.AssignedToEmployeeId == null).OrderBy(l => l.CustomerSince).Take(50).ToListAsync())
        {
            var next = await PickCareOwnerAsync(null);
            if (next is null) break; // nobody is available; they wait until someone is
            await AssignAsync(lead, next.Id, "Customer care: given to an available person", "auto");
            await db.SaveChangesAsync();
            moved++;
        }

        var cutoff = DateTime.UtcNow.AddMinutes(-10);
        var waiting = await (from l in db.Leads
                             join c in db.Conversations on l.Id equals c.LeadId
                             where l.CustomerSince != null && l.AssignedToEmployeeId != null && c.UnreadCount > 0 && c.LastInboundAt != null && c.LastInboundAt < cutoff
                             orderby c.LastInboundAt
                             select l).Take(50).ToListAsync();
        foreach (var lead in waiting)
        {
            var owner = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == lead.AssignedToEmployeeId);
            if (owner is not null && StaffAvailability.IsOnline(owner)) continue; // their owner is around and will answer
            var next = await PickCareOwnerAsync(owner?.Id);
            if (next is null) continue;
            await AssignAsync(lead, next.Id, $"Customer care: {owner?.Name ?? "the owner"} was not available", "auto");
            await db.SaveChangesAsync();
            moved++;
        }
        if (moved > 0) log.LogInformation("[Lead] Customer care: {Moved} customer(s) handed to an available person", moved);
        return moved;
    }

    /// <summary>
    /// First paid order: remember who converted the lead and make sure an available person looks after the customer.
    /// The converting sales person keeps the customer when they are around; otherwise (or for a plain website order) an available person gets it.
    /// </summary>
    private async Task SetCustomerOwnerAsync(Lead lead)
    {
        var owner = lead.AssignedToEmployeeId is { } id ? await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id) : null;
        if (owner is not null && owner.IsActive && StaffAvailability.IsOnline(owner)) return;
        if (!await IsAutoAssignEnabledAsync()) return;
        var next = await PickCareOwnerAsync(owner?.Id);
        if (next is null) return; // nobody available: it stays with the converter, or in the queue, until someone is
        await AssignAsync(lead, next.Id, owner is null ? "Customer care: given to an available person" : $"Customer care: {owner.Name} is not available", "auto");
    }

    /// <summary>
    /// Shares unstarted leads out evenly when someone new comes online. People who are online and eligible are compared by how many unopened leads they hold;
    /// while the fullest holds at least 2 more than the emptiest and has a movable unstarted lead (status "new", no team reply yet), the newest of those
    /// moves to the emptiest person. Leads already being worked never move. Returns how many leads moved.
    /// </summary>
    public async Task<int> RebalanceAsync()
    {
        if (!await IsAutoAssignEnabledAsync()) return 0;
        var moved = 0;
        foreach (var wa in new[] { true, false })
        {
            var people = await availability.EligibleAsync("leads", null, null, wa);
            if (people.Count < 2) continue;
            // Balance on the number of unopened leads each person holds (15 unopened across 3 people = 5 each), counting leads that cannot move too.
            var load = new Dictionary<long, int>();
            foreach (var p in people) load[p.Id] = await PendingOf(p.Id).CountAsync(l => (l.Source == LeadSource.WhatsApp) == wa);
            for (var guard = 0; guard < 500; guard++)
            {
                var receiver = people.OrderBy(p => load.GetValueOrDefault(p.Id)).ThenBy(p => p.LastAssignedAt == null ? 0 : 1).ThenBy(p => p.LastAssignedAt).ThenBy(p => p.Id).First();
                Lead? lead = null; Employee? donor = null;
                foreach (var d in people.Where(p => p.Id != receiver.Id && load.GetValueOrDefault(p.Id) - load.GetValueOrDefault(receiver.Id) >= 2).OrderByDescending(p => load.GetValueOrDefault(p.Id)))
                {
                    // the latest unopened lead moves first
                    lead = await PendingOf(d.Id, keepManual: true).Where(l => (l.Source == LeadSource.WhatsApp) == wa).OrderByDescending(l => l.CreatedAt).FirstOrDefaultAsync();
                    if (lead is not null) { donor = d; break; }
                }
                if (lead is null || donor is null) break;
                await AssignAsync(lead, receiver.Id, $"Shared out evenly: {receiver.Name} is online and {donor.Name} had unstarted leads waiting", "auto");
                await db.SaveChangesAsync(); // so the next round sees the new workload
                load[donor.Id] = load.GetValueOrDefault(donor.Id) - 1;
                load[receiver.Id] = load.GetValueOrDefault(receiver.Id) + 1;
                moved++;
            }
        }
        if (moved > 0) log.LogInformation("[Lead] Rebalanced {Moved} unstarted lead(s) between online staff", moved);
        return moved;
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
    private IQueryable<Lead> PendingOf(long employeeId, bool keepManual = false) =>
        db.Leads.Where(l => l.AssignedToEmployeeId == employeeId && l.Status == LeadStatus.New
            // a lead a person (admin / colleague) handed over on purpose stays put when the system rebalances or the owner merely looks away
            && (!keepManual || (db.LeadAssignmentHistory.Where(h => h.LeadId == l.Id).OrderByDescending(h => h.CreatedAt).ThenByDescending(h => h.Id).Select(h => h.ChangedBy).FirstOrDefault() ?? "auto") == "auto")
            && !db.WhatsAppMessages.Any(m => m.LeadId == l.Id && m.Direction == "outbound" && m.SentBy != null && m.SentBy != "system" && m.SentBy != "auto")); // automatic messages (welcome = "system", after-hours reply = "auto") do not count

    /// <summary>Gives the pending leads of <paramref name="employeeId"/> to other WhatsApp-active people; with nobody available they go back to the admin queue until someone is.</summary>
    public async Task<int> MovePendingLeadsAsync(long employeeId, string reason, string changedBy, bool keepManual = false)
    {
        var pending = await PendingOf(employeeId, keepManual).OrderBy(l => l.CreatedAt).ToListAsync();
        foreach (var lead in pending)
        {
            var target = await PickNextEmployeeAsync(employeeId, NeedsWhatsAppSwitch(lead));
            await AssignAsync(lead, target?.Id, $"{reason} ({(target is null ? "returned to admin queue" : "moved to an active person")})", changedBy);
            await db.SaveChangesAsync(); // so the next pick sees the new workload
        }
        return pending.Count;
    }

    /// <summary>Turns "WhatsApp active" off for a person and moves their pending leads to others. Returns how many leads moved.</summary>
    public async Task<int> SwitchOffWhatsAppAsync(long employeeId, string reason, string changedBy, bool keepManual = false)
    {
        var emp = await db.Employees.FirstOrDefaultAsync(e => e.Id == employeeId);
        if (emp is null) return 0;
        emp.WhatsAppActive = false;
        await db.SaveChangesAsync();
        return await MovePendingLeadsAsync(employeeId, reason, changedBy, keepManual);
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
        foreach (var id in away) moved += await SwitchOffWhatsAppAsync(id, "Owner went offline", "auto", keepManual: true);
        if (away.Count > 0) log.LogInformation("[Lead] {Away} staff went away; {Moved} pending lead(s) moved", away.Count, moved);
        return moved;
    }

    /// <summary>Moves every open lead of a leaving employee: spread across the remaining team, or back to the admin queue.</summary>
    public async Task<int> ReassignOpenLeadsAsync(long fromEmployeeId, bool spreadAcrossTeam, string changedBy) =>
        (await HandOverAsync(fromEmployeeId, spreadAcrossTeam ? HandoverModes.Auto : HandoverModes.Unassigned, null, [], [], changedBy)).Leads;

    public sealed record HandoverResult(int Leads, int FollowUps);

    /// <summary>
    /// Hands everything a leaving employee holds to others: open leads (and customers) and pending follow-ups.
    /// <paramref name="mode"/>: "equal" deals them out one by one to whoever holds the fewest, so the team ends up level; "person" gives all to <paramref name="toEmployeeId"/>;
    /// "unassigned" returns them to the admin queue; "auto" picks by who is online. Follow-ups go with their lead, so one person never holds the task without the lead.
    /// <paramref name="leadPeople"/> / <paramref name="carePeople"/> are the active people allowed to work leads / customers (used by "equal" and "person").
    /// </summary>
    public async Task<HandoverResult> HandOverAsync(long fromEmployeeId, string mode, long? toEmployeeId, IReadOnlyCollection<long> leadPeople, IReadOnlyCollection<long> carePeople, string changedBy)
    {
        var open = await db.Leads.Where(l => l.AssignedToEmployeeId == fromEmployeeId
            && (l.CustomerSince != null || (l.Status != LeadStatus.Converted && l.Status != LeadStatus.NotInterested))).OrderBy(l => l.CreatedAt).ToListAsync(); // customers need an owner too

        var pool = leadPeople.Concat(carePeople).Where(id => id != fromEmployeeId).Distinct().ToList();
        // How many open leads each possible receiver holds now; "equal" always hands the next one to the lightest.
        var load = pool.Count == 0 ? [] : (await db.Leads.AsNoTracking().Where(l => l.AssignedToEmployeeId != null && pool.Contains(l.AssignedToEmployeeId.Value)
                && (l.CustomerSince != null || (l.Status != LeadStatus.Converted && l.Status != LeadStatus.NotInterested)))
            .GroupBy(l => l.AssignedToEmployeeId!.Value).Select(g => new { g.Key, N = g.Count() }).ToListAsync()).ToDictionary(x => x.Key, x => x.N);
        foreach (var id in pool) load.TryAdd(id, 0);
        var received = new Dictionary<long, (int Leads, int FollowUps)>();
        void Got(long id, bool lead) { var r = received.GetValueOrDefault(id); received[id] = lead ? (r.Leads + 1, r.FollowUps) : (r.Leads, r.FollowUps + 1); }

        long? Lightest(IReadOnlyCollection<long> from) =>
            from.Where(id => id != fromEmployeeId).OrderBy(id => load.GetValueOrDefault(id)).ThenBy(id => id).Select(id => (long?)id).FirstOrDefault();

        foreach (var lead in open)
        {
            long? target;
            string how;
            switch (mode)
            {
                case HandoverModes.Equal:
                    target = Lightest(lead.CustomerSince != null ? carePeople : leadPeople) ?? Lightest(pool);
                    how = "split evenly";
                    break;
                case HandoverModes.Person:
                    target = toEmployeeId;
                    how = "handed to one person";
                    break;
                case HandoverModes.Unassigned:
                    target = null; how = "returned to admin queue";
                    break;
                default:
                    target = (lead.CustomerSince != null ? await PickCareOwnerAsync(fromEmployeeId) : await PickNextEmployeeAsync(fromEmployeeId, NeedsWhatsAppSwitch(lead)))?.Id;
                    how = target is null ? "returned to admin queue" : "redistributed";
                    break;
            }
            await AssignAsync(lead, target, $"Employee removed ({how})", changedBy);
            if (target is { } t) { load[t] = load.GetValueOrDefault(t) + 1; Got(t, true); }
            await db.SaveChangesAsync(); // persists LastAssignedAt so the rotation advances per lead
        }

        // Pending follow-ups follow their lead to the lead's new owner; ones whose lead is not theirs (closed or owned by someone else) go to the same place the leads went.
        var followUps = await db.FollowUps.Where(f => f.AssignedToEmployeeId == fromEmployeeId && f.Status == FollowUpStatus.Pending).OrderBy(f => f.DueAt).ToListAsync();
        var fuLeadIds = followUps.Select(f => f.LeadId).Distinct().ToList();
        var leadOwner = followUps.Count == 0 ? [] : await db.Leads.AsNoTracking().Where(l => fuLeadIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.AssignedToEmployeeId);
        foreach (var f in followUps)
        {
            long? owner = leadOwner.GetValueOrDefault(f.LeadId);
            if (owner == fromEmployeeId) owner = null;
            if (owner is null)
                owner = mode switch
                {
                    HandoverModes.Person => toEmployeeId,
                    HandoverModes.Equal => Lightest(leadPeople) ?? Lightest(pool),
                    HandoverModes.Auto => (await PickNextEmployeeAsync(fromEmployeeId, false))?.Id,
                    _ => null,
                };
            f.AssignedToEmployeeId = owner;
            f.ReminderSentAt = null; f.DueNotifiedAt = null; // the new owner gets the heads-up / due notice themselves
            if (owner is { } o) Got(o, false);
        }

        var names = await db.Employees.AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.Name);
        var from = names.GetValueOrDefault(fromEmployeeId) ?? "A team member";
        foreach (var (id, r) in received)
            db.Notifications.Add(new Notification
            {
                EmployeeId = id, Kind = "lead", Link = "/admin/leads",
                Title = $"{from}'s work was handed to you",
                Body = $"{r.Leads} lead(s) and {r.FollowUps} follow-up(s) are now yours.",
            });
        await db.SaveChangesAsync();
        return new HandoverResult(open.Count, followUps.Count);
    }

    /// <summary>
    /// Closes a lead's pending follow-ups that a person scheduled by hand (journey and order tasks are left alone); only those of <paramref name="type"/> when given.
    /// Caller saves.
    /// </summary>
    public async Task<int> CloseOpenManualFollowUpsAsync(long leadId, string reason, string by, string? type = null)
    {
        var now = DateTime.UtcNow;
        var open = (await db.FollowUps.Where(f => f.LeadId == leadId && f.Status == FollowUpStatus.Pending && f.Source == FollowUpSource.Manual && (type == null || f.Type == type)).ToListAsync())
            .Where(f => f.Status == FollowUpStatus.Pending).ToList(); // re-checked: the caller may have just completed one in memory
        foreach (var f in open) { f.Status = FollowUpStatus.Skipped; f.CompletedAt = now; f.CompletedBy = by; f.Outcome = Truncate(reason, 500); }
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
                Source = LeadSource.Website, SourceDetail = $"Order {o.Id}", Status = LeadStatus.Converted, ConvertedAt = now,
                CustomerId = o.CustomerId, CreatedAt = now, UpdatedAt = now, LastContactAt = now,
            };
            db.Leads.Add(lead);
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                db.Entry(lead).State = EntityState.Detached;
                lead = await db.Leads.FirstAsync(l => l.Phone == norm);
            }
            if (lead.Status == LeadStatus.Converted && lead.SourceDetail == $"Order {o.Id}")
                db.LeadStatusHistory.Add(new LeadStatusHistory { LeadId = lead.Id, ToStatus = LeadStatus.Converted, Note = $"Customer placed order {o.Id}", CreatedAt = now });
        }
        else
        {
            if (lead.Status != LeadStatus.Converted)
            {
                LeadStatus? fromStatus = Enum.IsDefined(lead.Status) ? lead.Status : null;
                db.LeadStatusHistory.Add(new LeadStatusHistory { LeadId = lead.Id, FromStatus = fromStatus, ToStatus = LeadStatus.Converted, Note = $"Customer placed order {o.Id}", CreatedAt = now });
                lead.Status = LeadStatus.Converted;
                lead.ConvertedAt ??= now;
                lead.UpdatedAt = now;
            }
            if (lead.CustomerId is null && o.CustomerId is not null) lead.CustomerId = o.CustomerId;
            if (string.IsNullOrEmpty(lead.Email) && !string.IsNullOrEmpty(o.CustomerEmail)) lead.Email = o.CustomerEmail;
            if (lead.Name == lead.Phone && !string.IsNullOrWhiteSpace(o.CustomerName)) lead.Name = o.CustomerName.Trim();
        }

        // The first paid order turns a lead into a customer, remembering who converted it. Later orders change nothing here, so a cycle that
        // runs over old orders never moves a customer around.
        if (lead.CustomerSince is null)
        {
            lead.CustomerSince = now;
            lead.ConvertedByEmployeeId = lead.AssignedToEmployeeId;
            await SetCustomerOwnerAsync(lead);
        }
        else if (lead.AssignedToEmployeeId is null) await AutoAssignAsync(lead);
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

    /// <summary>Why a status change is not allowed, or null. A lead becomes a customer only through a paid order, and a customer stays one.</summary>
    public static string? StatusBlock(Lead lead, string status) =>
        status == LeadStatuses.Converted && lead.CustomerSince is null ? "A lead becomes a customer when a paid or cash-on-delivery order is placed. Use “Create order” on the lead."
        : lead.CustomerSince is not null && status != LeadStatuses.Converted ? "This person is a paying customer, so they stay in Customer care."
        : null;

    public async Task<bool> ChangeStatusAsync(long id, string status, string? note, string changedBy)
    {
        if (!LeadStatuses.TryParse(status, out var nextStatus)) return false;
        var lead = await db.Leads.FirstOrDefaultAsync(l => l.Id == id);
        if (lead is null || StatusBlock(lead, nextStatus.ToCode()) is not null) return false;
        if (lead.Status == nextStatus && string.IsNullOrWhiteSpace(note)) return true;

        LeadStatus? fromStatus = Enum.IsDefined(lead.Status) ? lead.Status : null;
        db.LeadStatusHistory.Add(new LeadStatusHistory { LeadId = id, FromStatus = fromStatus, ToStatus = nextStatus, Note = Truncate(note, 500), ChangedBy = changedBy });
        lead.Status = nextStatus;
        lead.UpdatedAt = DateTime.UtcNow;
        lead.ConvertedAt = nextStatus == LeadStatus.Converted ? lead.ConvertedAt ?? DateTime.UtcNow : null;
        // Nobody should keep chasing someone who said no: their hand-made follow-ups stop appearing in the lists.
        if (nextStatus == LeadStatus.NotInterested) await CloseOpenManualFollowUpsAsync(id, "Lead marked not interested", changedBy);
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
