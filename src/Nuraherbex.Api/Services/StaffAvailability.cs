using Microsoft.EntityFrameworkCore;
using Nuraherbex.Api.Data;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Services;

/// <summary>
/// Who can take new work right now. Someone counts as online while their portal keeps checking in (the notification bell does it every 30 seconds),
/// and among the people online the one with the least open work gets the next lead or task.
/// </summary>
public class StaffAvailability(NuraDbContext db)
{
    /// <summary>A staff member who has not checked in for this long is treated as signed out.</summary>
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromMinutes(2);

    public static DateTime OnlineCutoff() => DateTime.UtcNow - OnlineWindow;
    public static bool IsOnline(Employee e) => e.IsActive && e.LastSeenAt is { } s && s >= OnlineCutoff();

    /// <summary>Marks the person as present. Called on sign-in and by every portal check-in.</summary>
    public Task CheckInAsync(long employeeId) =>
        db.Employees.Where(e => e.Id == employeeId).ExecuteUpdateAsync(s => s.SetProperty(e => e.LastSeenAt, DateTime.UtcNow));

    /// <summary>Marks the person as signed out so they stop receiving new work straight away.</summary>
    public Task CheckOutAsync(long employeeId) =>
        db.Employees.Where(e => e.Id == employeeId).ExecuteUpdateAsync(s => s.SetProperty(e => e.LastSeenAt, (DateTime?)null));

    public Task SetWhatsAppActiveAsync(long employeeId, bool active) =>
        db.Employees.Where(e => e.Id == employeeId).ExecuteUpdateAsync(s => s.SetProperty(e => e.WhatsAppActive, active));

    public Task<bool> IsWhatsAppActiveAsync(long employeeId) =>
        db.Employees.AsNoTracking().Where(e => e.Id == employeeId).Select(e => e.WhatsAppActive).FirstOrDefaultAsync();

    /// <summary>Open leads + open tasks + overdue follow-ups per employee: a simple picture of who is busy.</summary>
    public async Task<Dictionary<long, int>> WorkloadAsync()
    {
        var todayStart = FollowUpService.TodayStartUtc();
        var leads = await db.Leads.AsNoTracking().Where(l => l.AssignedToEmployeeId != null && l.Status != LeadStatuses.Converted && l.Status != LeadStatuses.NotInterested)
            .GroupBy(l => l.AssignedToEmployeeId!.Value).Select(g => new { g.Key, N = g.Count() }).ToListAsync();
        var tasks = await db.WorkTasks.AsNoTracking().Where(t => t.AssignedToEmployeeId != null && t.Status != TaskStatuses.Done)
            .GroupBy(t => t.AssignedToEmployeeId!.Value).Select(g => new { g.Key, N = g.Count() }).ToListAsync();
        var late = await db.FollowUps.AsNoTracking().Where(f => f.Status == "pending" && f.DueAt < todayStart && f.AssignedToEmployeeId != null)
            .GroupBy(f => f.AssignedToEmployeeId!.Value).Select(g => new { g.Key, N = g.Count() }).ToListAsync();
        var load = new Dictionary<long, int>();
        foreach (var x in leads) load[x.Key] = load.GetValueOrDefault(x.Key) + x.N;
        foreach (var x in tasks) load[x.Key] = load.GetValueOrDefault(x.Key) + x.N;
        foreach (var x in late) load[x.Key] = load.GetValueOrDefault(x.Key) + x.N;
        return load;
    }

    /// <summary>
    /// The online, active sales employee with the least open work (ties: whoever was given work longest ago).
    /// <paramref name="page"/> limits it to people whose designation lets them open that page; <paramref name="departmentId"/> to one department.
    /// Returns null when nobody is online, so the work waits in the admin queue instead of landing on someone who is away.
    /// </summary>
    public async Task<Employee?> PickAsync(string page, long? departmentId = null, long? excludeId = null, bool whatsAppOnly = false)
    {
        var staff = await EligibleAsync(page, departmentId, excludeId, whatsAppOnly);
        if (staff.Count == 0) return null;
        var load = await WorkloadAsync();
        return staff
            .OrderBy(e => load.GetValueOrDefault(e.Id))
            .ThenBy(e => e.LastAssignedAt == null ? 0 : 1).ThenBy(e => e.LastAssignedAt).ThenBy(e => e.Id)
            .FirstOrDefault();
    }

    /// <summary>Everyone who could take new work of this kind right now: active, sales, online, allowed to open the page, and (when asked) WhatsApp active.</summary>
    public async Task<List<Employee>> EligibleAsync(string page, long? departmentId = null, long? excludeId = null, bool whatsAppOnly = false)
    {
        var rights = await db.Designations.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.RightsJson);
        var cutoff = OnlineCutoff();
        var staff = await db.Employees.Where(e => e.IsActive && e.Role == EmployeeRoles.Sales && e.Id != excludeId && e.LastSeenAt != null && e.LastSeenAt >= cutoff && (!whatsAppOnly || e.WhatsAppActive)
            && (departmentId == null || e.DepartmentId == departmentId)).ToListAsync();
        return staff
            .Where(e => (e.DesignationId is { } id && rights.TryGetValue(id, out var json) ? PageCatalog.LevelFrom(json, page) : PageCatalog.DefaultStaff.GetValueOrDefault(page)) >= PageCatalog.Limited)
            .ToList();
    }
}
