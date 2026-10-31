using Nuraherbex.Api.Options;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Services;

/// <summary>
/// Courier settings saved by the admin (site_settings). Whatever is saved there wins over appsettings; blank fields fall back to appsettings.
/// Kept in memory so the courier services pick up a change on their next use, with no restart.
/// </summary>
public class ShippingSettingsStore
{
    private volatile ShippingSettingsDto? _saved;

    public ShippingSettingsDto? Saved => _saved;

    public void Set(ShippingSettingsDto? settings)
    {
        _saved = settings;
        ShiprocketService.ResetToken(); // credentials may have changed: log in again
    }

    public async Task LoadAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        Set(await scope.ServiceProvider.GetRequiredService<SettingsService>().GetAsync<ShippingSettingsDto>(SettingsService.ShippingKey));
    }

    private static string Pick(string? saved, string fallback) => string.IsNullOrWhiteSpace(saved) ? fallback : saved.Trim();

    public ShiprocketOptions Apply(ShiprocketOptions o)
    {
        var s = _saved?.Shiprocket;
        if (s is null) return o;
        return new ShiprocketOptions
        {
            ApiUrl = o.ApiUrl, WebhookToken = o.WebhookToken,
            Email = Pick(s.Email, o.Email), Password = Pick(s.Password, o.Password), Token = Pick(s.Token, o.Token), PickupLocation = Pick(s.PickupLocation, o.PickupLocation),
        };
    }

    public ShadowfaxOptions Apply(ShadowfaxOptions o)
    {
        var s = _saved?.Shadowfax;
        if (_saved is null || s is null) return o;
        return new ShadowfaxOptions
        {
            Env = Pick(s.Env, o.Env), Token = Pick(s.Token, o.Token), StagingToken = Pick(s.StagingToken, o.StagingToken),
            OrderType = Pick(s.OrderType, o.OrderType), ServiceTier = Pick(s.ServiceTier, o.ServiceTier),
            WebhookToken = o.WebhookToken, AutoBook = _saved.AutoBook, Pickup = o.Pickup, Return = o.Return,
        };
    }

    /// <summary>The courier that books new orders: the admin's choice, else the Shipping:Provider setting.</summary>
    public string ActiveProvider(ShippingOptions o) =>
        string.Equals(_saved?.ActiveProvider, ShippingGateway.Shiprocket, StringComparison.OrdinalIgnoreCase) ? ShippingGateway.Shiprocket
        : string.Equals(_saved?.ActiveProvider, ShippingGateway.Shadowfax, StringComparison.OrdinalIgnoreCase) ? ShippingGateway.Shadowfax
        : string.Equals(o.Provider, ShippingGateway.Shiprocket, StringComparison.OrdinalIgnoreCase) ? ShippingGateway.Shiprocket : ShippingGateway.Shadowfax;
}
