using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nuraherbex.Api.Services;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Controllers;

/// <summary>
/// The Ad campaigns report: which leads and customers came from Facebook / Instagram Click-to-WhatsApp ads, per campaign and ad.
/// "Everyone's records" on the Ad campaigns page sees all ad leads, spend and reach; "Own records" sees only leads assigned to them.
/// </summary>
[ApiController]
[Route("api/admin/ads")]
[Authorize(Roles = "admin,sales")]
public class AdsController(AdsReportService report, MetaAdsService meta, RightsService rights) : StaffControllerBase(rights)
{
    /// <summary>Everyone sees everything; otherwise only their own leads. Null owner = no limit.</summary>
    private (bool Allowed, long? OwnerOnly) Scope() =>
        CanAll("campaigns") ? (true, null) : MyId is { } me && Level("campaigns") >= 1 ? (true, me) : (false, null);

    /// <param name="from">First day, yyyy-MM-dd (default: 29 days ago).</param>
    /// <param name="to">Last day included, yyyy-MM-dd (default: today).</param>
    /// <param name="campaign">A campaign key from the filter list.</param>
    /// <param name="ad">An ad key from the filter list.</param>
    /// <param name="sourceType">ad | post</param>
    /// <param name="status">new | follow-up | interested | customer | not-interested</param>
    /// <param name="owner">An employee id, or "unassigned".</param>
    [HttpGet("report")]
    public async Task<IActionResult> Get([FromQuery] string? from, [FromQuery] string? to, [FromQuery] string? campaign, [FromQuery] string? ad,
        [FromQuery] string? sourceType, [FromQuery] string? status, [FromQuery] string? owner, [FromQuery] string? q, [FromQuery] bool customersOnly = false)
    {
        var (allowed, ownerOnly) = Scope();
        if (!allowed) return StatusCode(403, new ApiResult { Success = false, Message = "You do not have access to the Ad campaigns report." });

        var today = FollowUpService.IstNow.Date;
        var start = Parse(from) ?? today.AddDays(-29);
        var end = Parse(to) ?? today;
        if (end < start) (start, end) = (end, start);
        if ((end - start).TotalDays > 800) return BadRequest(new ApiResult { Success = false, Message = "Choose a period of up to about two years." });

        string? Clean(string? s, int max = 80) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(s.Trim().Length, max)];
        var query = new AdsQuery(start, end, Clean(campaign), Clean(ad), Clean(sourceType, 8) is "ad" or "post" ? Clean(sourceType, 8) : null,
            Clean(status, 20), Clean(owner, 20), Clean(q, 80), customersOnly);
        return Ok(await report.BuildAsync(query, ownerOnly));
    }

    /// <summary>Whether Meta is connected, when it last synced, and the last error (the access token is never included).</summary>
    [HttpGet("status")]
    public async Task<IActionResult> Status()
    {
        if (!Scope().Allowed) return StatusCode(403, new ApiResult { Success = false, Message = "You do not have access to the Ad campaigns report." });
        var s = await meta.StatusAsync();
        s.CanSync = CanAll("campaigns");
        s.CanConnect = IsAdmin;
        if (!s.CanConnect) { s.AdAccountId = null; } // only administrators see which ad account is connected
        return Ok(s);
    }

    /// <summary>Pulls the latest campaign, ad, spend and reach numbers from Meta now (people who see everyone's records only).</summary>
    [HttpPost("sync")]
    public async Task<IActionResult> Sync(CancellationToken ct)
    {
        if (!CanAll("campaigns")) return StatusCode(403, new ApiResult { Success = false, Message = "Only people who see everyone's ad records can sync with Meta." });
        var r = await meta.SyncAsync(ct);
        return r.Ok ? Ok(new ApiResult { Success = true, Message = r.Message }) : BadRequest(new ApiResult { Success = false, Message = r.Message });
    }

    // ---- Connection: the ad account and access token are entered here, by an administrator --------------------

    private IActionResult? NotAdmin() =>
        IsAdmin ? null : StatusCode(403, new ApiResult { Success = false, Message = "Only an administrator can connect or change the Meta account." });

    /// <summary>The saved connection. The access token is never returned, only whether one is saved and its last four characters.</summary>
    [HttpGet("connection")]
    public async Task<IActionResult> GetConnection() =>
        NotAdmin() ?? Ok(new MetaConnectionResponse { Success = true, Connection = await meta.ConnectionAsync() });

    /// <summary>Saves the ad account and token, then checks them with Meta so a typo is caught straight away.</summary>
    [HttpPut("connection")]
    public async Task<IActionResult> SaveConnection(MetaConnectionSaveRequest r, CancellationToken ct)
    {
        if (NotAdmin() is { } no) return no;
        if (await meta.SaveConnectionAsync(r, MyName) is { } error) return BadRequest(new ApiResult { Success = false, Message = error });
        var (ok, message, name) = await meta.TestAsync(ct);
        return Ok(new MetaConnectionResponse
        {
            Success = true, AccountName = name, Connection = await meta.ConnectionAsync(),
            Message = ok ? "Saved. " + message : "Saved, but Meta did not accept it yet: " + message,
        });
    }

    /// <summary>Checks the saved ad account and token against Meta without changing anything.</summary>
    [HttpPost("connection/test")]
    public async Task<IActionResult> TestConnection(CancellationToken ct)
    {
        if (NotAdmin() is { } no) return no;
        var (ok, message, name) = await meta.TestAsync(ct);
        return ok ? Ok(new MetaConnectionResponse { Success = true, AccountName = name, Message = message, Connection = await meta.ConnectionAsync() }) : BadRequest(new ApiResult { Success = false, Message = message });
    }

    /// <summary>Forgets what was saved in the app. Stored numbers stay; new ones stop until Meta is connected again.</summary>
    [HttpDelete("connection")]
    public async Task<IActionResult> Disconnect()
    {
        if (NotAdmin() is { } no) return no;
        await meta.ClearConnectionAsync();
        return Ok(new MetaConnectionResponse { Success = true, Connection = await meta.ConnectionAsync(), Message = "Disconnected. The numbers already stored stay in the report." });
    }

    private static DateTime? Parse(string? s) =>
        DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.Date : null;
}
