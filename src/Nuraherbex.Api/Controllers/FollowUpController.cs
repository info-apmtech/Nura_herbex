using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nuraherbex.Api.Data;
using Nuraherbex.Api.Services;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Controllers;

/// <summary>
/// Follow-up tasks, reminders and reports. Administrators see every employee's work; sales executives only their own.
/// </summary>
[ApiController]
[Route("api/admin")]
[Authorize(Roles = "admin,sales")]
public class FollowUpController(NuraDbContext db, FollowUpService followUps, WhatsAppService whatsapp, RightsService rights) : StaffControllerBase(rights)
{
    private readonly NuraDbContext _db = db;

    // ---- Tasks -------------------------------------------------------------------------

    /// <param name="scope">overdue | today | upcoming | done | open (default: everything not done)</param>
    /// <param name="assignee">admin only: "unassigned", "me" or an employee id</param>
    [HttpGet("followups")]
    public async Task<FollowUpListResponse> List([FromQuery] string? scope, [FromQuery] string? assignee, [FromQuery] string? type, [FromQuery] long? leadId, [FromQuery] int take = 200)
    {
        var q = _db.FollowUps.AsNoTracking().AsQueryable();
        if (!CanAll("followups")) q = q.Where(f => f.AssignedToEmployeeId == MyId);
        else if (assignee == "unassigned") q = q.Where(f => f.AssignedToEmployeeId == null);
        else if (assignee == "me") q = q.Where(f => f.AssignedToEmployeeId == MyId);
        else if (long.TryParse(assignee, out var aid)) q = q.Where(f => f.AssignedToEmployeeId == aid);
        if (!string.IsNullOrWhiteSpace(type)) q = q.Where(f => f.Type == type);
        if (leadId is { } lid) q = q.Where(f => f.LeadId == lid);

        var today = FollowUpService.TodayStartUtc();
        var tomorrow = today.AddDays(1);
        var recentDone = FollowUpService.Clock().AddDays(-30);
        var counts = new FollowUpCounts
        {
            Overdue = await q.CountAsync(f => f.Status == "pending" && f.DueAt < today),
            Today = await q.CountAsync(f => f.Status == "pending" && f.DueAt >= today && f.DueAt < tomorrow),
            Upcoming = await q.CountAsync(f => f.Status == "pending" && f.DueAt >= tomorrow),
            Done = await q.CountAsync(f => f.Status != "pending" && f.CompletedAt >= recentDone),
            Messages = await q.CountAsync(f => f.Status == "pending" && f.DueAt < tomorrow && f.SuggestedMessage != null && f.SuggestedMessage != ""),
            Sent = await q.CountAsync(f => f.Status == "done" && f.CompletedAt >= recentDone && f.Outcome != null && f.Outcome.StartsWith("Sent")),
        };

        var filtered = scope switch
        {
            "overdue" => q.Where(f => f.Status == "pending" && f.DueAt < today).OrderBy(f => f.DueAt),
            "today" => q.Where(f => f.Status == "pending" && f.DueAt >= today && f.DueAt < tomorrow).OrderBy(f => f.DueAt),
            "upcoming" => q.Where(f => f.Status == "pending" && f.DueAt >= tomorrow).OrderBy(f => f.DueAt),
            "done" => q.Where(f => f.Status != "pending" && f.CompletedAt >= recentDone).OrderByDescending(f => f.CompletedAt),
            "messages" => q.Where(f => f.Status == "pending" && f.DueAt < tomorrow && f.SuggestedMessage != null && f.SuggestedMessage != "").OrderBy(f => f.DueAt),
            "sent" => q.Where(f => f.Status == "done" && f.CompletedAt >= recentDone && f.Outcome != null && f.Outcome.StartsWith("Sent")).OrderByDescending(f => f.CompletedAt),
            _ => q.Where(f => f.Status == "pending").OrderBy(f => f.DueAt),
        };
        var rows = await filtered.Take(Math.Clamp(take, 1, 500)).ToListAsync();
        return new FollowUpListResponse { Success = true, Counts = counts, Items = await MapAsync(rows) };
    }

    private async Task<List<FollowUpDto>> MapAsync(List<FollowUp> rows)
    {
        var leadIds = rows.Select(r => r.LeadId).Distinct().ToList();
        var leadMap = await _db.Leads.AsNoTracking().Where(l => leadIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => new { l.Name, l.Phone });
        var owners = await _db.Employees.AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.Name);
        return rows.Select(r =>
        {
            leadMap.TryGetValue(r.LeadId, out var l);
            return r.ToDto(l?.Name ?? "Unknown lead", l?.Phone ?? "", r.AssignedToEmployeeId is { } id ? owners.GetValueOrDefault(id) : null);
        }).ToList();
    }

    /// <summary>The follow-up if the caller may touch it: any for admins, only their own for sales (404 otherwise).</summary>
    private async Task<FollowUp?> FindAsync(long id)
    {
        var f = await _db.FollowUps.FirstOrDefaultAsync(x => x.Id == id);
        return f is null || (!CanAll("followups") && f.AssignedToEmployeeId != MyId) ? null : f;
    }

    private static NotFoundObjectResult NoItem() => new(new ApiResult { Success = false, Message = "Follow-up not found" });

    [HttpPost("followups")]
    public async Task<IActionResult> Create(FollowUpCreateRequest r)
    {
        var (item, error) = await followUps.CreateManualAsync(r, MyName, CanAll("followups"), MyId);
        if (item is null) return BadRequest(new ApiResult { Success = false, Message = error });
        return StatusCode(201, new ApiResult { Success = true, Message = $"Follow-up scheduled for {FollowUpService.FormatDue(item.DueAt)}" });
    }

    [HttpPut("followups/{id:long}/complete")]
    public async Task<IActionResult> Complete(long id, FollowUpCompleteRequest r)
    {
        var item = await FindAsync(id);
        if (item is null) return NoItem();
        var (ok, message) = await followUps.CompleteAsync(item, r, MyName);
        return ok ? Ok(new ApiResult { Success = true, Message = message }) : BadRequest(new ApiResult { Success = false, Message = message });
    }

    [HttpPut("followups/{id:long}/reschedule")]
    public async Task<IActionResult> Reschedule(long id, FollowUpRescheduleRequest r)
    {
        var item = await FindAsync(id);
        if (item is null) return NoItem();
        if (item.Status != "pending") return BadRequest(new ApiResult { Success = false, Message = "This follow-up is already closed." });
        var due = r.DueAt.Kind == DateTimeKind.Local ? r.DueAt.ToUniversalTime() : DateTime.SpecifyKind(r.DueAt, DateTimeKind.Utc);
        if (due < FollowUpService.Clock().AddMinutes(-5)) return BadRequest(new ApiResult { Success = false, Message = "Choose a time in the future." });
        if (CanAll("followups") && r.AssignedToEmployeeId is { } emp && !await _db.Employees.AnyAsync(e => e.Id == emp && e.IsActive))
            return BadRequest(new ApiResult { Success = false, Message = "That employee is not active." });
        await followUps.RescheduleAsync(item, due, r.AssignedToEmployeeId, changeOwner: CanAll("followups") && r.AssignedToEmployeeId is not null);
        return Ok(new ApiResult { Success = true, Message = $"Rescheduled to {FollowUpService.FormatDue(due)}" });
    }

    /// <summary>Close a task without doing it (e.g. the customer already ordered).</summary>
    [HttpPut("followups/{id:long}/skip")]
    public async Task<IActionResult> Skip(long id)
    {
        var item = await FindAsync(id);
        if (item is null) return NoItem();
        if (item.Status != "pending") return BadRequest(new ApiResult { Success = false, Message = "This follow-up is already closed." });
        item.Status = "skipped"; item.CompletedAt = FollowUpService.Clock(); item.CompletedBy = MyName; item.Outcome = "skipped";
        await _db.SaveChangesAsync();
        return Ok(new ApiResult { Success = true, Message = "Follow-up skipped" });
    }

    [HttpPost("followups/{id:long}/send")]
    public async Task<IActionResult> Send(long id)
    {
        var item = await FindAsync(id);
        if (item is null) return NoItem();
        var (ok, message) = await followUps.SendNowAsync(item, MyName);
        return ok ? Ok(new ApiResult { Success = true, Message = message }) : BadRequest(new ApiResult { Success = false, Message = message });
    }

    /// <summary>Sends every WhatsApp message that is due today or earlier (the caller's own, or everyone's with full access). Stops when the monthly limit is reached.</summary>
    [HttpPost("followups/send-due")]
    public async Task<IActionResult> SendDue()
    {
        var tomorrow = FollowUpService.TodayStartUtc().AddDays(1);
        var q = _db.FollowUps.Where(f => f.Status == "pending" && f.DueAt < tomorrow);
        if (!CanAll("followups")) q = q.Where(f => f.AssignedToEmployeeId == MyId);
        var (sent, failed, skipped, message) = await followUps.SendDueAsync(q, MyName);
        return Ok(new ApiResult { Success = sent > 0 || (failed == 0 && skipped == 0), Message = message });
    }

    /// <summary>Usage of the monthly WhatsApp allowance, shown above the follow-up list so staff know before sending.</summary>
    [HttpGet("followups/whatsapp-usage")]
    public Task<WhatsAppLimitDto> WhatsAppUsage() => whatsapp.GetLimitStatusAsync();

    // ---- Settings & journey plan ----------------------------------------------------------

    [HttpGet("followups/settings")]
    public async Task<FollowUpSettingsDto> GetSettings() => await followUps.GetSettingsAsync();

    [HttpPut("followups/settings")]
    public async Task<IActionResult> SaveSettings(FollowUpSettingsRequest r)
    {
        await followUps.SaveSettingsAsync(r);
        return Ok(new ApiResult { Success = true, Message = "Settings saved" });
    }

    // ---- Reports ---------------------------------------------------------------------------

    [HttpGet("crm/dashboard")]
    public async Task<CrmDashboardResponse> CrmDashboard([FromServices] CrmDashboardService crm, [FromQuery] int days = 30) =>
        await crm.BuildAsync(days, CanAll("crm") ? null : MyId);

    [HttpGet("followups/report")]
    public async Task<FollowUpReportResponse> Report([FromQuery] int days = 30) =>
        await followUps.ReportAsync(days, CanAll("followups") ? null : MyId);

    // ---- Notifications ----------------------------------------------------------------------

    private IQueryable<Notification> MyNotifications() =>
        _db.Notifications.Where(n => n.EmployeeId == MyId || ((IsAdmin || CanAll("followups")) && n.EmployeeId == null));

    [HttpGet("notifications")]
    public async Task<NotificationListResponse> Notifications([FromServices] StaffAvailability presence)
    {
        if (MyId is { } me) await presence.CheckInAsync(me); // the bell polls every 30 s, which doubles as the online signal
        var q = MyNotifications().AsNoTracking();
        var items = await q.OrderByDescending(n => n.CreatedAt).Take(30).ToListAsync();
        return new NotificationListResponse
        {
            Success = true, Unread = await q.CountAsync(n => n.ReadAt == null),
            Items = items.Select(n => new NotificationDto { Id = n.Id, Kind = n.Kind, Title = n.Title, Body = n.Body, Link = n.Link, CreatedAt = n.CreatedAt, IsRead = n.ReadAt != null }).ToList(),
        };
    }

    /// <summary>Called on sign-out so the person stops receiving auto-assigned work at once.</summary>
    [HttpPost("presence/offline")]
    public async Task<IActionResult> GoOffline([FromServices] StaffAvailability presence, [FromServices] LeadService leads)
    {
        if (MyId is { } me) { await presence.CheckOutAsync(me); await leads.SwitchOffWhatsAppAsync(me, "Owner signed out", "auto"); }
        return Ok(new ApiResult { Success = true });
    }

    /// <summary>Staff switch for receiving auto-assigned WhatsApp leads.</summary>
    [HttpGet("presence/whatsapp")]
    public async Task<WhatsAppActiveResponse> GetWhatsAppActive([FromServices] StaffAvailability presence) =>
        new() { Success = true, Active = MyId is { } me && await presence.IsWhatsAppActiveAsync(me) };

    [HttpPut("presence/whatsapp")]
    public async Task<WhatsAppActiveResponse> SetWhatsAppActive([FromBody] WhatsAppActiveRequest req, [FromServices] StaffAvailability presence, [FromServices] LeadService leads)
    {
        if (MyId is not { } me) return new() { Success = false, Message = "Only team members with their own login can switch this." };
        var given = 0;
        if (req.Active)
        {
            await presence.SetWhatsAppActiveAsync(me, true);
            await presence.CheckInAsync(me); // switching on counts as being here right now
            given = await leads.AssignWaitingAsync() + await leads.RebalanceAsync(); // hand over leads that were waiting, without waiting for the 2-minute worker
        }
        else await leads.SwitchOffWhatsAppAsync(me, "Owner switched WhatsApp off", MyName); // their not-yet-started leads move to someone active
        return new() { Success = true, Active = req.Active, Message = req.Active ? (given > 0 ? $"You are WhatsApp active. {given} lead(s) were shared out." : "You are WhatsApp active. New WhatsApp leads can now come to you.") : "WhatsApp off. You will not get new WhatsApp leads." };
    }

    [HttpPut("notifications/read-all")]
    public async Task<IActionResult> ReadAll()
    {
        var now = FollowUpService.Clock();
        foreach (var n in await MyNotifications().Where(n => n.ReadAt == null).ToListAsync()) n.ReadAt = now;
        await _db.SaveChangesAsync();
        return Ok(new ApiResult { Success = true });
    }

    [HttpPut("notifications/{id:long}/read")]
    public async Task<IActionResult> Read(long id)
    {
        var n = await MyNotifications().FirstOrDefaultAsync(x => x.Id == id);
        if (n is not null && n.ReadAt is null) { n.ReadAt = FollowUpService.Clock(); await _db.SaveChangesAsync(); }
        return Ok(new ApiResult { Success = true });
    }
}
