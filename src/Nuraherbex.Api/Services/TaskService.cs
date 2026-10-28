using Microsoft.EntityFrameworkCore;
using Nuraherbex.Api.Data;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Services;

/// <summary>Team task management: owners, due dates, priorities, checklists, comments, repeats and reminders.</summary>
public class TaskService(NuraDbContext db, ILogger<TaskService> log, StaffAvailability availability)
{
    public sealed record Caller(long? EmployeeId, string Name, bool CanSeeAll);

    private static readonly string[] OpenStatuses = [TaskStatuses.Todo, TaskStatuses.InProgress, TaskStatuses.Blocked];

    /// <summary>Finished tasks stay visible (Done chip, Done column of the board) for this many days. One number, so the chip and the board always agree.</summary>
    public const int DoneWindowDays = 30;

    // ---------------------------------------------------------------------
    // Visibility and permissions
    // ---------------------------------------------------------------------

    /// <summary>Tasks this person may see: everything with the top task right, otherwise only their own tasks (each assignee has their own copy).
    /// Tasks they created and left unassigned stay visible too, so they are not lost.</summary>
    public IQueryable<WorkTask> Visible(Caller c) =>
        c.CanSeeAll ? db.WorkTasks : db.WorkTasks.Where(t => t.AssignedToEmployeeId == c.EmployeeId || (t.AssignedToEmployeeId == null && t.CreatedByEmployeeId == c.EmployeeId));

    public static bool CanEdit(WorkTask t, Caller c) => c.CanSeeAll || (c.EmployeeId is { } me && (t.CreatedByEmployeeId == me || t.AssignedToEmployeeId == me));
    public static bool CanDelete(WorkTask t, Caller c) => c.CanSeeAll || (c.EmployeeId is { } me && t.CreatedByEmployeeId == me);

    // ---------------------------------------------------------------------
    // Reading
    // ---------------------------------------------------------------------

    private sealed record Names(Dictionary<long, string> Employees, Dictionary<long, string> Departments, Dictionary<long, string> Leads);

    private async Task<Names> NamesAsync(IEnumerable<WorkTask> tasks)
    {
        var leadIds = tasks.Where(t => t.LeadId != null).Select(t => t.LeadId!.Value).Distinct().ToList();
        return new Names(
            await db.Employees.AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.Name),
            await db.Departments.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Name),
            leadIds.Count == 0 ? [] : await db.Leads.AsNoTracking().Where(l => leadIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.Name));
    }

    private TaskDto ToDto(WorkTask t, Names n, Caller c, int checkDone, int checkTotal, int comments) => new()
    {
        Id = t.Id, Title = t.Title, Description = t.Description, Status = t.Status, Priority = t.Priority, StartAt = t.StartAt, DueAt = t.DueAt,
        IsOverdue = t.Status != TaskStatuses.Done && t.DueAt is { } d && d < DateTime.UtcNow,
        AssignedToEmployeeId = t.AssignedToEmployeeId, AssignedToName = t.AssignedToEmployeeId is { } a ? n.Employees.GetValueOrDefault(a) : null,
        CreatedByEmployeeId = t.CreatedByEmployeeId, CreatedByName = t.CreatedByName,
        DepartmentId = t.DepartmentId, DepartmentName = t.DepartmentId is { } dp ? n.Departments.GetValueOrDefault(dp) : null,
        LeadId = t.LeadId, LeadName = t.LeadId is { } l ? n.Leads.GetValueOrDefault(l) : null, OrderId = t.OrderId, Tags = t.Tags,
        Repeat = t.Repeat, ChecklistDone = checkDone, ChecklistTotal = checkTotal, CommentCount = comments,
        CreatedAt = t.CreatedAt, UpdatedAt = t.UpdatedAt, CompletedAt = t.CompletedAt, CanEdit = CanEdit(t, c),
    };

    public sealed record Filter(string? Scope, string? Assignee, long? DepartmentId, string? Priority, string? Status, string? Search, long? LeadId, bool CreatedByMe, int Take);

    public async Task<TaskListResponse> ListAsync(Caller c, Filter f)
    {
        var visible = Visible(c).AsNoTracking();
        var now = DateTime.UtcNow;
        var todayStart = FollowUpService.TodayStartUtc();
        var tomorrow = todayStart.AddDays(1);
        var doneSince = now.AddDays(-DoneWindowDays);
        var open = visible.Where(t => t.Status != TaskStatuses.Done);

        // The filters the person picked (who, department, priority, search...) apply to the counts too, so a chip always matches what its list shows.
        // Only the status scope is left out: that is what the chips themselves switch between.
        var q = visible;
        if (f.Assignee == "me") q = q.Where(t => t.AssignedToEmployeeId == c.EmployeeId);
        else if (f.Assignee == "unassigned") q = q.Where(t => t.AssignedToEmployeeId == null);
        else if (long.TryParse(f.Assignee, out var aid)) q = q.Where(t => t.AssignedToEmployeeId == aid);
        if (f.CreatedByMe) q = q.Where(t => t.CreatedByEmployeeId == c.EmployeeId);
        if (f.DepartmentId is { } dep) q = q.Where(t => t.DepartmentId == dep);
        if (!string.IsNullOrWhiteSpace(f.Priority)) q = q.Where(t => t.Priority == f.Priority);
        if (f.LeadId is { } lead) q = q.Where(t => t.LeadId == lead);
        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            var s = f.Search.Trim();
            q = q.Where(t => t.Title.Contains(s) || (t.Tags != null && t.Tags.Contains(s)) || (t.Description != null && t.Description.Contains(s)));
        }

        // Pending shows only what needs attention now: overdue, due today, or no date. Later tasks appear on their own day.
        var qOpen = q.Where(t => t.Status != TaskStatuses.Done && (t.StartAt != null ? t.StartAt < tomorrow : (t.DueAt == null || t.DueAt < tomorrow)));
        var counts = new TaskCounts
        {
            Open = await qOpen.CountAsync(),
            Upcoming = await q.CountAsync(t => t.Status != TaskStatuses.Done && (t.StartAt != null ? t.StartAt >= tomorrow : (t.DueAt != null && t.DueAt >= tomorrow))),
            Overdue = await qOpen.CountAsync(t => t.DueAt != null && t.DueAt < now),
            DueToday = await qOpen.CountAsync(t => t.DueAt != null && t.DueAt >= todayStart && t.DueAt < tomorrow),
            Done = await q.CountAsync(t => t.Status == TaskStatuses.Done && t.CompletedAt >= doneSince),
            Mine = await open.CountAsync(t => t.AssignedToEmployeeId == c.EmployeeId),
            Unassigned = await open.CountAsync(t => t.AssignedToEmployeeId == null),
        };

        switch (f.Scope)
        {
            case "overdue": q = q.Where(t => t.Status != TaskStatuses.Done && t.DueAt != null && t.DueAt < now); break;
            case "today": q = q.Where(t => t.Status != TaskStatuses.Done && t.DueAt != null && t.DueAt >= todayStart && t.DueAt < tomorrow); break;
            case "done": q = q.Where(t => t.Status == TaskStatuses.Done && t.CompletedAt >= doneSince); break;
            case "board": q = q.Where(t => t.Status != TaskStatuses.Done || t.CompletedAt >= doneSince); break;
            case "all": break;
            case "upcoming": q = q.Where(t => t.Status != TaskStatuses.Done && (t.StartAt != null ? t.StartAt >= tomorrow : (t.DueAt != null && t.DueAt >= tomorrow))); break;
            default: q = q.Where(t => t.Status != TaskStatuses.Done && (t.StartAt != null ? t.StartAt < tomorrow : (t.DueAt == null || t.DueAt < tomorrow))); break;
        }
        if (!string.IsNullOrWhiteSpace(f.Status)) q = q.Where(t => t.Status == f.Status);

        var rows = (await q.Take(1000).ToListAsync())
            .OrderBy(t => t.Status == TaskStatuses.Done ? 1 : 0)
            .ThenByDescending(t => f.Scope == "done" ? 0 : PriorityRank(t.Priority))
            .ThenBy(t => f.Scope == "upcoming" ? (t.StartAt ?? t.DueAt ?? DateTime.MaxValue) : (t.DueAt ?? DateTime.MaxValue))
            .ThenByDescending(t => t.Status == TaskStatuses.Done ? t.CompletedAt : null)
            .Take(Math.Clamp(f.Take, 1, 500)).ToList();

        var ids = rows.Select(r => r.Id).ToList();
        var checks = await db.TaskChecklistItems.AsNoTracking().Where(i => ids.Contains(i.TaskId)).GroupBy(i => i.TaskId).Select(g => new { g.Key, Total = g.Count(), Done = g.Count(i => i.Done) }).ToDictionaryAsync(x => x.Key);
        var comments = await db.TaskComments.AsNoTracking().Where(i => ids.Contains(i.TaskId)).GroupBy(i => i.TaskId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        // Newest comment per task, shown under the title in the list.
        var latest = (await db.TaskComments.AsNoTracking().Where(i => ids.Contains(i.TaskId)).OrderByDescending(i => i.CreatedAt).ThenByDescending(i => i.Id).Select(i => new { i.TaskId, i.Author, i.Body, i.CreatedAt }).ToListAsync())
            .GroupBy(i => i.TaskId).ToDictionary(g => g.Key, g => g.First());
        var names = await NamesAsync(rows);
        return new TaskListResponse
        {
            Success = true, Counts = counts,
            Items = rows.Select(t =>
            {
                var dto = ToDto(t, names, c, checks.TryGetValue(t.Id, out var ck) ? ck.Done : 0, ck?.Total ?? 0, comments.GetValueOrDefault(t.Id));
                if (latest.TryGetValue(t.Id, out var lc)) { dto.LastCommentBy = lc.Author; dto.LastCommentAt = lc.CreatedAt; dto.LastComment = lc.Body.Length > 160 ? lc.Body[..160] + "…" : lc.Body; }
                return dto;
            }).ToList(),
        };
    }

    private static int PriorityRank(string p) => p switch { TaskPriorities.Urgent => 4, TaskPriorities.High => 3, TaskPriorities.Normal => 2, _ => 1 };

    public async Task<TaskDetailResponse?> DetailAsync(long id, Caller c)
    {
        var t = await Visible(c).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (t is null) return null;
        var items = await db.TaskChecklistItems.AsNoTracking().Where(i => i.TaskId == id).OrderBy(i => i.Position).ThenBy(i => i.Id).ToListAsync();
        var comments = await db.TaskComments.AsNoTracking().Where(i => i.TaskId == id).OrderBy(i => i.CreatedAt).ToListAsync();
        var activity = await db.TaskActivities.AsNoTracking().Where(i => i.TaskId == id).OrderByDescending(i => i.CreatedAt).Take(500).ToListAsync();
        var names = await NamesAsync([t]);
        return new TaskDetailResponse
        {
            Success = true,
            Task = ToDto(t, names, c, items.Count(i => i.Done), items.Count, comments.Count),
            Checklist = items.Select(i => new TaskChecklistItemDto { Id = i.Id, Text = i.Text, Done = i.Done, DoneAt = i.DoneAt, DoneByName = i.DoneByName, AddedAt = i.AddedAt.Year > 2000 ? i.AddedAt : null, AddedByName = i.AddedByName }).ToList(),
            Comments = comments.Select(m => new TaskCommentDto { Id = m.Id, Author = m.Author, Body = m.Body, CreatedAt = m.CreatedAt }).ToList(),
            Activity = activity.Select(a => new TaskActivityDto { Actor = a.Actor, Text = a.Text, CreatedAt = a.CreatedAt }).ToList(),
        };
    }

    // ---------------------------------------------------------------------
    // Changing
    // ---------------------------------------------------------------------

    private static DateTime? AsUtc(DateTime? d) => d is null ? null : d.Value.Kind switch { DateTimeKind.Utc => d, DateTimeKind.Local => d.Value.ToUniversalTime(), _ => DateTime.SpecifyKind(d.Value, DateTimeKind.Utc) };

    private void Log(long taskId, string actor, string text) => db.TaskActivities.Add(new TaskActivity { TaskId = taskId, Actor = actor, Text = text });

    private void Notify(long? employeeId, string title, string? body, long taskId, string kind = "task")
    {
        if (employeeId is null) return;
        db.Notifications.Add(new Notification { EmployeeId = employeeId, Kind = kind, Title = title, Body = body, Link = $"/admin/tasks?open={taskId}" });
    }

    private async Task<string?> ValidateAsync(TaskSaveRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Title)) return "Give the task a title.";
        if (r.Title.Trim().Length > 300) return "The title is too long (300 characters at most).";
        if (!TaskPriorities.All.Contains(r.Priority)) return "Unknown priority.";
        if (!TaskRepeats.All.Contains(r.Repeat)) return "Unknown repeat setting.";
        if (r.Repeat != TaskRepeats.None && r.DueAt is null) return "A repeating task needs a due date.";
        if (r.StartAt is { } st && r.DueAt is { } en && AsUtc(en) < AsUtc(st)) return "The end date cannot be before the start date.";
        if (r.AssignedToEmployeeId is { } a && !await db.Employees.AnyAsync(e => e.Id == a && e.IsActive)) return "That person is not active.";
        if (r.DepartmentId is { } d && !await db.Departments.AnyAsync(x => x.Id == d && x.IsActive)) return "That department does not exist.";
        if (r.LeadId is { } l && !await db.Leads.AnyAsync(x => x.Id == l)) return "That lead does not exist.";
        return null;
    }

    public async Task<(WorkTask? Task, string? Error)> CreateAsync(TaskSaveRequest r, Caller c)
    {
        var error = await ValidateAsync(r);
        if (error is not null) return (null, error);
        // Auto-assign: only people who are signed in right now, least busy first. Nobody online = the task waits unassigned.
        string? autoName = null;
        if (r.AutoAssign && r.AssignedToEmployeeId is null && await availability.PickAsync("tasks", r.DepartmentId) is { } pick) { r.AssignedToEmployeeId = pick.Id; autoName = pick.Name; pick.LastAssignedAt = DateTime.UtcNow; }
        var t = new WorkTask
        {
            Title = r.Title.Trim(), Description = Clean(r.Description, 4000), Priority = r.Priority, StartAt = AsUtc(r.StartAt), DueAt = AsUtc(r.DueAt),
            AssignedToEmployeeId = r.AssignedToEmployeeId, DepartmentId = r.DepartmentId, LeadId = r.LeadId,
            OrderId = string.IsNullOrWhiteSpace(r.OrderId) ? null : r.OrderId.Trim(), Tags = Clean(r.Tags, 300), Repeat = r.Repeat,
            CreatedByEmployeeId = c.EmployeeId, CreatedByName = c.Name,
        };
        if (t.StartAt is { } sa0 && sa0 <= DateTime.UtcNow) t.StartedNotifiedAt = t.StartSoonNotifiedAt = DateTime.UtcNow; // already started: no "starts now" notice
        db.WorkTasks.Add(t);
        await db.SaveChangesAsync();
        var pos = 0;
        foreach (var line in r.Checklist.Select(x => x.Trim()).Where(x => x.Length > 0).Take(40))
            db.TaskChecklistItems.Add(new TaskChecklistItem { TaskId = t.Id, Text = line.Length > 300 ? line[..300] : line, Position = pos++, AddedByName = c.Name });
        Log(t.Id, c.Name, autoName is null ? "Created the task" : $"Created the task and auto-assigned it to {autoName} (signed in, least open work)");
        if (t.AssignedToEmployeeId is { } who && who != c.EmployeeId) Notify(who, $"New task: {t.Title}", $"From {c.Name}{(t.StartAt is { } s && s > DateTime.UtcNow ? $" · starts {FollowUpService.FormatDue(s)}" : "")}{(t.DueAt is { } d ? $" · due {FollowUpService.FormatDue(d)}" : "")}", t.Id);
        await db.SaveChangesAsync();
        return (t, null);
    }

    private static string? Clean(string? s, int max) { var v = s?.Trim(); return string.IsNullOrEmpty(v) ? null : v.Length > max ? v[..max] : v; }

    public async Task<(bool Ok, string Message)> UpdateAsync(WorkTask t, TaskSaveRequest r, Caller c)
    {
        var error = await ValidateAsync(r);
        if (error is not null) return (false, error);
        var names = await db.Employees.AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.Name);
        var changes = new List<string>();
        if (t.Title != r.Title.Trim()) { changes.Add("changed the title"); t.Title = r.Title.Trim(); }
        t.Description = Clean(r.Description, 4000);
        if (t.Priority != r.Priority) { changes.Add($"set the priority to {r.Priority}"); t.Priority = r.Priority; }
        var start = AsUtc(r.StartAt);
        if (t.StartAt != start)
        {
            changes.Add(start is null ? "removed the start date" : $"set the start date to {FollowUpService.FormatDue(start.Value)}");
            t.StartAt = start; t.StartSoonNotifiedAt = null; t.StartedNotifiedAt = null;
        }
        var due = AsUtc(r.DueAt);
        if (t.DueAt != due)
        {
            changes.Add(due is null ? "removed the due date" : $"moved the due date to {FollowUpService.FormatDue(due.Value)}");
            t.DueAt = due; t.ReminderSentAt = null; t.OverdueNotifiedAt = null;
        }
        if (t.AssignedToEmployeeId != r.AssignedToEmployeeId)
        {
            changes.Add(r.AssignedToEmployeeId is { } a ? $"assigned it to {names.GetValueOrDefault(a)}" : "left it unassigned");
            t.AssignedToEmployeeId = r.AssignedToEmployeeId;
            if (r.AssignedToEmployeeId is { } who && who != c.EmployeeId) Notify(who, $"Task assigned to you: {t.Title}", $"By {c.Name}", t.Id);
        }
        t.DepartmentId = r.DepartmentId; t.LeadId = r.LeadId; t.OrderId = string.IsNullOrWhiteSpace(r.OrderId) ? null : r.OrderId.Trim();
        t.Tags = Clean(r.Tags, 300);
        if (t.Repeat != r.Repeat) { changes.Add($"set it to repeat {r.Repeat}"); t.Repeat = r.Repeat; }
        t.UpdatedAt = DateTime.UtcNow;
        foreach (var ch in changes) Log(t.Id, c.Name, char.ToUpperInvariant(ch[0]) + ch[1..]);
        await db.SaveChangesAsync();
        return (true, "Task saved");
    }

    public async Task<(bool Ok, string Message)> SetStatusAsync(WorkTask t, string status, string? note, Caller c)
    {
        if (!TaskStatuses.All.Contains(status)) return (false, "Unknown status.");
        if (t.Status == status) return (true, "No change");
        // Finishing with steps still open needs a written reason, so the creator can see what was skipped and why.
        if (status == TaskStatuses.Done && string.IsNullOrWhiteSpace(note) && await db.TaskChecklistItems.AnyAsync(i => i.TaskId == t.Id && !i.Done))
            return (false, "Some checklist steps are not ticked. Give a reason for each one before marking the task done.");
        var from = t.Status;
        t.Status = status; t.UpdatedAt = DateTime.UtcNow;
        Log(t.Id, c.Name, $"Moved from {TaskStatuses.Label(from)} to {TaskStatuses.Label(status)}{(string.IsNullOrWhiteSpace(note) ? "" : " - " + note.Trim())}");
        if (status == TaskStatuses.Done)
        {
            t.CompletedAt = DateTime.UtcNow;
            if (Clean(note, 4000) is { } why) db.TaskComments.Add(new TaskComment { TaskId = t.Id, AuthorEmployeeId = c.EmployeeId, Author = c.Name, Body = "Marked done. " + why });
            if (t.CreatedByEmployeeId is { } creator && creator != c.EmployeeId) Notify(creator, $"Task done: {t.Title}", $"Completed by {c.Name}", t.Id);
            await db.SaveChangesAsync();
            await CreateNextRepeatAsync(t, c);
        }
        else
        {
            var reopened = from == TaskStatuses.Done;
            t.CompletedAt = null;
            if (reopened) await RemoveUntouchedNextRepeatAsync(t); // finishing it again must not stack up another copy
            await db.SaveChangesAsync();
            if (reopened) return (true, "Task reopened. It is back in your open tasks.");
        }
        return (true, status == TaskStatuses.Done ? "Task completed" : "Status updated");
    }

    /// <summary>The still-open later occurrence of a repeating task, if one was already made (by finishing it before).</summary>
    private IQueryable<WorkTask> NextOccurrences(WorkTask done) =>
        db.WorkTasks.Where(x => x.Id != done.Id && x.Status != TaskStatuses.Done && x.Title == done.Title && x.Repeat == done.Repeat
            && x.AssignedToEmployeeId == done.AssignedToEmployeeId && x.CreatedByEmployeeId == done.CreatedByEmployeeId && x.DueAt > done.DueAt);

    /// <summary>When a finished repeating task is reopened, drop the next occurrence that was created for it if nobody has touched it yet.</summary>
    private async Task RemoveUntouchedNextRepeatAsync(WorkTask reopened)
    {
        if (reopened.Repeat == TaskRepeats.None || reopened.DueAt is null) return;
        foreach (var n in await NextOccurrences(reopened).Where(x => x.Status == TaskStatuses.Todo).ToListAsync())
        {
            var touched = await db.TaskComments.AnyAsync(m => m.TaskId == n.Id) || await db.TaskChecklistItems.AnyAsync(i => i.TaskId == n.Id && i.Done)
                || await db.TaskActivities.CountAsync(a => a.TaskId == n.Id) > 1;
            if (touched) continue;
            await DeleteAsync(n);
            Log(reopened.Id, "system", "Removed the next repeat that was created when this task was finished, because it was reopened");
        }
    }

    private async Task CreateNextRepeatAsync(WorkTask done, Caller c)
    {
        if (done.Repeat == TaskRepeats.None || done.DueAt is not { } due) return;
        if (await NextOccurrences(done).AnyAsync()) return; // already scheduled: marking it done a second time must not add another
        var next = done.Repeat switch { TaskRepeats.Daily => due.AddDays(1), TaskRepeats.Weekly => due.AddDays(7), _ => due.AddMonths(1) };
        while (next < DateTime.UtcNow) next = done.Repeat switch { TaskRepeats.Daily => next.AddDays(1), TaskRepeats.Weekly => next.AddDays(7), _ => next.AddMonths(1) }; // skip occurrences already missed
        var copy = new WorkTask
        {
            Title = done.Title, Description = done.Description, Priority = done.Priority, DueAt = next, StartAt = done.StartAt is { } s ? s + (next - due) : null, AssignedToEmployeeId = done.AssignedToEmployeeId,
            CreatedByEmployeeId = done.CreatedByEmployeeId, CreatedByName = done.CreatedByName, DepartmentId = done.DepartmentId, LeadId = done.LeadId,
            OrderId = done.OrderId, Tags = done.Tags, Repeat = done.Repeat,
        };
        db.WorkTasks.Add(copy);
        await db.SaveChangesAsync();
        var items = await db.TaskChecklistItems.AsNoTracking().Where(i => i.TaskId == done.Id).OrderBy(i => i.Position).ToListAsync();
        foreach (var i in items) db.TaskChecklistItems.Add(new TaskChecklistItem { TaskId = copy.Id, Text = i.Text, Position = i.Position });
        Log(copy.Id, "system", $"Created automatically because the {done.Repeat} task was completed");
        Log(done.Id, "system", $"Next {done.Repeat} task scheduled for {FollowUpService.FormatDue(next)}");
        Notify(copy.AssignedToEmployeeId, $"Repeating task scheduled: {copy.Title}", $"Next due {FollowUpService.FormatDue(next)}", copy.Id);
        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(WorkTask t)
    {
        db.TaskChecklistItems.RemoveRange(db.TaskChecklistItems.Where(i => i.TaskId == t.Id));
        db.TaskComments.RemoveRange(db.TaskComments.Where(i => i.TaskId == t.Id));
        db.TaskActivities.RemoveRange(db.TaskActivities.Where(i => i.TaskId == t.Id));
        db.WorkTasks.Remove(t);
        await db.SaveChangesAsync();
    }

    public async Task<(bool Ok, string Message)> CommentAsync(WorkTask t, string body, Caller c)
    {
        var text = body?.Trim() ?? "";
        if (text.Length == 0) return (false, "Write a comment first.");
        db.TaskComments.Add(new TaskComment { TaskId = t.Id, AuthorEmployeeId = c.EmployeeId, Author = c.Name, Body = text.Length > 4000 ? text[..4000] : text });
        t.UpdatedAt = DateTime.UtcNow;
        var preview = text.Length > 120 ? text[..120] + "…" : text;
        foreach (var who in new[] { t.AssignedToEmployeeId, t.CreatedByEmployeeId }.Where(x => x != null && x != c.EmployeeId).Distinct())
            Notify(who, $"New comment on: {t.Title}", $"{c.Name}: {preview}", t.Id);
        await db.SaveChangesAsync();
        return (true, "Comment added");
    }

    public async Task<(bool Ok, string Message)> AddChecklistAsync(WorkTask t, string? text, Caller c)
    {
        var v = text?.Trim();
        if (string.IsNullOrEmpty(v)) return (false, "Write the checklist item first.");
        if (await db.TaskChecklistItems.CountAsync(i => i.TaskId == t.Id) >= 40) return (false, "A task can have 40 checklist items at most.");
        var pos = (await db.TaskChecklistItems.Where(i => i.TaskId == t.Id).MaxAsync(i => (int?)i.Position) ?? -1) + 1;
        var text0 = v.Length > 300 ? v[..300] : v;
        db.TaskChecklistItems.Add(new TaskChecklistItem { TaskId = t.Id, Text = text0, Position = pos, AddedByName = c.Name });
        Log(t.Id, c.Name, $"Added checklist step: {text0} (task was created {FollowUpService.FormatDue(t.CreatedAt)})");
        t.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return (true, "Added");
    }

    public async Task<bool> SetChecklistDoneAsync(WorkTask t, long itemId, bool done, Caller c)
    {
        var i = await db.TaskChecklistItems.FirstOrDefaultAsync(x => x.Id == itemId && x.TaskId == t.Id);
        if (i is null) return false;
        i.Done = done; t.UpdatedAt = DateTime.UtcNow;
        i.DoneAt = done ? DateTime.UtcNow : null; i.DoneByName = done ? c.Name : null;
        Log(t.Id, c.Name, $"{(done ? "Ticked" : "Unticked")}: {i.Text}");
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> RemoveChecklistAsync(WorkTask t, long itemId, Caller c)
    {
        var i = await db.TaskChecklistItems.FirstOrDefaultAsync(x => x.Id == itemId && x.TaskId == t.Id);
        if (i is null) return false;
        db.TaskChecklistItems.Remove(i);
        Log(t.Id, c.Name, $"Removed checklist step: {i.Text}{(i.Done ? " (it had been ticked)" : "")}");
        t.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return true;
    }

    // ---------------------------------------------------------------------
    // Reminders (called by the background worker)
    // ---------------------------------------------------------------------

    public async Task RunCycleAsync()
    {
        var now = DateTime.UtcNow;
        // Upcoming work: tell the assignee a day before the start date, and again when it starts.
        var startSoon = await db.WorkTasks.Where(t => t.Status != TaskStatuses.Done && t.StartAt != null && t.StartAt > now && t.StartAt <= now.AddHours(24) && t.StartSoonNotifiedAt == null).Take(200).ToListAsync();
        foreach (var t in startSoon)
        {
            t.StartSoonNotifiedAt = now;
            Notify(t.AssignedToEmployeeId ?? t.CreatedByEmployeeId, $"Upcoming task: {t.Title}", $"Starts {FollowUpService.FormatDue(t.StartAt!.Value)}", t.Id, "reminder");
        }
        var started = await db.WorkTasks.Where(t => t.Status != TaskStatuses.Done && t.StartAt != null && t.StartAt <= now && t.StartedNotifiedAt == null).Take(200).ToListAsync();
        foreach (var t in started)
        {
            t.StartedNotifiedAt = now;
            t.StartSoonNotifiedAt ??= now;
            Notify(t.AssignedToEmployeeId ?? t.CreatedByEmployeeId, $"Task starts now: {t.Title}", t.DueAt is { } e ? $"Finish by {FollowUpService.FormatDue(e)}" : "Time to begin", t.Id, "reminder");
        }
        if (startSoon.Count + started.Count > 0) await db.SaveChangesAsync();
        // Due within a day.
        var soon = await db.WorkTasks.Where(t => t.Status != TaskStatuses.Done && t.DueAt != null && t.DueAt > now && t.DueAt <= now.AddHours(24) && t.ReminderSentAt == null).Take(200).ToListAsync();
        foreach (var t in soon)
        {
            t.ReminderSentAt = now;
            Notify(t.AssignedToEmployeeId ?? t.CreatedByEmployeeId, $"Task due soon: {t.Title}", $"Due {FollowUpService.FormatDue(t.DueAt!.Value)}", t.Id, "reminder");
        }
        // Overdue: tell the owner, and the person who created it.
        var late = await db.WorkTasks.Where(t => t.Status != TaskStatuses.Done && t.DueAt != null && t.DueAt < now && t.OverdueNotifiedAt == null).Take(200).ToListAsync();
        foreach (var t in late)
        {
            t.OverdueNotifiedAt = now;
            foreach (var who in new[] { t.AssignedToEmployeeId, t.CreatedByEmployeeId }.Where(x => x != null).Distinct())
                Notify(who, $"Task overdue: {t.Title}", $"Was due {FollowUpService.FormatDue(t.DueAt!.Value)}", t.Id, "overdue");
        }
        if (soon.Count + late.Count > 0) { await db.SaveChangesAsync(); log.LogInformation("[Tasks] {Soon} due-soon and {Late} overdue notices sent", soon.Count, late.Count); }
    }
}
