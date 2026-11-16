using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nuraherbex.Api.Services;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Controllers;

[ApiController]
[Route("api/admin/drive")]
[Authorize(Roles = "admin,sales")]
public class DriveController(GoogleDriveService drive, RightsService rights) : StaffControllerBase(rights)
{
    [HttpGet("files")]
    public async Task<DriveListResponse> Files([FromQuery] string? q, [FromQuery] string? kind, CancellationToken ct) =>
        await drive.ListFilesAsync(q, kind, ct);

    [HttpGet("settings")]
    public async Task<DriveSettingsDto> GetSettings() => await drive.GetSettingsAsync();

    [HttpPut("settings")]
    public async Task<IActionResult> SaveSettings(DriveSettingsSaveRequest r)
    {
        var (ok, msg) = await drive.SaveSettingsAsync(r);
        return ok ? Ok(new ApiResult { Success = true, Message = msg }) : BadRequest(new ApiResult { Success = false, Message = msg });
    }

    [HttpPost("share")]
    public async Task<IActionResult> Share(DriveShareRequest r, CancellationToken ct)
    {
        var res = await drive.EnsureShareLinkAsync(r.FileId, ct);
        return res.Success ? Ok(res) : BadRequest(res);
    }
}

