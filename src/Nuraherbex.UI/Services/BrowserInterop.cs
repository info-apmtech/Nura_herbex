using Microsoft.JSInterop;
using Nuraherbex.UI.Config;

namespace Nuraherbex.UI.Services;

/// <summary>
/// Thin, failure-tolerant wrapper over wwwroot/js/nura.js. Every call swallows JS errors so a blocked
/// storage API (private mode, WebView restrictions) never breaks rendering.
/// </summary>
public class BrowserInterop(IJSRuntime js)
{
    public async Task<string?> GetLocalAsync(string key) { try { return await js.InvokeAsync<string?>("nura.storage.get", "local", key); } catch { return null; } }
    public async Task SetLocalAsync(string key, string value) { try { await js.InvokeVoidAsync("nura.storage.set", "local", key, value); } catch { } }
    public async Task RemoveLocalAsync(string key) { try { await js.InvokeVoidAsync("nura.storage.remove", "local", key); } catch { } }

    public async Task<string?> GetSessionAsync(string key) { try { return await js.InvokeAsync<string?>("nura.storage.get", "session", key); } catch { return null; } }
    public async Task SetSessionAsync(string key, string value) { try { await js.InvokeVoidAsync("nura.storage.set", "session", key, value); } catch { } }
    public async Task RemoveSessionAsync(string key) { try { await js.InvokeVoidAsync("nura.storage.remove", "session", key); } catch { } }

    /// <summary>Starts the scroll-progress bar, parallax variable and cinematic IntersectionObserver reveals (port of useScrollEffects).</summary>
    public async Task InitScrollEffectsAsync() { try { await js.InvokeVoidAsync("nura.initScrollEffects"); } catch { } }

    public async Task ScrollToTopAsync(bool smooth = true) { try { await js.InvokeVoidAsync("nura.scrollToTop", smooth); } catch { } }
    public async Task ScrollToIdAsync(string id, int delayMs = 100) { try { await js.InvokeVoidAsync("nura.scrollToId", id, delayMs); } catch { } }

    public async Task SetMetaAsync(PageMeta meta, string? canonicalOverride = null)
    {
        try { await js.InvokeVoidAsync("nura.setMeta", meta.Title, meta.Description, canonicalOverride ?? meta.Canonical, meta.OgTitle ?? meta.Title); } catch { }
    }

    public async Task CopyToClipboardAsync(string text) { try { await js.InvokeVoidAsync("nura.copyText", text); } catch { } }

    /// <summary>Builds and submits a hidden POST form (used to redirect the browser to PayU's hosted checkout).</summary>
    public async Task PostFormAsync(string actionUrl, IDictionary<string, string> fields) { try { await js.InvokeVoidAsync("nura.postForm", actionUrl, fields); } catch { } }

    public async Task OpenUrlAsync(string url, string target = "_blank") { try { await js.InvokeVoidAsync("nura.openUrl", url, target); } catch { } }

    /// <summary>Triggers a client-side file download.</summary>
    public async Task DownloadTextAsync(string filename, string content, string mime = "application/json") { try { await js.InvokeVoidAsync("nura.downloadText", filename, content, mime); } catch { } }

    /// <summary>Reads a user-picked file input as a data: URL (certificate image upload). Returns null on failure.</summary>
    public async Task<FilePick?> ReadFileAsDataUrlAsync(string inputElementId)
    {
        try { return await js.InvokeAsync<FilePick?>("nura.readFileAsDataUrl", inputElementId); } catch { return null; }
    }

    public async Task<bool> ConfirmAsync(string message) { try { return await js.InvokeAsync<bool>("confirm", message); } catch { return false; } }
}

public class FilePick
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public long Size { get; set; }
    public string DataUrl { get; set; } = "";
}
