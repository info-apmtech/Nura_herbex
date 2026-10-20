using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nuraherbex.Api.Data;
using Nuraherbex.Api.Services;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Controllers;

/// <summary>Departments, designations (with their page rights) and the catalogue of pages. All of it needs the "Team &amp; organisation" page.</summary>
[ApiController]
[Route("api/admin")]
[Authorize(Roles = "admin,sales")]
public class OrganizationController(NuraDbContext db, RightsService rightsService, ILogger<OrganizationController> log) : StaffControllerBase(rightsService)
{
    // ---- Pages and presets ---------------------------------------------------------------------

    [HttpGet("rights/catalog")]
    public RightsCatalogResponse Catalog() => new()
    {
        Success = true,
        Pages = PageCatalog.Pages.Select(p => new PageDefDto
        {
            Key = p.Key, Label = p.Label, Group = p.Group, Kind = p.Kind, Description = p.Description,
            LimitedLabel = PageCatalog.LevelLabel(p.Key, 1), FullLabel = PageCatalog.LevelLabel(p.Key, 2),
        }).ToList(),
        Presets = PageCatalog.Presets.Select(p => new RightsPresetDto { Name = p.Name, Hint = p.Hint, Rights = p.Rights }).ToList(),
    };

    // ---- Departments ---------------------------------------------------------------------------

    [HttpGet("departments")]
    public async Task<DepartmentListResponse> Departments()
    {
        var deps = await db.Departments.AsNoTracking().OrderByDescending(d => d.IsActive).ThenBy(d => d.Name).ToListAsync();
        var people = await db.Employees.AsNoTracking().Where(e => e.IsActive && e.DepartmentId != null).GroupBy(e => e.DepartmentId!.Value).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        var desig = await db.Designations.AsNoTracking().Where(d => d.DepartmentId != null).GroupBy(d => d.DepartmentId!.Value).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        var tasks = await db.WorkTasks.AsNoTracking().Where(t => t.DepartmentId != null && t.Status != TaskStatuses.Done).GroupBy(t => t.DepartmentId!.Value).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        var names = await db.Employees.AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.Name);
        return new DepartmentListResponse
        {
            Success = true,
            Departments = deps.Select(d => new DepartmentDto
            {
                Id = d.Id, Name = d.Name, Description = d.Description, HeadEmployeeId = d.HeadEmployeeId,
                HeadName = d.HeadEmployeeId is { } h ? names.GetValueOrDefault(h) : null, IsActive = d.IsActive,
                EmployeeCount = people.GetValueOrDefault(d.Id), DesignationCount = desig.GetValueOrDefault(d.Id), OpenTasks = tasks.GetValueOrDefault(d.Id),
            }).ToList(),
        };
    }

    private async Task<string?> ValidateDepartmentAsync(DepartmentSaveRequest r, long? id)
    {
        var name = r.Name?.Trim() ?? "";
        if (name.Length < 2) return "Give the department a name.";
        if (name.Length > 120) return "The name is too long (120 characters at most).";
        if (await db.Departments.AnyAsync(d => d.Name.ToLower() == name.ToLower() && d.Id != id)) return "A department with this name already exists.";
        if (r.HeadEmployeeId is { } h && !await db.Employees.AnyAsync(e => e.Id == h && e.IsActive)) return "The department head must be an active employee.";
        return null;
    }

    [HttpPost("departments")]
    public async Task<IActionResult> CreateDepartment(DepartmentSaveRequest r)
    {
        var error = await ValidateDepartmentAsync(r, null);
        if (error is not null) return BadRequest(new ApiResult { Success = false, Message = error });
        var d = new Department { Name = r.Name.Trim(), Description = Trim(r.Description, 500), HeadEmployeeId = r.HeadEmployeeId, IsActive = r.IsActive };
        db.Departments.Add(d);
        await db.SaveChangesAsync();
        RightsService.Invalidate();
        log.LogInformation("[Org] {Who} created department {Name}", MyName, d.Name);
        return StatusCode(201, new ApiResult { Success = true, Message = $"Department {d.Name} created" });
    }

    [HttpPut("departments/{id:long}")]
    public async Task<IActionResult> UpdateDepartment(long id, DepartmentSaveRequest r)
    {
        var d = await db.Departments.FirstOrDefaultAsync(x => x.Id == id);
        if (d is null) return NotFound(new ApiResult { Success = false, Message = "Department not found" });
        var error = await ValidateDepartmentAsync(r, id);
        if (error is not null) return BadRequest(new ApiResult { Success = false, Message = error });
        d.Name = r.Name.Trim(); d.Description = Trim(r.Description, 500); d.HeadEmployeeId = r.HeadEmployeeId; d.IsActive = r.IsActive;
        await db.SaveChangesAsync();
        RightsService.Invalidate();
        return Ok(new ApiResult { Success = true, Message = "Department saved" });
    }

    /// <summary>Deletes an unused department. One that people, designations or tasks still use is refused: deactivate it instead.</summary>
    [HttpDelete("departments/{id:long}")]
    public async Task<IActionResult> DeleteDepartment(long id)
    {
        var d = await db.Departments.FirstOrDefaultAsync(x => x.Id == id);
        if (d is null) return NotFound(new ApiResult { Success = false, Message = "Department not found" });
        var people = await db.Employees.CountAsync(e => e.DepartmentId == id);
        var desig = await db.Designations.CountAsync(x => x.DepartmentId == id);
        var tasks = await db.WorkTasks.CountAsync(t => t.DepartmentId == id);
        if (people + desig + tasks > 0)
            return BadRequest(new ApiResult { Success = false, Message = $"{d.Name} is still used by {people} people, {desig} designations and {tasks} tasks. Move them first, or switch the department to inactive instead." });
        db.Departments.Remove(d);
        await db.SaveChangesAsync();
        RightsService.Invalidate();
        return Ok(new ApiResult { Success = true, Message = "Department deleted" });
    }

    // ---- Designations ----------------------------------------------------------------------------

    private async Task<List<DesignationDto>> DesignationDtosAsync()
    {
        var rows = await db.Designations.AsNoTracking().OrderByDescending(d => d.IsActive).ThenByDescending(d => d.Level).ThenBy(d => d.Name).ToListAsync();
        var deps = await db.Departments.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Name);
        var counts = await db.Employees.AsNoTracking().Where(e => e.IsActive && e.DesignationId != null).GroupBy(e => e.DesignationId!.Value).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        return rows.Select(d => new DesignationDto
        {
            Id = d.Id, Name = d.Name, Description = d.Description, DepartmentId = d.DepartmentId,
            DepartmentName = d.DepartmentId is { } p ? deps.GetValueOrDefault(p) : null, Level = d.Level, IsActive = d.IsActive,
            EmployeeCount = counts.GetValueOrDefault(d.Id), Rights = PageCatalog.FromJson(d.RightsJson),
        }).ToList();
    }

    [HttpGet("designations")]
    public async Task<DesignationListResponse> Designations() => new() { Success = true, Designations = await DesignationDtosAsync() };

    private async Task<string?> ValidateDesignationAsync(DesignationSaveRequest r, long? id)
    {
        var name = r.Name?.Trim() ?? "";
        if (name.Length < 2) return "Give the designation a name.";
        if (name.Length > 120) return "The name is too long (120 characters at most).";
        if (await db.Designations.AnyAsync(d => d.Name.ToLower() == name.ToLower() && d.Id != id)) return "A designation with this name already exists.";
        if (r.Level is < 1 or > 10) return "Seniority must be between 1 and 10.";
        if (r.DepartmentId is { } dep && !await db.Departments.AnyAsync(d => d.Id == dep)) return "That department does not exist.";
        return null;
    }

    [HttpPost("designations")]
    public async Task<IActionResult> CreateDesignation(DesignationSaveRequest r)
    {
        var error = await ValidateDesignationAsync(r, null);
        if (error is not null) return BadRequest(new ApiResult { Success = false, Message = error });
        // You cannot give a designation more than you have yourself (the owner can give anything).
        if (!CanGrant(r.Rights, out var over)) return BadRequest(new ApiResult { Success = false, Message = over });
        var d = new Designation { Name = r.Name.Trim(), Description = Trim(r.Description, 500), DepartmentId = r.DepartmentId, Level = r.Level, IsActive = r.IsActive, RightsJson = PageCatalog.ToJson(r.Rights) };
        db.Designations.Add(d);
        await db.SaveChangesAsync();
        RightsService.Invalidate();
        log.LogInformation("[Org] {Who} created designation {Name}", MyName, d.Name);
        return StatusCode(201, new ApiResult { Success = true, Message = $"Designation {d.Name} created" });
    }

    [HttpPut("designations/{id:long}")]
    public async Task<IActionResult> UpdateDesignation(long id, DesignationSaveRequest r)
    {
        var d = await db.Designations.FirstOrDefaultAsync(x => x.Id == id);
        if (d is null) return NotFound(new ApiResult { Success = false, Message = "Designation not found" });
        var error = await ValidateDesignationAsync(r, id);
        if (error is not null) return BadRequest(new ApiResult { Success = false, Message = error });
        if (!CanGrant(r.Rights, out var over)) return BadRequest(new ApiResult { Success = false, Message = over });
        // Someone editing their own designation could otherwise lock themselves out or raise their own access.
        if (!IsAdmin && MyId is { } me && await db.Employees.AnyAsync(e => e.Id == me && e.DesignationId == id))
            return BadRequest(new ApiResult { Success = false, Message = "You cannot change the rights of your own designation. Ask an administrator." });
        d.Name = r.Name.Trim(); d.Description = Trim(r.Description, 500); d.DepartmentId = r.DepartmentId; d.Level = r.Level; d.IsActive = r.IsActive;
        d.RightsJson = PageCatalog.ToJson(r.Rights);
        await db.SaveChangesAsync();
        RightsService.Invalidate();
        log.LogInformation("[Org] {Who} changed designation {Name}", MyName, d.Name);
        return Ok(new ApiResult { Success = true, Message = "Designation saved. The new page rights apply straight away." });
    }

    [HttpDelete("designations/{id:long}")]
    public async Task<IActionResult> DeleteDesignation(long id)
    {
        var d = await db.Designations.FirstOrDefaultAsync(x => x.Id == id);
        if (d is null) return NotFound(new ApiResult { Success = false, Message = "Designation not found" });
        var people = await db.Employees.CountAsync(e => e.DesignationId == id);
        if (people > 0) return BadRequest(new ApiResult { Success = false, Message = $"{people} people still hold the designation {d.Name}. Give them another designation first, or switch it to inactive." });
        db.Designations.Remove(d);
        await db.SaveChangesAsync();
        RightsService.Invalidate();
        return Ok(new ApiResult { Success = true, Message = "Designation deleted" });
    }

    /// <summary>Only the owner / administrators may hand out every right; a manager can grant no more than they hold.</summary>
    private bool CanGrant(Dictionary<string, int>? rights, out string message)
    {
        message = "";
        if (IsAdmin) return true;
        foreach (var (page, level) in PageCatalog.Clean(rights))
            if (level > Level(page)) { message = $"You cannot grant more access to {PageCatalog.Find(page)?.Label ?? page} than you have yourself."; return false; }
        return true;
    }

    private static string? Trim(string? s, int max) { var v = s?.Trim(); return string.IsNullOrEmpty(v) ? null : v.Length > max ? v[..max] : v; }
}
