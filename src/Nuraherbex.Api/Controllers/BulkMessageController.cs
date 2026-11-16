using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nuraherbex.Api.Services;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Controllers;

[ApiController]
[Route("api/admin/bulk-messages")]
[Authorize(Roles = "admin,sales")]
public class BulkMessageController(BulkMessageService bulk, RightsService rights) : StaffControllerBase(rights)
{
    [HttpGet("preview")]
    public async Task<BulkPreviewResponse> Preview([FromQuery] string category = BulkCategories.AllLeads) =>
        await bulk.PreviewAsync(category);

    [HttpGet("campaigns")]
    public async Task<BulkCampaignListResponse> Campaigns() => await bulk.ListAsync();

    [HttpGet("campaigns/{id:long}")]
    public async Task<IActionResult> Detail(long id)
    {
        var res = await bulk.DetailAsync(id);
        return res is null ? NotFound(new ApiResult { Success = false, Message = "Campaign not found." }) : Ok(res);
    }

    [HttpPost("send")]
    public async Task<IActionResult> Send(BulkSendRequest r)
    {
        var (campaign, error) = await bulk.CreateAndStartAsync(r, MyName, runInBackground: true);
        if (campaign is null) return BadRequest(new ApiResult { Success = false, Message = error });
        return StatusCode(202, new ApiResult<BulkCampaignDto>
        {
            Success = true,
            Data = campaign,
            Message = $"Bulk campaign \"{campaign.Title}\" queued for {campaign.TotalTarget} recipient(s).",
        });
    }
}

