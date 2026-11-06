using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nuraherbex.Api.Services;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Controllers;

/// <summary>Business reports for orders, leads, follow-ups, WhatsApp, tasks, customers and payments. Dates are Indian calendar days.</summary>
[ApiController]
[Route("api/admin/reports")]
[Authorize(Roles = "admin,sales")]
public class ReportController(ReportService reports, RightsService rights) : StaffControllerBase(rights)
{
    /// <summary>Who may open a report and whose records it covers. Administrators and people with "everyone's records" on Reports see everything;
    /// everyone else gets the leads, follow-ups and tasks reports (for pages they work on, or with "own records" on Reports), limited to their own records unless they manage that page.</summary>
    private (bool Allowed, long? OwnerOnly, string? Scope) Access(string key)
    {
        if (IsAdmin || CanAll("reports")) return (true, null, null);
        var page = key switch { "leads" => "leads", "followups" => "followups", "tasks" => "tasks", _ => null };
        if (page is null) return (false, null, null);
        if (CanAll(page)) return (true, null, null);
        if (Level(page) < 1 && Level("reports") < 1) return (false, null, null); // "Reports: own records" opens these three, limited to the person's own
        return MyId is { } me ? (true, me, "Showing your own records only.") : (false, null, null);
    }

    [HttpGet]
    public ReportListResponse List() => new() { Success = true, Reports = ReportService.Catalog.Where(r => Access(r.Key).Allowed).ToList() };

    /// <param name="from">First day, yyyy-MM-dd (default: 29 days ago).</param>
    /// <param name="to">Last day included, yyyy-MM-dd (default: today).</param>
    [HttpGet("{key}")]
    public async Task<IActionResult> Get(string key, [FromQuery] string? from, [FromQuery] string? to)
    {
        var access = Access(key.ToLowerInvariant());
        if (!access.Allowed) return StatusCode(403, new ApiResult { Success = false, Message = "You do not have access to this report." });
        var today = FollowUpService.IstNow.Date;
        var start = Parse(from) ?? today.AddDays(-29);
        var end = Parse(to) ?? today;
        if (end < start) (start, end) = (end, start);
        if ((end - start).TotalDays > 800) return BadRequest(new ApiResult { Success = false, Message = "Choose a period of up to about two years." });

        var label = start == end ? start.ToString("d MMM yyyy", CultureInfo.InvariantCulture) : $"{start:d MMM yyyy} to {end:d MMM yyyy}";
        var result = await reports.BuildAsync(key.ToLowerInvariant(), FollowUpService.ToUtc(start), FollowUpService.ToUtc(end.AddDays(1)), label, access.OwnerOnly);
        if (result is not null) result.ScopeNote = access.Scope;
        return result is null ? NotFound(new ApiResult { Success = false, Message = "Unknown report." }) : Ok(result);
    }

    private static DateTime? Parse(string? s) =>
        DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.Date : null;
}
