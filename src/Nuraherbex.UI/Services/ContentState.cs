using Microsoft.Extensions.DependencyInjection;
using Nuraherbex.Shared.Models;
using Nuraherbex.UI.Config;

namespace Nuraherbex.UI.Services;

/// <summary>Port of ContentContext.jsx — editable marketing copy. Starts from embedded defaults, then overlays the server copy.</summary>
public class ContentState(ApiClient api)
{
    public SiteContent Content { get; private set; } = SiteContent.Default();
    public bool IsSaving { get; private set; }
    public event Action? Changed;
    private bool _loaded;

    public async Task InitAsync()
    {
        if (_loaded) return;
        _loaded = true;
        var remote = await api.GetSiteContentAsync();
        if (remote is not null) { Content = remote; Changed?.Invoke(); }
    }

    /// <summary>Saves a draft (admin only) and, on success, publishes it to the live site.</summary>
    public async Task<ApiResult> SaveAsync(SiteContent draft)
    {
        IsSaving = true; Changed?.Invoke();
        var res = await api.AdminSaveContentAsync(draft);
        if (res.Success) Content = draft.Clone();
        IsSaving = false; Changed?.Invoke();
        return res;
    }

    public async Task<ApiResult> ResetToDefaultsAsync()
    {
        var res = await api.AdminResetContentAsync();
        if (res.Success) { Content = SiteContent.Default(); Changed?.Invoke(); }
        return res;
    }

    public string ExportJson() => System.Text.Json.JsonSerializer.Serialize(Content, new System.Text.Json.JsonSerializerOptions(NuraJson.Options) { WriteIndented = true });
}

/// <summary>Which global modal (review | privacy | terms) is open — port of App.jsx activeModal.</summary>
public class ModalState
{
    public string? Active { get; private set; }
    public event Action? Changed;

    public void Open(string type) { Active = type; Changed?.Invoke(); }
    public void Close() { Active = null; Changed?.Invoke(); }
}

public static class ServiceRegistration
{
    /// <summary>Registers all UI services. <paramref name="apiBaseUrl"/> is the Nuraherbex.Api address.</summary>
    public static IServiceCollection AddNuraherbexUI(this IServiceCollection services, string apiBaseUrl)
    {
        if (!apiBaseUrl.EndsWith('/')) apiBaseUrl += "/";
        // Scoped (not singleton): in MAUI Blazor Hybrid IJSRuntime is scoped, and scoped == app-lifetime in WebAssembly/Hybrid anyway.
        services.AddSingleton(new ApiOptions { BaseUrl = apiBaseUrl });
        services.AddSingleton<HttpClient>(sp => new HttpClient { BaseAddress = new Uri(apiBaseUrl), Timeout = TimeSpan.FromSeconds(60) });
        services.AddScoped<SessionTokens>();
        services.AddScoped<BrowserInterop>();
        services.AddScoped<ApiClient>();
        services.AddScoped<CartState>();
        services.AddScoped<AuthState>();
        services.AddScoped<AdminSession>();
        services.AddScoped<ContentState>();
        services.AddScoped<ModalState>();
        return services;
    }
}
