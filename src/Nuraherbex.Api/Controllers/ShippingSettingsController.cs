using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Nuraherbex.Api.Options;
using Nuraherbex.Api.Services;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Controllers;

/// <summary>Admin "Shipping setup": choose Shiprocket or Shadowfax for new orders and keep their credentials.</summary>
[ApiController]
[Route("api/admin/shipping")]
[Authorize(Roles = "admin,sales")]
public class ShippingSettingsController(
    ShippingSettingsStore store,
    SettingsService settings,
    ShiprocketService shiprocket,
    ShadowfaxService shadowfax,
    IOptions<ShiprocketOptions> srOptions,
    IOptions<ShadowfaxOptions> sfOptions,
    IOptions<ShippingOptions> shippingOptions) : ControllerBase
{
    /// <summary>What is in effect now (saved values over appsettings), with secrets replaced by "saved" flags.</summary>
    private ShippingSettingsDto Effective()
    {
        var sr = store.Apply(srOptions.Value);
        var sf = store.Apply(sfOptions.Value);
        return new ShippingSettingsDto
        {
            ActiveProvider = store.ActiveProvider(shippingOptions.Value),
            AutoBook = sf.AutoBook,
            Shiprocket = new ShiprocketSettingsDto { Email = sr.Email, PickupLocation = sr.PickupLocation, HasPassword = sr.Password.Length > 0, HasToken = sr.Token.Length > 0, Configured = sr.IsConfigured },
            Shadowfax = new ShadowfaxSettingsDto { Env = sf.Env, OrderType = sf.OrderType, ServiceTier = sf.ServiceTier, HasToken = sf.Token.Length > 0, HasStagingToken = sf.StagingToken.Length > 0, Configured = sf.IsConfigured },
        };
    }

    [HttpGet("settings")]
    public IActionResult Get() => Ok(new ShippingSettingsResponse { Success = true, Settings = Effective() });

    [HttpPut("settings")]
    public async Task<IActionResult> Save(ShippingSettingsDto r)
    {
        if (r.ActiveProvider is not (ShippingGateway.Shiprocket or ShippingGateway.Shadowfax)) return BadRequest(new ApiResult { Message = "Choose Shiprocket or Shadowfax." });
        if (r.Shadowfax.Env is not ("staging" or "production")) return BadRequest(new ApiResult { Message = "Shadowfax environment must be staging or production." });
        if (r.Shadowfax.OrderType is not ("marketplace" or "warehouse")) return BadRequest(new ApiResult { Message = "Unsupported Shadowfax order type." });
        if (r.Shadowfax.ServiceTier is not ("Regular" or "Surface")) return BadRequest(new ApiResult { Message = "Unsupported Shadowfax service tier." });

        // Blank secret = keep what is saved (the browser is never given them). Whatever is not saved yet falls back to appsettings.
        var old = store.Saved;
        var next = new ShippingSettingsDto
        {
            ActiveProvider = r.ActiveProvider,
            AutoBook = r.AutoBook,
            Shiprocket = new ShiprocketSettingsDto
            {
                Email = r.Shiprocket.Email.Trim(), PickupLocation = r.Shiprocket.PickupLocation.Trim(),
                Password = string.IsNullOrWhiteSpace(r.Shiprocket.Password) ? old?.Shiprocket.Password ?? "" : r.Shiprocket.Password,
                Token = string.IsNullOrWhiteSpace(r.Shiprocket.Token) ? old?.Shiprocket.Token ?? "" : r.Shiprocket.Token.Trim(),
            },
            Shadowfax = new ShadowfaxSettingsDto
            {
                Env = r.Shadowfax.Env, OrderType = r.Shadowfax.OrderType, ServiceTier = r.Shadowfax.ServiceTier,
                Token = string.IsNullOrWhiteSpace(r.Shadowfax.Token) ? old?.Shadowfax.Token ?? "" : r.Shadowfax.Token.Trim(),
                StagingToken = string.IsNullOrWhiteSpace(r.Shadowfax.StagingToken) ? old?.Shadowfax.StagingToken ?? "" : r.Shadowfax.StagingToken.Trim(),
            },
        };
        await settings.SetAsync(SettingsService.ShippingKey, next);
        store.Set(next);

        var effective = Effective();
        var chosen = effective.ActiveProvider == ShippingGateway.Shiprocket ? effective.Shiprocket.Configured : effective.Shadowfax.Configured;
        return Ok(new ShippingSettingsResponse
        {
            Success = true, Settings = effective,
            Message = chosen ? $"{effective.ActiveProvider} is now booking new orders." : $"Saved, but {effective.ActiveProvider} has no credentials yet, so new shipments will fail until you add them.",
        });
    }

    /// <summary>Checks the saved credentials of one courier against its live API.</summary>
    [HttpPost("test")]
    public async Task<IActionResult> Test(ShippingTestRequest r)
    {
        if (r.Provider == ShippingGateway.Shiprocket)
        {
            var (ok, message) = await shiprocket.TestAsync();
            return Ok(new ApiResult { Success = ok, Message = message });
        }
        if (r.Provider == ShippingGateway.Shadowfax)
        {
            if (!shadowfax.IsConfigured) return Ok(new ApiResult { Message = "Add the Shadowfax token for the selected environment, save, then test." });
            var live = await shadowfax.IsServiceableAsync("600056");
            return Ok(live is null
                ? new ApiResult { Message = "Shadowfax did not accept the token or could not be reached." }
                : new ApiResult { Success = true, Message = "Connected to Shadowfax." });
        }
        return BadRequest(new ApiResult { Message = "Unknown courier." });
    }
}
