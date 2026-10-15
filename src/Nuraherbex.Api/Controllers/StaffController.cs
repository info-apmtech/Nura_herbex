using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Nuraherbex.Api.Data;
using Nuraherbex.Api.Services;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Controllers;

/// <summary>
/// CRM endpoints for employees. Both roles may sign in: "admin" sees every lead and manages the team,
/// "sales" only sees (and replies to) leads assigned to them. Admin-only actions add their own role attribute
/// (attributes combine, so those require "admin").
/// </summary>
[ApiController]
[Route("api/admin")]
[Authorize(Roles = "admin,sales")]
public class StaffController(NuraDbContext db, LeadService leads, WhatsAppService whatsapp, ILogger<StaffController> log) : ControllerBase, IAsyncActionFilter
{
    private static readonly string[] Roles = [EmployeeRoles.Admin, EmployeeRoles.Sales];

    private bool IsAdmin => User.IsInRole(EmployeeRoles.Admin);
    private long? MyId => long.TryParse(User.FindFirstValue("emp_id"), out var id) ? id : null;
    private string MyName => User.FindFirstValue(ClaimTypes.Name) ?? User.FindFirstValue(ClaimTypes.Email) ?? "admin";

    /// <summary>A token stays valid for hours; make sure a deactivated or deleted employee is locked out immediately.</summary>
    [NonAction]
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (MyId is { } id && !await db.Employees.AsNoTracking().AnyAsync(e => e.Id == id && e.IsActive))
        {
            context.Result = Unauthorized(new ApiResult { Success = false, Message = "This account has been deactivated." });
            return;
        }
        await next();
    }

    // ---- Session ---------------------------------------------------------------------

    [HttpGet("verify")]
    public IActionResult Verify() => Ok(new AdminLoginResponse
    {
        Success = true, Valid = true,
        User = new AdminUserDto { Email = User.FindFirstValue(ClaimTypes.Email) ?? "", Role = IsAdmin ? "admin" : "sales", Name = MyId is null ? "Administrator" : MyName, EmployeeId = MyId },
    });

    /// <summary>Employees a lead can be assigned or transferred to (id + name only, safe for sales users).</summary>
    [HttpGet("team")]
    public async Task<IActionResult> Team()
    {
        var team = await db.Employees.AsNoTracking().Where(e => e.IsActive && e.Role == EmployeeRoles.Sales).OrderBy(e => e.Name)
            .Select(e => new EmployeeDto { Id = e.Id, Name = e.Name, Role = e.Role, IsActive = true }).ToListAsync();
        return Ok(new EmployeeListResponse { Success = true, Employees = team, AutoAssign = await leads.IsAutoAssignEnabledAsync() });
    }

    // ---- Leads -----------------------------------------------------------------------

    /// <param name="assignee">admin only: "unassigned", "me" or an employee id. Sales users always get just their own leads.</param>
    [HttpGet("leads")]
    public async Task<LeadListResponse> Leads([FromQuery] string? status, [FromQuery] string? source, [FromQuery] string? q, [FromQuery] string? assignee, [FromQuery] int take = 100)
    {
        var baseQuery = db.Leads.AsNoTracking().AsQueryable();
        if (!IsAdmin) baseQuery = baseQuery.Where(l => l.AssignedToEmployeeId == MyId);
        else if (assignee == "unassigned") baseQuery = baseQuery.Where(l => l.AssignedToEmployeeId == null);
        else if (assignee == "me") baseQuery = baseQuery.Where(l => l.AssignedToEmployeeId == MyId);
        else if (long.TryParse(assignee, out var aid)) baseQuery = baseQuery.Where(l => l.AssignedToEmployeeId == aid);

        if (!string.IsNullOrWhiteSpace(source)) baseQuery = baseQuery.Where(l => l.Source == source);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            var digits = new string(term.Where(char.IsDigit).ToArray());
            baseQuery = baseQuery.Where(l => l.Name.Contains(term) || (digits.Length > 0 && l.Phone.Contains(digits)) || (l.Email != null && l.Email.Contains(term)));
        }

        var counts = await baseQuery.GroupBy(l => l.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync();
        var filtered = string.IsNullOrWhiteSpace(status) ? baseQuery : baseQuery.Where(l => l.Status == status);
        var rows = await filtered.OrderByDescending(l => l.LastContactAt).Take(Math.Clamp(take, 1, 500))
            .GroupJoin(db.Conversations.AsNoTracking(), l => l.Id, c => c.LeadId, (l, cs) => new { l, cs })
            .SelectMany(x => x.cs.DefaultIfEmpty(), (x, c) => new { x.l, c })
            .ToListAsync();

        var ids = rows.Select(r => r.l.Id).ToList();
        var last = await db.WhatsAppMessages.AsNoTracking()
            .Where(m => m.LeadId != null && ids.Contains(m.LeadId.Value)
                && m.Id == db.WhatsAppMessages.Where(x => x.LeadId == m.LeadId).Max(x => x.Id))
            .ToListAsync();
        var lastByLead = last.GroupBy(m => m.LeadId!.Value).ToDictionary(g => g.Key, g => g.First());
        var names = await db.Employees.AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.Name);

        return new LeadListResponse
        {
            Success = true,
            Leads = rows.Select(r =>
            {
                var dto = r.l.ToDto(r.c);
                dto.AssignedToName = r.l.AssignedToEmployeeId is { } eid ? names.GetValueOrDefault(eid) : null;
                if (lastByLead.TryGetValue(r.l.Id, out var m))
                {
                    dto.LastMessage = string.IsNullOrEmpty(m.Body) ? $"[{m.Type}]" : m.Body.Length > 90 ? m.Body[..(char.IsHighSurrogate(m.Body[89]) ? 89 : 90)] + "…" : m.Body;
                    dto.LastMessageDirection = m.Direction;
                }
                return dto;
            }).ToList(),
            Total = await filtered.CountAsync(),
            StatusCounts = counts.ToDictionary(c => c.Key, c => c.N),
            UnassignedCount = IsAdmin ? await db.Leads.CountAsync(l => l.AssignedToEmployeeId == null) : 0,
        };
    }

    /// <summary>Loads a lead the caller may see: any lead for admins, only their own for sales (404 otherwise, so ids do not leak).</summary>
    private async Task<Lead?> FindLeadAsync(long id)
    {
        var lead = await db.Leads.FirstOrDefaultAsync(l => l.Id == id);
        return lead is null || (!IsAdmin && lead.AssignedToEmployeeId != MyId) ? null : lead;
    }

    private static NotFoundObjectResult NoLead() => new(new ApiResult { Success = false, Message = "Lead not found" });

    [HttpGet("leads/{id:long}")]
    public async Task<IActionResult> LeadDetail(long id)
    {
        var lead = await FindLeadAsync(id);
        if (lead is null) return NoLead();
        var convo = await db.Conversations.FirstOrDefaultAsync(c => c.LeadId == id);
        var dto = lead.ToDto(convo);
        if (lead.AssignedToEmployeeId is { } eid) dto.AssignedToName = await db.Employees.AsNoTracking().Where(e => e.Id == eid).Select(e => e.Name).FirstOrDefaultAsync();
        if (convo is { UnreadCount: > 0 }) { convo.UnreadCount = 0; await db.SaveChangesAsync(); }

        var messages = await db.WhatsAppMessages.AsNoTracking().Where(m => m.LeadId == id).OrderByDescending(m => m.CreatedAt).Take(200).ToListAsync();
        var history = await db.LeadStatusHistory.AsNoTracking().Where(h => h.LeadId == id).OrderByDescending(h => h.CreatedAt).ToListAsync();
        var assignments = await db.LeadAssignmentHistory.AsNoTracking().Where(h => h.LeadId == id).OrderByDescending(h => h.CreatedAt).ToListAsync();
        var names = await db.Employees.AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.Name);
        return Ok(new LeadDetailResponse
        {
            Success = true, Lead = dto,
            Messages = messages.OrderBy(m => m.CreatedAt).Select(m => m.ToDto()).ToList(),
            History = history.Select(h => new LeadStatusHistoryDto { FromStatus = h.FromStatus, ToStatus = h.ToStatus, Note = h.Note, ChangedBy = h.ChangedBy, CreatedAt = h.CreatedAt }).ToList(),
            Assignments = assignments.Select(a => new LeadAssignmentDto
            {
                FromName = a.FromEmployeeId is { } f ? names.GetValueOrDefault(f) ?? "Former employee" : null,
                ToName = a.ToEmployeeId is { } t ? names.GetValueOrDefault(t) ?? "Former employee" : null,
                Reason = a.Reason, ChangedBy = a.ChangedBy, CreatedAt = a.CreatedAt,
            }).ToList(),
        });
    }

    [HttpPut("leads/{id:long}/status")]
    public async Task<IActionResult> LeadStatus(long id, LeadStatusRequest r)
    {
        if (!LeadStatuses.All.Contains(r.Status)) return BadRequest(new ApiResult { Success = false, Message = "Unknown status" });
        if (await FindLeadAsync(id) is null) return NoLead();
        await leads.ChangeStatusAsync(id, r.Status, r.Note, MyName);
        return Ok(new ApiResult { Success = true, Message = "Status updated" });
    }

    [HttpPut("leads/{id:long}/notes")]
    public async Task<IActionResult> LeadNotes(long id, LeadNotesRequest r)
    {
        var lead = await FindLeadAsync(id);
        if (lead is null) return NoLead();
        var notes = r.Notes?.Trim();
        lead.Notes = string.IsNullOrEmpty(notes) ? null : notes[..Math.Min(notes.Length, 4000)];
        lead.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new ApiResult { Success = true, Message = "Notes saved" });
    }

    /// <summary>Assign / transfer. Admins can assign anyone or unassign; a sales owner can hand their lead to another sales employee.</summary>
    [HttpPut("leads/{id:long}/assign")]
    public async Task<IActionResult> LeadAssign(long id, LeadAssignRequest r)
    {
        var lead = await FindLeadAsync(id);
        if (lead is null) return NoLead();
        if (r.EmployeeId is null && !IsAdmin) return StatusCode(403, new ApiResult { Success = false, Message = "Only an administrator can unassign a lead." });
        if (r.EmployeeId is { } target)
        {
            var emp = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == target && e.IsActive);
            if (emp is null) return BadRequest(new ApiResult { Success = false, Message = "That employee is not active." });
            if (!IsAdmin && emp.Role != EmployeeRoles.Sales) return BadRequest(new ApiResult { Success = false, Message = "Leads can only be transferred to a sales employee." });
        }
        await leads.AssignAsync(lead, r.EmployeeId, string.IsNullOrWhiteSpace(r.Reason) ? (IsAdmin ? "Assigned by admin" : "Transferred by owner") : r.Reason, MyName);
        await db.SaveChangesAsync();
        return Ok(new ApiResult { Success = true, Message = r.EmployeeId is null ? "Lead unassigned" : "Lead assigned" });
    }

    /// <summary>Reply to a lead on WhatsApp. Sales users can only reply to their own leads; consent and the 24h window are enforced here too.</summary>
    [HttpPost("leads/{id:long}/reply")]
    public async Task<IActionResult> LeadReply(long id, LeadReplyRequest r)
    {
        var lead = await FindLeadAsync(id);
        if (lead is null) return NoLead();
        if (string.IsNullOrWhiteSpace(r.Text)) return BadRequest(new ApiResult { Success = false, Message = "Type a message first." });
        if (lead.ConsentStatus == "opted_out") return BadRequest(new ApiResult { Success = false, Message = "This customer opted out of messages." });
        var window = await db.Conversations.AsNoTracking().Where(c => c.LeadId == id).Select(c => c.ServiceWindowExpiresAt).FirstOrDefaultAsync();
        if (window is null || window < DateTime.UtcNow)
            return BadRequest(new ApiResult { Success = false, Message = "The 24-hour reply window is closed. Only approved templates can be sent until the customer writes again." });

        var ok = await whatsapp.SendTextAsync(lead.Phone, r.Text.Trim());
        if (ok) log.LogInformation("[Lead] {Who} replied to lead {Id}", MyName, id);
        return Ok(new ApiResult { Success = ok, Message = ok ? "Message sent" : "Message could not be sent. Check the WhatsApp credentials in the server configuration." });
    }

    // ---- Employees (admin only) --------------------------------------------------------

    [HttpGet("employees"), Authorize(Roles = "admin")]
    public async Task<EmployeeListResponse> Employees()
    {
        var emps = await db.Employees.AsNoTracking().OrderByDescending(e => e.IsActive).ThenBy(e => e.Name).ToListAsync();
        var stats = await db.Leads.AsNoTracking().Where(l => l.AssignedToEmployeeId != null)
            .GroupBy(l => l.AssignedToEmployeeId!.Value)
            .Select(g => new { Id = g.Key, Total = g.Count(), Open = g.Count(l => l.Status != LeadStatuses.Converted && l.Status != LeadStatuses.NotInterested) })
            .ToDictionaryAsync(x => x.Id);
        return new EmployeeListResponse
        {
            Success = true, AutoAssign = await leads.IsAutoAssignEnabledAsync(),
            Employees = emps.Select(e =>
            {
                var dto = e.ToDto();
                if (stats.TryGetValue(e.Id, out var s)) { dto.TotalLeads = s.Total; dto.OpenLeads = s.Open; }
                return dto;
            }).ToList(),
        };
    }

    [HttpPost("employees"), Authorize(Roles = "admin")]
    public async Task<IActionResult> CreateEmployee(EmployeeSaveRequest r)
    {
        var error = Validate(r, creating: true);
        if (error is not null) return BadRequest(new ApiResult { Success = false, Message = error });
        var email = r.Email.Trim().ToLowerInvariant();
        if (await db.Employees.AnyAsync(e => e.Email == email)) return BadRequest(new ApiResult { Success = false, Message = "An employee with this email already exists." });

        var emp = new Employee
        {
            Name = r.Name.Trim(), Email = email, Phone = (r.Phone ?? "").Trim(), Role = r.Role, IsActive = r.IsActive,
            PasswordHash = AdminPassword.Hash(r.Password!),
        };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();
        log.LogInformation("[Staff] {Admin} created {Role} employee {Email}", MyName, emp.Role, emp.Email);
        return StatusCode(201, new EmployeeResponse { Success = true, Message = "Employee created", Employee = emp.ToDto() });
    }

    [HttpPut("employees/{id:long}"), Authorize(Roles = "admin")]
    public async Task<IActionResult> UpdateEmployee(long id, EmployeeSaveRequest r)
    {
        var emp = await db.Employees.FirstOrDefaultAsync(e => e.Id == id);
        if (emp is null) return NotFound(new ApiResult { Success = false, Message = "Employee not found" });
        var error = Validate(r, creating: false);
        if (error is not null) return BadRequest(new ApiResult { Success = false, Message = error });
        var email = r.Email.Trim().ToLowerInvariant();
        if (await db.Employees.AnyAsync(e => e.Email == email && e.Id != id)) return BadRequest(new ApiResult { Success = false, Message = "Another employee already uses this email." });
        if (id == MyId && (!r.IsActive || r.Role != EmployeeRoles.Admin))
            return BadRequest(new ApiResult { Success = false, Message = "You cannot deactivate or demote your own account." });

        var wasActive = emp.IsActive;
        emp.Name = r.Name.Trim(); emp.Email = email; emp.Phone = (r.Phone ?? "").Trim(); emp.Role = r.Role; emp.IsActive = r.IsActive;
        if (!string.IsNullOrEmpty(r.Password)) emp.PasswordHash = AdminPassword.Hash(r.Password);
        await db.SaveChangesAsync();

        // A deactivated or demoted employee must not keep leads silently.
        var moved = 0;
        if ((wasActive && !emp.IsActive) || emp.Role != EmployeeRoles.Sales)
            moved = await leads.ReassignOpenLeadsAsync(emp.Id, spreadAcrossTeam: true, MyName);
        return Ok(new EmployeeResponse { Success = true, Message = moved > 0 ? $"Employee updated; {moved} open lead(s) were reassigned." : "Employee updated", Employee = emp.ToDto() });
    }

    /// <summary>Removing an employee deactivates the login (history stays intact) and moves their open leads.</summary>
    [HttpDelete("employees/{id:long}"), Authorize(Roles = "admin")]
    public async Task<IActionResult> RemoveEmployee(long id, [FromQuery] string? reassign)
    {
        var emp = await db.Employees.FirstOrDefaultAsync(e => e.Id == id);
        if (emp is null) return NotFound(new ApiResult { Success = false, Message = "Employee not found" });
        if (id == MyId) return BadRequest(new ApiResult { Success = false, Message = "You cannot remove your own account." });

        emp.IsActive = false;
        await db.SaveChangesAsync();
        var moved = await leads.ReassignOpenLeadsAsync(emp.Id, spreadAcrossTeam: !string.Equals(reassign, "unassigned", StringComparison.OrdinalIgnoreCase), MyName);
        log.LogInformation("[Staff] {Admin} removed employee {Email}; {Moved} lead(s) reassigned", MyName, emp.Email, moved);
        return Ok(new ApiResult { Success = true, Message = moved > 0 ? $"{emp.Name} removed; {moved} open lead(s) were reassigned." : $"{emp.Name} removed." });
    }

    [HttpPut("leads/settings"), Authorize(Roles = "admin")]
    public async Task<IActionResult> LeadSettings(LeadSettingsRequest r)
    {
        await leads.SetAutoAssignAsync(r.AutoAssign);
        return Ok(new ApiResult { Success = true, Message = r.AutoAssign ? "New leads will be assigned round-robin." : "New leads will wait in the admin queue." });
    }

    private static string? Validate(EmployeeSaveRequest r, bool creating)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) return "Name is required.";
        if (string.IsNullOrWhiteSpace(r.Email) || !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(r.Email.Trim())) return "A valid email is required.";
        if (!Roles.Contains(r.Role)) return "Role must be admin or sales.";
        if (creating && (r.Password is null || r.Password.Length < 8)) return "Password must be at least 8 characters.";
        if (!creating && !string.IsNullOrEmpty(r.Password) && r.Password.Length < 8) return "Password must be at least 8 characters.";
        return null;
    }
}
