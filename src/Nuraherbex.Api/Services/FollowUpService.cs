using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nuraherbex.Api.Data;
using Nuraherbex.Api.Options;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Services;

/// <summary>The 15-day customer journey: one task per step, scheduled from the delivery date.</summary>
public static class JourneyPlan
{
    public sealed record Step(string Key, int Day, string Type, string Title, string Message);

    public static readonly Step[] Steps =
    [
        new("day1-welcome", 1, FollowUpTypes.WhatsApp, "Day 1 · Welcome & guidance",
            "Hi {name}, welcome to the Nura Herbex family 🌿 Your order {orderId} has been delivered. Please follow the usage instructions printed on the pack. Reply here any time if you have a question about how to use it."),
        new("day3-usage", 3, FollowUpTypes.WhatsApp, "Day 3 · Usage check",
            "Hi {name}, it has been a few days since your Stamix arrived. Are you finding the routine easy to follow? Reply here if you need any help."),
        new("day5-progress", 5, FollowUpTypes.WhatsApp, "Day 5 · Progress check",
            "Hi {name}, you are five days into your Stamix routine. Many customers start noticing steadier energy around now. How are you finding it? Reply here if you would like any tips."),
        new("day7-experience", 7, FollowUpTypes.WhatsApp, "Day 7 · Experience check",
            "Hi {name}, how has your first week with Stamix been? We would love to hear your experience, and our team is here if you have any questions."),
        new("day8-review", 8, FollowUpTypes.Review, "Day 8 · Ask for a review",
            "Hi {name}, if you are happy with Stamix, a short review helps other customers a lot: {reviewLink} Thank you 🙏"),
        new("day10-call", 10, FollowUpTypes.Call, "Day 10 · Personal call",
            "Call the customer: ask how they are using Stamix, how they feel, and whether they have any concerns. Note anything that needs the team. Offer to arrange a reorder."),
        new("day12-reorder", 12, FollowUpTypes.Reorder, "Day 12 · Reorder reminder",
            "Hi {name}, your Stamix pack will be finishing soon. Reorder now to continue without a break: {storeLink}"),
        new("day14-reorder-followup", 14, FollowUpTypes.Reorder, "Day 14-15 · Reorder follow-up",
            "Hi {name}, just checking whether you would like to reorder Stamix. Reply here and we will arrange it for you."),
        new("day15-wrapup", 15, FollowUpTypes.WhatsApp, "Day 15 · Journey wrap-up",
            "Hi {name}, it has been 15 days since your Stamix arrived. Thank you for trusting Nura Herbex 🌿 Tell us how it went, and reorder any time here: {storeLink}"),
    ];

    public static Step? Find(string? key) => Steps.FirstOrDefault(s => s.Key == key);
}

/// <summary>Scheduling, completion, reminders, order-progress checks and reporting for follow-ups.</summary>
public class FollowUpService(
    NuraDbContext db,
    LeadService leads,
    WhatsAppService whatsapp,
    IOptions<FollowUpOptions> options,
    IOptions<StoreOptions> store,
    ILogger<FollowUpService> log)
{
    /// <summary>Current UTC time; tests replace it to travel through the 15 days.</summary>
    public static Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    private static readonly TimeZoneInfo Ist = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "India Standard Time" : "Asia/Kolkata");

    // ---------------------------------------------------------------------
    // Time helpers (everything is stored in UTC; "today" means the Indian calendar day)
    // ---------------------------------------------------------------------

    public static DateTime IstNow => TimeZoneInfo.ConvertTimeFromUtc(Clock(), Ist);
    public static DateTime ToUtc(DateTime istLocal) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(istLocal, DateTimeKind.Unspecified), Ist);
    public static DateTime ToIst(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(AsUtc(utc), Ist);
    public static DateTime TodayStartUtc() => ToUtc(IstNow.Date);

    /// <summary>The given instant moved to "day N at 10:00 IST".</summary>
    private static DateTime AtTenAm(DateTime utc, int addDays) => ToUtc(TimeZoneInfo.ConvertTimeFromUtc(utc, Ist).Date.AddDays(addDays).AddHours(10));

    private static DateTime AsUtc(DateTime d) => d.Kind switch { DateTimeKind.Utc => d, DateTimeKind.Local => d.ToUniversalTime(), _ => DateTime.SpecifyKind(d, DateTimeKind.Utc) };

    // ---------------------------------------------------------------------
    // Settings (admin-controlled, stored in site_settings)
    // ---------------------------------------------------------------------

    private const string KeyJourney = "followups.journey", KeyAutoSend = "followups.autosend", KeyReminder = "followups.reminderDays", KeyStarted = "followups.startedAt";

    private async Task<string?> GetSettingAsync(string key) => (await db.SiteSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key))?.Value;

    private async Task SetSettingAsync(string key, string value)
    {
        var row = await db.SiteSettings.FirstOrDefaultAsync(s => s.Key == key);
        if (row is null) db.SiteSettings.Add(new SiteSetting { Key = key, Value = value });
        else { row.Value = value; row.UpdatedAt = Clock(); }
    }

    public async Task<FollowUpSettingsDto> GetSettingsAsync() => new()
    {
        Success = true,
        JourneyEnabled = !string.Equals(await GetSettingAsync(KeyJourney), "false", StringComparison.OrdinalIgnoreCase),
        AutoSend = string.Equals(await GetSettingAsync(KeyAutoSend), "true", StringComparison.OrdinalIgnoreCase),
        ReminderDays = int.TryParse(await GetSettingAsync(KeyReminder), out var d) ? Math.Clamp(d, 0, 14) : 2,
        Steps = JourneyPlan.Steps.Select(s => new JourneyStepDto
        {
            Key = s.Key, Day = s.Day, Type = s.Type, Title = s.Title, Message = s.Message,
            HasTemplate = options.Value.Templates.TryGetValue(s.Key, out var t) && !string.IsNullOrWhiteSpace(t.Name),
        }).ToList(),
    };

    public async Task SaveSettingsAsync(FollowUpSettingsRequest r)
    {
        await SetSettingAsync(KeyJourney, r.JourneyEnabled ? "true" : "false");
        await SetSettingAsync(KeyAutoSend, r.AutoSend ? "true" : "false");
        await SetSettingAsync(KeyReminder, Math.Clamp(r.ReminderDays, 0, 14).ToString(CultureInfo.InvariantCulture));
        await db.SaveChangesAsync();
    }

    /// <summary>Go-live moment: only orders placed after this get tasks, so existing history is never back-filled.</summary>
    private async Task<DateTime> EnsureStartedAtAsync()
    {
        if (DateTime.TryParse(await GetSettingAsync(KeyStarted), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)) return t;
        var now = Clock();
        await SetSettingAsync(KeyStarted, now.ToString("O", CultureInfo.InvariantCulture));
        await db.SaveChangesAsync();
        return now;
    }

    // ---------------------------------------------------------------------
    // Notifications
    // ---------------------------------------------------------------------

    /// <summary>Notifies one employee, or every administrator when <paramref name="employeeId"/> is null. Caller saves.</summary>
    public void Notify(long? employeeId, string kind, string title, string? body = null, string link = "/admin/followups") =>
        db.Notifications.Add(new Notification { EmployeeId = employeeId, Kind = kind, Title = title, Body = body, Link = link });

    // ---------------------------------------------------------------------
    // Create / complete / reschedule
    // ---------------------------------------------------------------------

    private static string DefaultTitle(string type, string leadName) => type switch
    {
        FollowUpTypes.Call => $"Call {leadName}",
        FollowUpTypes.WhatsApp => $"Message {leadName} on WhatsApp",
        FollowUpTypes.Review => $"Ask {leadName} for a review",
        FollowUpTypes.Reorder => $"Reorder follow-up with {leadName}",
        FollowUpTypes.Order => $"Check order for {leadName}",
        _ => $"Follow up with {leadName}",
    };

    public async Task<(FollowUp? Item, string? Error)> CreateManualAsync(FollowUpCreateRequest r, string actor, bool isAdmin, long? myId)
    {
        var lead = await db.Leads.FirstOrDefaultAsync(l => l.Id == r.LeadId);
        if (lead is null || (!isAdmin && lead.AssignedToEmployeeId != myId)) return (null, "Lead not found.");
        if (!FollowUpTypes.All.Contains(r.Type)) return (null, "Unknown follow-up type.");
        var due = AsUtc(r.DueAt);
        if (due < Clock().AddMinutes(-5)) return (null, "Choose a time in the future.");

        long? owner = isAdmin ? r.AssignedToEmployeeId ?? lead.AssignedToEmployeeId : lead.AssignedToEmployeeId ?? myId;
        if (owner is { } oid && !await db.Employees.AnyAsync(e => e.Id == oid && e.IsActive)) return (null, "That employee is not active.");

        var item = NewItem(lead, r.Type, string.IsNullOrWhiteSpace(r.Title) ? DefaultTitle(r.Type, lead.Name) : r.Title.Trim(), r.Notes, due, owner, "manual", actor, await ReminderDaysAsync());
        db.FollowUps.Add(item);

        // Scheduling a follow-up means the lead is being worked on.
        if (lead.Status is LeadStatuses.New or LeadStatuses.Interested)
        {
            db.LeadStatusHistory.Add(new LeadStatusHistory { LeadId = lead.Id, FromStatus = lead.Status, ToStatus = LeadStatuses.FollowUp, Note = "Follow-up scheduled", ChangedBy = actor });
            lead.Status = LeadStatuses.FollowUp;
            lead.UpdatedAt = Clock();
        }
        // Unowned lead + sales user: they take it, so it does not float around.
        if (lead.AssignedToEmployeeId is null && owner is not null) await leads.AssignAsync(lead, owner, "Took ownership by scheduling a follow-up", actor);
        if (owner is { } assignee && assignee != myId)
            Notify(assignee, "lead", $"New follow-up: {item.Title}", $"Due {FormatDue(due)}");
        await db.SaveChangesAsync();
        return (item, null);
    }

    private FollowUp NewItem(Lead lead, string type, string title, string? notes, DateTime due, long? owner, string source, string actor, int reminderDays = 2)
    {
        var item = new FollowUp
        {
            LeadId = lead.Id, AssignedToEmployeeId = owner, Type = type, Title = Truncate(title, 300), Notes = Truncate(notes?.Trim(), 2000),
            DueAt = due, Source = source, CreatedBy = actor,
        };
        var remind = due.AddDays(-reminderDays);
        item.RemindAt = remind > Clock().AddMinutes(5) ? remind : null;
        return item;
    }

    /// <summary>Sets the reminder from the admin setting (called where the setting is known).</summary>
    private async Task<int> ReminderDaysAsync() => int.TryParse(await GetSettingAsync(KeyReminder), out var d) ? Math.Clamp(d, 0, 14) : 2;

    public async Task<(bool Ok, string Message)> CompleteAsync(FollowUp item, FollowUpCompleteRequest r, string actor)
    {
        if (item.Status != "pending") return (false, "This follow-up is already closed.");
        if (!FollowUpOutcomes.All.Contains(r.Outcome)) return (false, "Unknown outcome.");
        if (r.Outcome == FollowUpOutcomes.Callback && r.NextDueAt is null) return (false, "Pick when to call back.");

        var now = Clock();
        var note = r.Note?.Trim();
        item.Status = "done";
        item.CompletedAt = now;
        item.CompletedBy = actor;
        item.Outcome = Truncate(string.IsNullOrEmpty(note) ? r.Outcome : $"{r.Outcome}: {note}", 500);

        var lead = await db.Leads.FirstAsync(l => l.Id == item.LeadId);
        switch (r.Outcome)
        {
            case FollowUpOutcomes.Interested when lead.Status is LeadStatuses.New or LeadStatuses.FollowUp or LeadStatuses.NotInterested:
                await leads.ChangeStatusAsync(lead.Id, LeadStatuses.Interested, note ?? "Follow-up outcome", actor); break;
            case FollowUpOutcomes.NotInterested when lead.Status != LeadStatuses.Converted:
                await leads.ChangeStatusAsync(lead.Id, LeadStatuses.NotInterested, note ?? "Follow-up outcome", actor); break;
            case FollowUpOutcomes.Converted:
                await leads.ChangeStatusAsync(lead.Id, LeadStatuses.Converted, note ?? "Follow-up outcome", actor); break;
        }

        string message = "Follow-up completed";
        if (r.Outcome is FollowUpOutcomes.NoAnswer or FollowUpOutcomes.Callback)
        {
            var next = r.NextDueAt is { } n ? AsUtc(n) : AtTenAm(now, 1);
            if (next <= now) return (false, "Choose a callback time in the future.");
            var again = NewItem(lead, item.Type == FollowUpTypes.Order ? FollowUpTypes.Call : item.Type, item.Title.StartsWith("Retry: ") ? item.Title : "Retry: " + item.Title, null, next, item.AssignedToEmployeeId, "manual", actor, await ReminderDaysAsync());
            again.OrderId = null;
            db.FollowUps.Add(again);
            message = $"Completed. Next follow-up set for {FormatDue(next)}.";
        }

        await db.SaveChangesAsync();
        await CloseJourneyIfFinishedAsync(item);
        return (true, message);
    }

    private async Task CloseJourneyIfFinishedAsync(FollowUp item)
    {
        if (item.EnrollmentId is not { } eid) return;
        if (await db.FollowUps.AnyAsync(f => f.EnrollmentId == eid && f.Status == "pending")) return;
        var e = await db.JourneyEnrollments.FirstOrDefaultAsync(x => x.Id == eid);
        if (e is { Status: "active" }) { e.Status = "completed"; await db.SaveChangesAsync(); }
    }

    public async Task RescheduleAsync(FollowUp item, DateTime dueUtc, long? newOwner, bool changeOwner)
    {
        item.DueAt = dueUtc;
        var remind = dueUtc.AddDays(-await ReminderDaysAsync());
        item.RemindAt = remind > Clock().AddMinutes(5) ? remind : null;
        item.ReminderSentAt = null; item.DueNotifiedAt = null; item.EscalatedAt = null;
        if (changeOwner) item.AssignedToEmployeeId = newOwner;
        await db.SaveChangesAsync();
    }

    // ---------------------------------------------------------------------
    // Messaging
    // ---------------------------------------------------------------------

    private string Fill(string text, Lead lead, string? orderId) => text
        .Replace("{name}", FirstName(lead.Name))
        .Replace("{orderId}", orderId ?? "")
        .Replace("{reviewLink}", store.Value.FrontendUrl.TrimEnd('/') + "/#reviews")
        .Replace("{storeLink}", store.Value.FrontendUrl.TrimEnd('/') + "/shop");

    private static string FirstName(string name) => name.Replace("[Sample]", "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "there";

    private List<string> TemplateParams(FollowUpTemplate t, Lead lead, string? orderId) =>
        t.Params.Select(p => p.ToLowerInvariant() switch
        {
            "name" => FirstName(lead.Name),
            "orderid" => orderId ?? "",
            "reviewlink" => store.Value.FrontendUrl.TrimEnd('/') + "/#reviews",
            "storelink" => store.Value.FrontendUrl.TrimEnd('/') + "/shop",
            _ => "",
        }).ToList();

    /// <summary>Sends the task's WhatsApp message: free text inside the 24h window, otherwise the configured template.</summary>
    public async Task<(bool Ok, string Message)> SendNowAsync(FollowUp item, string actor)
    {
        if (item.Status != "pending") return (false, "This follow-up is already closed.");
        if (string.IsNullOrWhiteSpace(item.SuggestedMessage)) return (false, "This task has no message to send. Use the chat instead.");
        var lead = await db.Leads.FirstAsync(l => l.Id == item.LeadId);
        if (lead.ConsentStatus == "opted_out") return (false, "This customer opted out of messages.");

        var window = await db.Conversations.AsNoTracking().Where(c => c.LeadId == lead.Id).Select(c => c.ServiceWindowExpiresAt).FirstOrDefaultAsync();
        bool sent;
        if (window is { } w && w > Clock()) sent = await whatsapp.SendTextAsync(lead.Phone, item.SuggestedMessage, item.OrderId, actor);
        else if (item.StepKey is { } key && options.Value.Templates.TryGetValue(key, out var t) && !string.IsNullOrWhiteSpace(t.Name))
            sent = await whatsapp.SendTemplateAsync(lead.Phone, t.Name, TemplateParams(t, lead, item.OrderId), item.OrderId, actor);
        else return (false, "The 24-hour reply window is closed and no approved template is set for this step. Call the customer or wait for them to message.");

        if (!sent) return (false, whatsapp.LastError ?? "WhatsApp could not send the message. Check the WhatsApp credentials in the server configuration.");
        item.Status = "done"; item.CompletedAt = Clock(); item.CompletedBy = actor; item.Outcome = "Sent on WhatsApp";
        await db.SaveChangesAsync();
        await CloseJourneyIfFinishedAsync(item);
        return (true, "Message sent and task completed.");
    }

    /// <summary>Sends every message in <paramref name="pending"/> one by one, stopping as soon as the monthly WhatsApp limit is reached.</summary>
    public async Task<(int Sent, int Failed, int Skipped, string Message)> SendDueAsync(IQueryable<FollowUp> pending, string actor, int max = 50)
    {
        var items = await pending.Where(f => f.Status == "pending" && f.SuggestedMessage != null && f.SuggestedMessage != "").OrderBy(f => f.DueAt).Take(max).ToListAsync();
        int sent = 0, failed = 0, skipped = 0;
        string? firstProblem = null;
        foreach (var item in items)
        {
            var (ok, message) = await SendNowAsync(item, actor);
            if (ok) { sent++; continue; }
            if (whatsapp.LimitReached) { firstProblem = message; skipped = items.Count - sent - failed; break; }
            failed++; firstProblem ??= message;
        }
        var text = items.Count == 0 ? "There are no messages waiting to be sent."
            : $"Sent {sent} message{(sent == 1 ? "" : "s")}." + (failed > 0 ? $" {failed} could not be sent." : "") + (skipped > 0 ? $" {skipped} not attempted because the monthly limit was reached." : "") + (firstProblem is not null && sent < items.Count ? $" {firstProblem}" : "");
        return (sent, failed, skipped, text);
    }

    // ---------------------------------------------------------------------
    // Background cycle
    // ---------------------------------------------------------------------

    public async Task RunCycleAsync()
    {
        var started = await EnsureStartedAtAsync();
        var settings = await GetSettingsAsync();
        await SyncOrdersAsync(settings, started);
        await AddMissingJourneyStepsAsync();
        await SendRemindersAsync();
        if (settings.AutoSend) await AutoSendAsync();
        await EscalateAsync();
    }

    private async Task SyncOrdersAsync(FollowUpSettingsDto settings, DateTime started)
    {
        var since = started > Clock().AddDays(-90) ? started : Clock().AddDays(-90);
        var orders = await db.Orders.AsNoTracking()
            .Where(o => o.CreatedAt >= since && (o.PaymentStatus == "PAID" || o.PaymentStatus == "COD_PENDING" || o.PaymentStatus == "COD_COLLECTED"))
            .ToListAsync();
        if (orders.Count == 0) return;

        var existingSteps = (await db.FollowUps.AsNoTracking().Where(f => f.OrderId != null && f.StepKey != null).Select(f => f.OrderId + "|" + f.StepKey).ToListAsync()).ToHashSet();
        var enrolled = (await db.JourneyEnrollments.AsNoTracking().Select(e => e.OrderId).ToListAsync()).ToHashSet();
        var now = Clock();

        foreach (var o in orders)
        {
            try
            {
                var lead = await leads.UpsertFromOrderAsync(o);

                // ---- Order progress: raise a task when something needs a human ----
                async Task Raise(string step, string title, string notes)
                {
                    if (!existingSteps.Add(o.Id + "|" + step)) return;
                    var item = NewItem(lead, FollowUpTypes.Order, title, notes, now, lead.AssignedToEmployeeId, "system", "system");
                    item.OrderId = o.Id; item.StepKey = step; item.RemindAt = null;
                    db.FollowUps.Add(item);
                    Notify(lead.AssignedToEmployeeId, "order", title, notes, "/admin/followups");
                    await SaveQuietlyAsync();
                }

                var active = o.FulfillmentStatus is not ("DELIVERED" or "CANCELLED" or "RTO");
                if (o.FulfillmentStatus == "PENDING" && string.IsNullOrEmpty(o.ShiprocketAwb) && now - o.CreatedAt > TimeSpan.FromHours(2))
                    await Raise("order-unbooked", $"Order {o.Id} has no courier booking", $"Placed {FormatDue(o.CreatedAt)}. {Truncate(o.Notes, 220)}");
                if (active && o.FulfillmentStatus is "AWB_ASSIGNED" or "PICKED_UP" or "IN_TRANSIT" or "OUT_FOR_DELIVERY" or "SHIPMENT_CREATED"
                    && now - (o.LastShippingEventAt ?? o.UpdatedAt) > TimeSpan.FromDays(3))
                    await Raise("order-stuck", $"Shipment for order {o.Id} has not moved in 3 days", $"Status: {o.DeliveryStatus}. AWB {o.ShiprocketAwb}. Check the courier and update the customer.");
                if (o.FulfillmentStatus == "RTO")
                    await Raise("order-rto", $"Order {o.Id} is returning to origin", "Call the customer to confirm the address and arrange re-delivery or a refund.");
                if (o.FulfillmentStatus == "CANCELLED")
                    await Raise("order-cancelled", $"Order {o.Id} shipment was cancelled", "Contact the customer and rebook or refund.");

                // ---- 15-day journey starts at delivery ----
                var delivered = o.FulfillmentStatus == "DELIVERED" ? o.DeliveredAt ?? o.UpdatedAt : (DateTime?)null;
                if (settings.JourneyEnabled && delivered is { } anchor && !enrolled.Contains(o.Id))
                {
                    await EnrollAsync(lead, o, anchor);
                    enrolled.Add(o.Id);
                }
            }
            catch (Exception ex)
            {
                db.ChangeTracker.Clear();
                log.LogWarning(ex, "[FollowUp] Order sync failed for {Order}", o.Id);
            }
        }
    }

    private async Task SaveQuietlyAsync()
    {
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException ex) { log.LogInformation("[FollowUp] Duplicate system task ignored: {Msg}", ex.GetBaseException().Message); db.ChangeTracker.Clear(); }
    }

    private async Task EnrollAsync(Lead lead, Order order, DateTime anchor)
    {
        var enrollment = new JourneyEnrollment { LeadId = lead.Id, OrderId = order.Id, AnchorAt = anchor };
        db.JourneyEnrollments.Add(enrollment);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); return; } // another worker enrolled it first

        var days = await ReminderDaysAsync();
        foreach (var step in JourneyPlan.Steps)
        {
            var due = AtTenAm(anchor, step.Day);
            var item = NewItem(lead, step.Type, step.Title, step.Type == FollowUpTypes.Call ? step.Message : null, due, lead.AssignedToEmployeeId, "journey", "journey");
            item.OrderId = order.Id; item.EnrollmentId = enrollment.Id; item.StepKey = step.Key;
            item.SuggestedMessage = step.Type == FollowUpTypes.Call ? null : Fill(step.Message, lead, order.Id);
            var remind = due.AddDays(-days);
            item.RemindAt = days > 0 && remind > Clock().AddMinutes(5) ? remind : null;
            db.FollowUps.Add(item);
        }
        Notify(lead.AssignedToEmployeeId, "info", $"15-day follow-up started for {lead.Name}", $"Order {order.Id} was delivered. {JourneyPlan.Steps.Length} steps are scheduled; the first is tomorrow.");
        await SaveQuietlyAsync();
        log.LogInformation("[FollowUp] Journey started for lead {Lead} order {Order}", lead.Id, order.Id);
    }

    /// <summary>
    /// Journeys that started before a step was added (for example Day 5 and Day 15) get that step too, as long as its day has not already passed.
    /// </summary>
    private async Task AddMissingJourneyStepsAsync()
    {
        var since = Clock().AddDays(-20);
        var active = await db.JourneyEnrollments.Where(e => e.Status == "active" && e.AnchorAt >= since).Take(200).ToListAsync();
        if (active.Count == 0) return;
        var ids = active.Select(e => e.Id).ToList();
        var have = (await db.FollowUps.AsNoTracking().Where(f => f.EnrollmentId != null && ids.Contains(f.EnrollmentId.Value)).Select(f => f.EnrollmentId + "|" + f.StepKey).ToListAsync()).ToHashSet();
        var days = await ReminderDaysAsync();
        var now = Clock();
        var added = 0;
        foreach (var e in active)
        {
            var lead = await db.Leads.FirstOrDefaultAsync(l => l.Id == e.LeadId);
            if (lead is null) continue;
            foreach (var step in JourneyPlan.Steps)
            {
                if (have.Contains(e.Id + "|" + step.Key)) continue;
                var due = AtTenAm(e.AnchorAt, step.Day);
                if (due < now.AddDays(-1)) continue; // that moment is long gone; do not create a stale task
                var item = NewItem(lead, step.Type, step.Title, step.Type == FollowUpTypes.Call ? step.Message : null, due, lead.AssignedToEmployeeId, "journey", "journey");
                item.OrderId = e.OrderId; item.EnrollmentId = e.Id; item.StepKey = step.Key;
                item.SuggestedMessage = step.Type == FollowUpTypes.Call ? null : Fill(step.Message, lead, e.OrderId);
                var remind = due.AddDays(-days);
                item.RemindAt = days > 0 && remind > now.AddMinutes(5) ? remind : null;
                db.FollowUps.Add(item);
                added++;
            }
        }
        if (added > 0) { await SaveQuietlyAsync(); log.LogInformation("[FollowUp] Added {Count} missing journey step(s) to running journeys", added); }
    }

    private async Task SendRemindersAsync()
    {
        var now = Clock();
        var names = await db.Leads.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Name);

        // Advance heads-up (normally 2 days before).
        var heads = await db.FollowUps.Where(f => f.Status == "pending" && f.RemindAt != null && f.RemindAt <= now && f.ReminderSentAt == null).Take(200).ToListAsync();
        foreach (var f in heads)
        {
            f.ReminderSentAt = now;
            if (f.DueAt <= now) continue; // too late for a heads-up; the due notice covers it
            var days = Math.Max(1, (int)Math.Round((f.DueAt - now).TotalDays));
            Notify(f.AssignedToEmployeeId, "reminder", $"Coming up in {days} day{(days == 1 ? "" : "s")}: {f.Title}", $"{names.GetValueOrDefault(f.LeadId)} · due {FormatDue(f.DueAt)}");
        }

        // Due now.
        var due = await db.FollowUps.Where(f => f.Status == "pending" && f.DueAt <= now && f.DueNotifiedAt == null).Take(200).ToListAsync();
        foreach (var f in due)
        {
            f.DueNotifiedAt = now;
            Notify(f.AssignedToEmployeeId, "due", $"Due now: {f.Title}", names.GetValueOrDefault(f.LeadId));
        }
        if (heads.Count + due.Count > 0) await db.SaveChangesAsync();
    }

    private async Task EscalateAsync()
    {
        var cutoff = Clock().AddHours(-24);
        var late = await db.FollowUps.Where(f => f.Status == "pending" && f.DueAt < cutoff && f.EscalatedAt == null).Take(100).ToListAsync();
        if (late.Count == 0) return;
        var owners = await db.Employees.AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.Name);
        foreach (var f in late)
        {
            f.EscalatedAt = Clock();
            Notify(null, "overdue", $"Overdue by a day: {f.Title}", $"Owner: {(f.AssignedToEmployeeId is { } id ? owners.GetValueOrDefault(id) : "unassigned")}");
        }
        await db.SaveChangesAsync();
    }

    /// <summary>Journey messages go out by template only when an admin switched auto-send on, a template exists, the customer opted in, and it is between 9am and 8pm IST.</summary>
    private async Task AutoSendAsync()
    {
        var hour = IstNow.Hour;
        if (hour < 9 || hour >= 20) return;
        if (await whatsapp.IsLimitReachedAsync()) return; // nothing is sent once the monthly allowance is used; the tasks stay in the list
        var now = Clock();
        var items = await db.FollowUps.Where(f => f.Status == "pending" && f.Source == "journey" && f.DueAt <= now
            && (f.Type == FollowUpTypes.WhatsApp || f.Type == FollowUpTypes.Review || f.Type == FollowUpTypes.Reorder)).OrderBy(f => f.DueAt).Take(25).ToListAsync();
        foreach (var f in items)
        {
            if (f.StepKey is not { } key || !options.Value.Templates.TryGetValue(key, out var t) || string.IsNullOrWhiteSpace(t.Name)) continue;
            var lead = await db.Leads.FirstAsync(l => l.Id == f.LeadId);
            if (lead.ConsentStatus != "opted_in") continue;
            if (!await whatsapp.SendTemplateAsync(lead.Phone, t.Name, TemplateParams(t, lead, f.OrderId), f.OrderId))
            {
                log.LogWarning("[FollowUp] Auto-send of {Step} to lead {Lead} failed; left as a task", key, lead.Id);
                if (whatsapp.LimitReached) return;
                continue;
            }
            f.Status = "done"; f.CompletedAt = now; f.CompletedBy = "auto"; f.Outcome = $"Sent automatically ({t.Name})";
            await db.SaveChangesAsync();
            await CloseJourneyIfFinishedAsync(f);
        }
    }

    // ---------------------------------------------------------------------
    // Reports
    // ---------------------------------------------------------------------

    private sealed record Slim(long Id, string Type, string Status, DateTime DueAt, DateTime? CompletedAt, string? CompletedBy, long? AssignedToEmployeeId, string? StepKey, string Source);

    public async Task<FollowUpReportResponse> ReportAsync(int days, long? onlyEmployee)
    {
        days = Math.Clamp(days, 1, 365);
        var todayStart = TodayStartUtc();
        var tomorrow = todayStart.AddDays(1);
        var from = todayStart.AddDays(-(days - 1));

        var q = db.FollowUps.AsNoTracking().Where(f => f.DueAt >= from && f.DueAt < tomorrow);
        if (onlyEmployee is { } me) q = q.Where(f => f.AssignedToEmployeeId == me);
        var items = await q.Select(f => new Slim(f.Id, f.Type, f.Status, f.DueAt, f.CompletedAt, f.CompletedBy, f.AssignedToEmployeeId, f.StepKey, f.Source)).ToListAsync();

        bool OnTime(DateTime due, DateTime? done) => done is { } d && d < TimeZoneInfo.ConvertTimeToUtc(TimeZoneInfo.ConvertTimeFromUtc(due, Ist).Date.AddDays(1), Ist);
        bool IsDone(string s) => s == "done";
        bool IsOverdue(string s, DateTime due) => s == "pending" && due < todayStart;

        var names = await db.Employees.AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.Name);
        FollowUpReportRow Row(string label, long? emp, IEnumerable<Slim> set)
        {
            var list = set.ToList();
            var done = list.Count(x => IsDone(x.Status));
            return new FollowUpReportRow
            {
                Label = label, EmployeeId = emp, Due = list.Count, Done = done,
                Overdue = list.Count(x => IsOverdue(x.Status, x.DueAt)),
                OnTimePercent = done == 0 ? 0 : (int)Math.Round(100.0 * list.Count(x => IsDone(x.Status) && OnTime(x.DueAt, x.CompletedAt)) / done),
            };
        }

        var report = new FollowUpReportResponse
        {
            Success = true, Days = days, From = from,
            Due = items.Count, Done = items.Count(i => IsDone(i.Status)),
            DoneOnTime = items.Count(i => IsDone(i.Status) && OnTime(i.DueAt, i.CompletedAt)),
            Overdue = items.Count(i => IsOverdue(i.Status, i.DueAt)),
            Pending = items.Count(i => i.Status == "pending" && !IsOverdue(i.Status, i.DueAt)),
            AutoSent = items.Count(i => i.CompletedBy == "auto"),
        };
        report.CompletionRate = report.Due == 0 ? 0 : (int)Math.Round(100.0 * report.Done / report.Due);
        report.OnTimeRate = report.Done == 0 ? 0 : (int)Math.Round(100.0 * report.DoneOnTime / report.Done);

        // By employee (admins) — includes leads owned and converted in the period.
        var owned = await db.Leads.AsNoTracking().Where(l => l.AssignedToEmployeeId != null).GroupBy(l => l.AssignedToEmployeeId!.Value)
            .Select(g => new { Id = g.Key, Open = g.Count(l => l.Status != LeadStatuses.Converted && l.Status != LeadStatuses.NotInterested), Won = g.Count(l => l.ConvertedAt != null && l.ConvertedAt >= from) })
            .ToDictionaryAsync(x => x.Id);
        var groups = items.GroupBy(i => i.AssignedToEmployeeId);
        var empIds = names.Keys.Where(id => onlyEmployee is null || id == onlyEmployee).ToList();
        foreach (var id in empIds)
        {
            var row = Row(names[id], id, items.Where(i => i.AssignedToEmployeeId == id));
            if (owned.TryGetValue(id, out var o)) { row.OpenLeads = o.Open; row.ConvertedLeads = o.Won; }
            report.ByEmployee.Add(row);
        }
        if (onlyEmployee is null && items.Any(i => i.AssignedToEmployeeId == null))
            report.ByEmployee.Add(Row("Unassigned", null, items.Where(i => i.AssignedToEmployeeId == null)));
        report.ByEmployee = report.ByEmployee.OrderByDescending(r => r.Due).ToList();

        report.ByType = items.GroupBy(i => i.Type).Select(g => Row(g.Key, null, g)).OrderByDescending(r => r.Due).ToList();

        // 15-day journey funnel.
        var journey = items.Where(i => i.Source == "journey" && i.StepKey != null).GroupBy(i => i.StepKey!).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var step in JourneyPlan.Steps)
        {
            var list = journey.GetValueOrDefault(step.Key) ?? [];
            report.Journey.Add(new JourneyReportRow
            {
                StepKey = step.Key, Title = step.Title, Day = step.Day, Total = list.Count,
                Done = list.Count(i => IsDone(i.Status)), AutoSent = list.Count(i => i.CompletedBy == "auto"),
                Pending = list.Count(i => i.Status == "pending" && !IsOverdue(i.Status, i.DueAt)),
                Overdue = list.Count(i => IsOverdue(i.Status, i.DueAt)), Skipped = list.Count(i => i.Status == "skipped"),
            });
        }
        report.ReviewRequests = report.Journey.FirstOrDefault(j => j.StepKey == "day8-review")?.Done ?? 0;

        // Funnel numbers (company-wide, admins only see these in the UI).
        var leadsQ = db.Leads.AsNoTracking().Where(l => l.CreatedAt >= from);
        if (onlyEmployee is { } e1) leadsQ = leadsQ.Where(l => l.AssignedToEmployeeId == e1);
        report.NewLeads = await leadsQ.CountAsync();
        var wonQ = db.Leads.AsNoTracking().Where(l => l.ConvertedAt != null && l.ConvertedAt >= from);
        if (onlyEmployee is { } e2) wonQ = wonQ.Where(l => l.AssignedToEmployeeId == e2);
        report.ConvertedLeads = await wonQ.CountAsync();
        report.ReviewsReceived = await db.Reviews.AsNoTracking().CountAsync(r => r.CreatedAt >= from);
        var enrollments = await db.JourneyEnrollments.AsNoTracking().Where(e => e.CreatedAt >= from).Select(e => new { e.LeadId, e.OrderId, e.AnchorAt }).ToListAsync();
        report.JourneysStarted = enrollments.Count;
        if (enrollments.Count > 0)
        {
            var orderIds = enrollments.Select(e => e.OrderId).ToList();
            var phones = await db.Orders.AsNoTracking().Where(o => orderIds.Contains(o.Id)).Select(o => new { o.Id, o.CustomerPhone }).ToListAsync();
            var later = await db.Orders.AsNoTracking().Where(o => o.CreatedAt >= from && !orderIds.Contains(o.Id)).Select(o => new { o.CustomerPhone, o.CreatedAt }).ToListAsync();
            report.Reorders = enrollments.Count(e => phones.Any(p => p.Id == e.OrderId && later.Any(l => l.CustomerPhone == p.CustomerPhone && l.CreatedAt > e.AnchorAt)));
        }

        // Day-by-day trend (capped to the last 31 days so the chart stays readable).
        var span = Math.Min(days, 31);
        for (var i = span - 1; i >= 0; i--)
        {
            var dayStart = todayStart.AddDays(-i);
            var dayEnd = dayStart.AddDays(1);
            var day = items.Where(x => x.DueAt >= dayStart && x.DueAt < dayEnd).ToList();
            report.Trend.Add(new FollowUpTrendPoint { Day = TimeZoneInfo.ConvertTimeFromUtc(dayStart, Ist).ToString("dd MMM", CultureInfo.InvariantCulture), Due = day.Count, Done = day.Count(x => IsDone(x.Status)) });
        }
        return report;
    }

    // ---------------------------------------------------------------------

    public static string FormatDue(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(AsUtc(utc), Ist).ToString("ddd d MMM, h:mm tt", CultureInfo.InvariantCulture);
    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}
