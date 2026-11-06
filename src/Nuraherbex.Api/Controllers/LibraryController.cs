using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Nuraherbex.Api.Options;
using Nuraherbex.Api.Services;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Controllers;

/// <summary>
/// Manage the file library. Everyone who chats with customers may list the files (to send them); only people with Team rights add, rename or delete.
/// The page rules in <see cref="PageRules"/> enforce that before these actions run.
/// </summary>
[ApiController]
[Route("api/admin/library")]
[Authorize(Roles = "admin,sales")]
public class LibraryAdminController(LibraryService library, IOptions<StoreOptions> store, RightsService rights) : StaffControllerBase(rights)
{
    private string PublicBase => string.IsNullOrWhiteSpace(store.Value.ApiPublicUrl) ? $"{Request.Scheme}://{Request.Host}" : store.Value.ApiPublicUrl;

    [HttpGet]
    public async Task<LibraryListResponse> List() => new() { Success = true, Files = await library.ListAsync(PublicBase) };

    /// <summary>Upload a file (multipart: title, description, file). Up to 100 MB; WhatsApp only takes videos up to 16 MB and images up to 5 MB as attachments, bigger ones go as a link.</summary>
    [HttpPost]
    [RequestSizeLimit(LibraryKinds.MaxUpload + 2 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = LibraryKinds.MaxUpload + 2 * 1024 * 1024)]
    public async Task<IActionResult> Upload([FromForm] string? title, [FromForm] string? description, IFormFile? file)
    {
        if (file is null || file.Length == 0) return BadRequest(new ApiResult { Success = false, Message = "Choose a file to upload." });
        await using var stream = file.OpenReadStream();
        var (added, error) = await library.AddAsync(stream, file.FileName, title ?? "", description, MyName, PublicBase);
        if (added is null) return BadRequest(new ApiResult { Success = false, Message = error });
        return StatusCode(201, new LibraryFileResponse { Success = true, Message = "File added to the library.", File = added });
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, LibraryUpdateRequest r)
    {
        var f = await library.UpdateAsync(id, r.Title, r.Description, PublicBase);
        return f is null ? NotFound(new ApiResult { Success = false, Message = "File not found." }) : Ok(new LibraryFileResponse { Success = true, Message = "Saved.", File = f });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id) =>
        await library.DeleteAsync(id)
            ? Ok(new ApiResult { Success = true, Message = "File deleted. Its link no longer works." })
            : NotFound(new ApiResult { Success = false, Message = "File not found." });
}

/// <summary>
/// The public link to a library file. Meta fetches it when a file is sent as an attachment, and customers open it from a link.
/// The random token in the address is the key: it opens only that file, and deleting the file closes it.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/library")]
public class LibraryFilesController(LibraryService library) : ControllerBase
{
    private static bool Inline(string mime) => mime == "application/pdf" || mime.StartsWith("image/") || mime.StartsWith("video/");

    [HttpGet("{id}/{token}/{name?}")]
    public async Task<IActionResult> Get(string id, string token, string? name)
    {
        if (await library.OpenAsync(id, token) is not { } f) return NotFound();
        Response.Headers["Cache-Control"] = "public, max-age=3600";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (Inline(f.Mime)) return PhysicalFile(f.Path, f.Mime, enableRangeProcessing: true); // range support lets videos seek and WhatsApp fetch in parts
        return PhysicalFile(f.Path, f.Mime, f.FileName);
    }
}
