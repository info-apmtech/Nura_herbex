using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nuraherbex.Api.Data;
using Nuraherbex.Api.Services;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Controllers;

/// <summary>Team tasks. With "own records" access a person works on tasks assigned to or created by them; with full access, everyone's.</summary>
[ApiController]
[Route("api/admin/tasks")]
[Authorize(Roles = "admin,sales")]
public class TaskController(TaskService tasks, RightsService rights) : StaffControllerBase(rights)
{
    private TaskService.Caller Me => new(MyId, MyName, CanAll("tasks"));

    private static NotFoundObjectResult NoTask() => new(new ApiResult { Success = false, Message = "Task not found" });
    private static ObjectResult Denied() => new(new ApiResult { Success = false, Message = "You can only change tasks that are assigned to you or that you created." }) { StatusCode = 403 };

    private Task<WorkTask?> Find(long id) => tasks.Visible(Me).FirstOrDefaultAsync(t => t.Id == id);

    /// <param name="scope">open (default) | overdue | today | done | board | all</param>
    /// <param name="assignee">me | unassigned | an employee id</param>
    [HttpGet]
    public async Task<TaskListResponse> List([FromQuery] string? scope, [FromQuery] string? assignee, [FromQuery] long? department, [FromQuery] string? priority,
        [FromQuery] string? status, [FromQuery] string? q, [FromQuery] long? leadId, [FromQuery] bool createdByMe = false, [FromQuery] int take = 300) =>
        await tasks.ListAsync(Me, new TaskService.Filter(scope, assignee, department, priority, status, q, leadId, createdByMe, take));

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id)
    {
        var d = await tasks.DetailAsync(id, Me);
        return d is null ? NoTask() : Ok(d);
    }

    [HttpPost]
    public async Task<IActionResult> Create(TaskSaveRequest r)
    {
        var (t, error) = await tasks.CreateAsync(r, Me);
        return t is null ? BadRequest(new ApiResult { Success = false, Message = error }) : StatusCode(201, new TaskDetailResponse { Success = true, Message = r.AutoAssign && t.AssignedToEmployeeId is null ? "Task created. Nobody is signed in right now, so it is unassigned." : "Task created", Task = new TaskDto { Id = t.Id, Title = t.Title, AssignedToEmployeeId = t.AssignedToEmployeeId } });
    }

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, TaskSaveRequest r)
    {
        var t = await Find(id);
        if (t is null) return NoTask();
        if (!TaskService.CanEdit(t, Me)) return Denied();
        var (ok, msg) = await tasks.UpdateAsync(t, r, Me);
        return ok ? Ok(new ApiResult { Success = true, Message = msg }) : BadRequest(new ApiResult { Success = false, Message = msg });
    }

    [HttpPut("{id:long}/status")]
    public async Task<IActionResult> Status(long id, TaskStatusRequest r)
    {
        var t = await Find(id);
        if (t is null) return NoTask();
        if (!TaskService.CanEdit(t, Me)) return Denied();
        var (ok, msg) = await tasks.SetStatusAsync(t, r.Status, r.Note, Me);
        return ok ? Ok(new ApiResult { Success = true, Message = msg }) : BadRequest(new ApiResult { Success = false, Message = msg });
    }

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id)
    {
        var t = await Find(id);
        if (t is null) return NoTask();
        if (!TaskService.CanDelete(t, Me)) return StatusCode(403, new ApiResult { Success = false, Message = "Only the person who created a task, or a manager, can delete it." });
        await tasks.DeleteAsync(t, stopSeries: true);
        return Ok(new ApiResult { Success = true, Message = "Task deleted" });
    }

    [HttpPost("{id:long}/comments")]
    public async Task<IActionResult> Comment(long id, TaskCommentRequest r)
    {
        var t = await Find(id);
        if (t is null) return NoTask();
        var (ok, msg) = await tasks.CommentAsync(t, r.Body, Me);
        return ok ? Ok(new ApiResult { Success = true, Message = msg }) : BadRequest(new ApiResult { Success = false, Message = msg });
    }

    [HttpPost("{id:long}/checklist")]
    public async Task<IActionResult> AddItem(long id, TaskChecklistRequest r)
    {
        var t = await Find(id);
        if (t is null) return NoTask();
        if (!TaskService.CanEdit(t, Me)) return Denied();
        var (ok, msg) = await tasks.AddChecklistAsync(t, r.Text, Me);
        return ok ? Ok(new ApiResult { Success = true, Message = msg }) : BadRequest(new ApiResult { Success = false, Message = msg });
    }

    [HttpPut("{id:long}/checklist/{itemId:long}")]
    public async Task<IActionResult> TickItem(long id, long itemId, TaskChecklistRequest r)
    {
        var t = await Find(id);
        if (t is null) return NoTask();
        if (!TaskService.CanEdit(t, Me)) return Denied();
        return await tasks.SetChecklistDoneAsync(t, itemId, r.Done ?? false, Me) ? Ok(new ApiResult { Success = true }) : NotFound(new ApiResult { Success = false, Message = "Checklist item not found" });
    }

    [HttpDelete("{id:long}/checklist/{itemId:long}")]
    public async Task<IActionResult> RemoveItem(long id, long itemId)
    {
        var t = await Find(id);
        if (t is null) return NoTask();
        if (!TaskService.CanEdit(t, Me)) return Denied();
        return await tasks.RemoveChecklistAsync(t, itemId, Me) ? Ok(new ApiResult { Success = true }) : NotFound(new ApiResult { Success = false, Message = "Checklist item not found" });
    }
}
