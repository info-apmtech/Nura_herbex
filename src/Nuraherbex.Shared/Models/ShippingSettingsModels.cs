namespace Nuraherbex.Shared.Models;

/// <summary>Courier choice and credentials the store admin can change without a redeploy. Secrets are never sent back to the browser: the Has* flags say whether one is saved.</summary>
public class ShippingSettingsDto
{
    /// <summary>Courier that books new orders: Shiprocket or Shadowfax. Existing shipments stay with the courier that booked them.</summary>
    public string ActiveProvider { get; set; } = "Shadowfax";
    /// <summary>Book the pickup automatically once an order is confirmed.</summary>
    public bool AutoBook { get; set; } = true;
    public ShiprocketSettingsDto Shiprocket { get; set; } = new();
    public ShadowfaxSettingsDto Shadowfax { get; set; } = new();
}

public class ShiprocketSettingsDto
{
    public string Email { get; set; } = "";
    /// <summary>Blank when saving means "keep the saved password".</summary>
    public string Password { get; set; } = "";
    /// <summary>Optional API token; blank keeps the saved one.</summary>
    public string Token { get; set; } = "";
    public string PickupLocation { get; set; } = "Primary";
    public bool HasPassword { get; set; }
    public bool HasToken { get; set; }
    public bool Configured { get; set; }
}

public class ShadowfaxSettingsDto
{
    /// <summary>staging | production</summary>
    public string Env { get; set; } = "production";
    /// <summary>Production token; blank when saving keeps the saved one.</summary>
    public string Token { get; set; } = "";
    public string StagingToken { get; set; } = "";
    /// <summary>marketplace | warehouse</summary>
    public string OrderType { get; set; } = "warehouse";
    /// <summary>Regular | Surface</summary>
    public string ServiceTier { get; set; } = "Regular";
    public bool HasToken { get; set; }
    public bool HasStagingToken { get; set; }
    public bool Configured { get; set; }
}

public class ShippingSettingsResponse : ApiResult
{
    public ShippingSettingsDto? Settings { get; set; }
}

public class ShippingTestRequest
{
    public string Provider { get; set; } = "";
}
