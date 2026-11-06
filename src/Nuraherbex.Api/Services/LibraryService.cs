using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nuraherbex.Api.Data;
using Nuraherbex.Api.Options;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Services;

/// <summary>
/// The file library: PDFs, videos and images an administrator uploads once and staff send to customers, as a WhatsApp attachment or as a link.
/// Files live on disk; the list is one JSON setting. Every file has its own random token, so its public link cannot be guessed, and deleting the file closes the link.
/// </summary>
public class LibraryService(NuraDbContext db, IOptions<WhatsAppOptions> options, ILogger<LibraryService> log, IWebHostEnvironment? env = null)
{
    private const string Key = "library.files";

    private sealed class Entry
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string? Description { get; set; }
        public string Kind { get; set; } = LibraryKinds.Document;
        public string StoredName { get; set; } = "";
        public string FileName { get; set; } = "";
        public string Mime { get; set; } = "";
        public long Size { get; set; }
        public string Token { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public string UploadedBy { get; set; } = "";
        public int TimesSent { get; set; }
    }

    /// <summary>What may be uploaded. WhatsApp itself only takes JPEG/PNG images, MP4/3GP video and common document types.</summary>
    private static readonly Dictionary<string, (string Kind, string Mime)> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = (LibraryKinds.Pdf, "application/pdf"),
        [".mp4"] = (LibraryKinds.Video, "video/mp4"),
        [".3gp"] = (LibraryKinds.Video, "video/3gpp"),
        [".jpg"] = (LibraryKinds.Image, "image/jpeg"),
        [".jpeg"] = (LibraryKinds.Image, "image/jpeg"),
        [".png"] = (LibraryKinds.Image, "image/png"),
        [".doc"] = (LibraryKinds.Document, "application/msword"),
        [".docx"] = (LibraryKinds.Document, "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
        [".xls"] = (LibraryKinds.Document, "application/vnd.ms-excel"),
        [".xlsx"] = (LibraryKinds.Document, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
        [".ppt"] = (LibraryKinds.Document, "application/vnd.ms-powerpoint"),
        [".pptx"] = (LibraryKinds.Document, "application/vnd.openxmlformats-officedocument.presentationml.presentation"),
        [".txt"] = (LibraryKinds.Document, "text/plain"),
    };

    public static string AllowedExtensions => string.Join(", ", Allowed.Keys.Select(k => k.TrimStart('.').ToUpperInvariant()));

    public string Directory_ => string.IsNullOrWhiteSpace(options.Value.LibraryPath)
        ? Path.Combine(env?.ContentRootPath ?? AppContext.BaseDirectory, "App_Data", "whatsapp-library")
        : options.Value.LibraryPath;

    private async Task<List<Entry>> LoadAsync()
    {
        var row = await db.SiteSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == Key);
        if (row is null) return [];
        try { return JsonSerializer.Deserialize<List<Entry>>(row.Value, NuraJson.Options) ?? []; }
        catch (JsonException) { return []; }
    }

    private async Task SaveAsync(List<Entry> all)
    {
        var json = JsonSerializer.Serialize(all, NuraJson.Options);
        var row = await db.SiteSettings.FirstOrDefaultAsync(s => s.Key == Key);
        if (row is null) db.SiteSettings.Add(new SiteSetting { Key = Key, Value = json });
        else { row.Value = json; row.UpdatedAt = DateTime.UtcNow; }
        await db.SaveChangesAsync();
    }

    private static string PublicUrl(Entry e, string publicBase) =>
        $"{publicBase.TrimEnd('/')}/api/library/{e.Id}/{e.Token}/{Uri.EscapeDataString(e.FileName)}";

    private static LibraryFileDto ToDto(Entry e, string publicBase) => new()
    {
        Id = e.Id, Title = e.Title, Description = e.Description, Kind = e.Kind, FileName = e.FileName, Mime = e.Mime, Size = e.Size,
        CreatedAt = e.CreatedAt, UploadedBy = e.UploadedBy, Url = PublicUrl(e, publicBase), CanAttach = LibraryKinds.CanAttach(e.Kind, e.Size), TimesSent = e.TimesSent,
    };

    public async Task<List<LibraryFileDto>> ListAsync(string publicBase) =>
        (await LoadAsync()).OrderByDescending(e => e.CreatedAt).Select(e => ToDto(e, publicBase)).ToList();

    public async Task<LibraryFileDto?> FindAsync(string id, string publicBase) =>
        (await LoadAsync()).FirstOrDefault(e => e.Id == id) is { } e ? ToDto(e, publicBase) : null;

    /// <summary>Checks the file looks like what its extension says, so a renamed program or script is not accepted as a PDF.</summary>
    private static bool LooksRight(string ext, byte[] head) => ext.ToLowerInvariant() switch
    {
        ".pdf" => head.Length >= 4 && head[0] == '%' && head[1] == 'P' && head[2] == 'D' && head[3] == 'F',
        ".mp4" or ".3gp" => head.Length >= 12 && head[4] == 'f' && head[5] == 't' && head[6] == 'y' && head[7] == 'p',
        ".jpg" or ".jpeg" => head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF,
        ".png" => head.Length >= 8 && head[0] == 0x89 && head[1] == 'P' && head[2] == 'N' && head[3] == 'G',
        ".docx" or ".xlsx" or ".pptx" => head.Length >= 2 && head[0] == 'P' && head[1] == 'K',
        ".doc" or ".xls" or ".ppt" => head.Length >= 4 && head[0] == 0xD0 && head[1] == 0xCF && head[2] == 0x11 && head[3] == 0xE0,
        _ => true,
    };

    private static string CleanName(string name)
    {
        var n = Path.GetFileName(name ?? "file");
        var bad = Path.GetInvalidFileNameChars().Concat(['"', '\'', '%', '#', '?', '&', ';']).ToHashSet();
        var cleaned = new string(n.Select(c => bad.Contains(c) || char.IsControl(c) ? '-' : c).ToArray()).Trim('.', ' ');
        return cleaned.Length == 0 ? "file" : cleaned.Length > 120 ? cleaned[^120..] : cleaned;
    }

    /// <summary>Stores an uploaded file. Returns the new item, or an error message.</summary>
    public async Task<(LibraryFileDto? File, string? Error)> AddAsync(Stream content, string originalName, string title, string? description, string uploadedBy, string publicBase)
    {
        var fileName = CleanName(originalName);
        var ext = Path.GetExtension(fileName);
        if (!Allowed.TryGetValue(ext, out var type)) return (null, $"That file type is not supported. Use {AllowedExtensions}.");
        title = (title ?? "").Trim();
        if (title.Length == 0) title = Path.GetFileNameWithoutExtension(fileName);
        if (title.Length > 120) return (null, "The title is too long (120 characters at most).");
        if ((description?.Length ?? 0) > 400) return (null, "The description is too long (400 characters at most).");

        var id = Guid.NewGuid().ToString("N")[..12];
        var stored = id + ext.ToLowerInvariant();
        Directory.CreateDirectory(Directory_);
        var path = Path.Combine(Directory_, stored);
        long size;
        try
        {
            await using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                var head = new byte[16]; var headLen = 0;
                int read;
                while ((read = await content.ReadAsync(buffer)) > 0)
                {
                    if (headLen < head.Length) { var take = Math.Min(read, head.Length - headLen); Array.Copy(buffer, 0, head, headLen, take); headLen += take; }
                    await fs.WriteAsync(buffer.AsMemory(0, read));
                    if (fs.Length > LibraryKinds.MaxUpload) { throw new InvalidDataException("too big"); }
                }
                size = fs.Length;
                if (size == 0) throw new InvalidDataException("empty");
                if (!LooksRight(ext, head[..headLen])) throw new InvalidDataException("mismatch");
            }
        }
        catch (InvalidDataException ex)
        {
            TryDelete(path);
            return (null, ex.Message switch
            {
                "too big" => "That file is too large (100 MB at most).",
                "empty" => "That file is empty.",
                _ => $"The file does not look like a real {ext.TrimStart('.').ToUpperInvariant()} file.",
            });
        }
        catch { TryDelete(path); throw; }

        var all = await LoadAsync();
        var entry = new Entry
        {
            Id = id, Title = title, Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(), Kind = type.Kind, StoredName = stored, FileName = fileName,
            Mime = type.Mime, Size = size, Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(), UploadedBy = uploadedBy,
        };
        all.Add(entry);
        await SaveAsync(all);
        log.LogInformation("[Library] {Who} added {Kind} {Title} ({Size} bytes)", uploadedBy, entry.Kind, entry.Title, size);
        return (ToDto(entry, publicBase), null);
    }

    public async Task<LibraryFileDto?> UpdateAsync(string id, string title, string? description, string publicBase)
    {
        var all = await LoadAsync();
        var e = all.FirstOrDefault(x => x.Id == id);
        if (e is null) return null;
        e.Title = string.IsNullOrWhiteSpace(title) ? e.Title : title.Trim()[..Math.Min(title.Trim().Length, 120)];
        e.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim()[..Math.Min(description.Trim().Length, 400)];
        await SaveAsync(all);
        return ToDto(e, publicBase);
    }

    public async Task<bool> DeleteAsync(string id)
    {
        var all = await LoadAsync();
        var e = all.FirstOrDefault(x => x.Id == id);
        if (e is null) return false;
        all.Remove(e);
        await SaveAsync(all);
        TryDelete(Path.Combine(Directory_, Path.GetFileName(e.StoredName)));
        return true;
    }

    public async Task CountSentAsync(string id)
    {
        var all = await LoadAsync();
        if (all.FirstOrDefault(x => x.Id == id) is not { } e) return;
        e.TimesSent++;
        await SaveAsync(all);
    }

    /// <summary>The file on disk for a public link, or null when the id or token is wrong (compared in constant time).</summary>
    public async Task<(string Path, string Mime, string FileName, string Kind)?> OpenAsync(string id, string token)
    {
        var e = (await LoadAsync()).FirstOrDefault(x => x.Id == id);
        if (e is null) return null;
        var a = Encoding.ASCII.GetBytes(e.Token); var b = Encoding.ASCII.GetBytes((token ?? "").ToLowerInvariant());
        if (a.Length != b.Length || !CryptographicOperations.FixedTimeEquals(a, b)) return null;
        var path = Path.Combine(Directory_, Path.GetFileName(e.StoredName));
        return File.Exists(path) ? (path, e.Mime, e.FileName, e.Kind) : null;
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { /* leave it; harmless */ } }

    // ---------------------------------------------------------------------
    // Temporary samples so the library is not empty on first use
    // ---------------------------------------------------------------------

    /// <summary>Adds a sample PDF and a sample video once (only while the library has never been used). Staff can delete them any time.</summary>
    public async Task SeedSamplesAsync()
    {
        try
        {
            if (await db.SiteSettings.AnyAsync(s => s.Key == Key)) return;
            await SaveAsync([]); // mark as initialised, so deleting the samples is final
            await using (var pdf = new MemoryStream(SamplePdf()))
                await AddAsync(pdf, "usage-guide-sample.pdf", "Usage guide (sample)", "Temporary sample. Delete it and upload your real guide.", "system", "https://example.invalid");
            var video = Path.Combine(AppContext.BaseDirectory, "Content", "library-samples", "sample-video.mp4");
            if (File.Exists(video))
                await using (var fs = File.OpenRead(video))
                    await AddAsync(fs, "product-video-sample.mp4", "Product video (sample)", "Temporary sample. Delete it and upload your real video.", "system", "https://example.invalid");
        }
        catch (Exception ex) { log.LogWarning(ex, "[Library] Sample files skipped"); }
    }

    /// <summary>A small, valid one-page PDF (built by hand so the sample needs no library).</summary>
    public static byte[] SamplePdf()
    {
        var lines = new[] { "Nura Herbex", "Usage guide (sample)", "", "This is a temporary sample file.", "Upload your real guide in the file library.", "", "Take the powder daily at the same time, as printed on the pack." };
        var text = new StringBuilder("BT /F1 22 Tf 72 740 Td 26 TL\n");
        foreach (var l in lines) text.Append('(').Append(l.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)")).Append(") Tj T*\n");
        text.Append("ET");
        var stream = text.ToString();
        var objs = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}\nendstream",
        };
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objs.Length; i++) { offsets.Add(sb.Length); sb.Append($"{i + 1} 0 obj\n{objs[i]}\nendobj\n"); }
        var xref = sb.Length;
        sb.Append($"xref\n0 {objs.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objs.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
