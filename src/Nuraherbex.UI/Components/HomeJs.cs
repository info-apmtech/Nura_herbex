using Microsoft.JSInterop;

namespace Nuraherbex.UI.Components;

/// <summary>
/// Helpers for the pinned/scroll-driven home sections (Journey, DailyRitual) and the background videos.
/// The JS lives in wwwroot/js/nura.js. Every call swallows JS errors.
/// </summary>
internal static class HomeJs
{
    public static Task EnsureAsync(IJSRuntime js) => Task.CompletedTask;

    /// <summary>Per id: [top, height, innerHeight, scrollY, innerWidth, scrollWidth] or null when the element is missing.</summary>
    public static async Task<double[]?[]?> ProbeAsync(IJSRuntime js, params string[] ids)
    {
        try { return await js.InvokeAsync<double[]?[]>("__nuraProbeMany", new object[] { ids }); } catch { return null; }
    }

    public static async Task ScrollToProgressAsync(IJSRuntime js, string id, double progress)
    {
        try { await js.InvokeVoidAsync("__nuraScrollToProgress", id, progress); } catch { }
    }

    public static async Task PlayVideoAsync(IJSRuntime js, string id)
    {
        try { await js.InvokeVoidAsync("__nuraPlayVideo", id); } catch { }
    }
}
