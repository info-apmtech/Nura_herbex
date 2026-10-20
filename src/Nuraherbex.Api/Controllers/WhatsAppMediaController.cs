using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nuraherbex.Api.Data;
using Nuraherbex.Api.Services;

namespace Nuraherbex.Api.Controllers;

/// <summary>
/// Serves photos, stickers, voice notes and documents customers sent on WhatsApp. The link carries its own short-lived
/// signature (made by the CRM when it lists a conversation), because an image tag cannot send a login header.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/whatsapp-media")]
public class WhatsAppMediaController(NuraDbContext db, WhatsAppService whatsapp, MediaSigner signer) : ControllerBase
{
    /// <summary>Only content types a browser can show safely inline; anything else is offered as a download.</summary>
    private static bool Inline(string mime) =>
        mime is "application/pdf" || (mime.StartsWith("image/") && mime is not "image/svg+xml") || mime.StartsWith("video/") || mime.StartsWith("audio/");

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id, [FromQuery] long exp, [FromQuery] string? sig)
    {
        if (!signer.IsValid(id, exp, sig)) return StatusCode(403);
        var msg = await db.WhatsAppMessages.FirstOrDefaultAsync(m => m.Id == id && m.MediaId != null);
        if (msg is null) return NotFound();

        // First view after a failed download (or an older message): try to fetch it from Meta now.
        if (msg.MediaFile is null || !System.IO.File.Exists(Path.Combine(whatsapp.MediaDirectory, Path.GetFileName(msg.MediaFile))))
        {
            msg.MediaFile = null;
            if (!await whatsapp.DownloadMediaAsync(msg, HttpContext.RequestAborted)) return NotFound();
        }

        var path = Path.Combine(whatsapp.MediaDirectory, Path.GetFileName(msg.MediaFile!));
        var mime = (msg.MediaMime ?? "application/octet-stream").Split(';')[0].Trim().ToLowerInvariant();
        Response.Headers["Cache-Control"] = "private, max-age=3600";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (Inline(mime)) return PhysicalFile(path, mime, enableRangeProcessing: true); // range support lets audio and video seek
        return PhysicalFile(path, "application/octet-stream", msg.MediaName ?? msg.MediaFile!);
    }
}
