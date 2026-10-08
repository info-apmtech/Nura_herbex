namespace Nuraherbex.Api.Options;

public class StoreOptions
{
    public const string Section = "Store";
    public string FrontendUrl { get; set; } = "http://localhost:5200";
    public string ApiPublicUrl { get; set; } = "http://localhost:5118";
    public string SupportEmail { get; set; } = "care@nuraherbex.com";
    public string SupportPhone { get; set; } = "08888003430";
    public decimal FreeShippingThreshold { get; set; } = 999;
    public decimal FlatShippingFee { get; set; } = 99;
}

public class JwtOptions
{
    public const string Section = "Jwt";
    public string Issuer { get; set; } = "nuraherbex-api";
    public string Audience { get; set; } = "nuraherbex-clients";
    /// <summary>HMAC signing key, minimum 32 characters. Set via user-secrets / environment.</summary>
    public string Secret { get; set; } = "";
    public int CustomerTokenDays { get; set; } = 30;
    public int AdminTokenHours { get; set; } = 8;
}

public class AdminOptions
{
    public const string Section = "Admin";
    public string PasswordHash { get; set; } = "";
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
}

public class PayUOptions
{
    public const string Section = "PayU";
    public string MerchantKey { get; set; } = "";
    public string MerchantSalt { get; set; } = "";
    /// <summary>test | live</summary>
    public string Env { get; set; } = "test";
    /// <summary>Demo only: when no key/salt is set, auto-approve payments without PayU. Never enable in production.</summary>
    public bool AllowSimulation { get; set; }
    public bool IsConfigured => !string.IsNullOrWhiteSpace(MerchantKey) && !string.IsNullOrWhiteSpace(MerchantSalt);
    public string ActionUrl => Env == "live" ? "https://secure.payu.in/_payment" : "https://test.payu.in/_payment";
}

public class ShiprocketOptions
{
    public const string Section = "Shiprocket";
    public string ApiUrl { get; set; } = "https://apiv2.shiprocket.in/v1/external";
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
    public string Token { get; set; } = "";
    public string PickupLocation { get; set; } = "Primary";
    public string WebhookToken { get; set; } = "";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Token) || (!string.IsNullOrWhiteSpace(Email) && !string.IsNullOrWhiteSpace(Password));
}

public class ShippingOptions
{
    public const string Section = "Shipping";
    /// <summary>Courier platform used for NEW orders: Shadowfax | Shiprocket. Existing orders keep the courier they were booked with.</summary>
    public string Provider { get; set; } = "Shadowfax";
}

public class ShadowfaxAddress
{
    public string Name { get; set; } = "";
    public string Contact { get; set; } = "";
    public string AddressLine1 { get; set; } = "";
    public string AddressLine2 { get; set; } = "";
    public string City { get; set; } = "";
    public string State { get; set; } = "";
    public string Pincode { get; set; } = "";
    public string Email { get; set; } = "";
    /// <summary>Warehouse code registered with Shadowfax (optional).</summary>
    public string UniqueCode { get; set; } = "";
    public bool IsComplete => !string.IsNullOrWhiteSpace(AddressLine1)
        && !string.IsNullOrWhiteSpace(City) && !string.IsNullOrWhiteSpace(Pincode)
        && Pincode.Length == 6 && Pincode.All(char.IsDigit);
}

public class ShadowfaxOptions
{
    public const string Section = "Shadowfax";
    /// <summary>staging | production</summary>
    public string Env { get; set; } = "staging";
    public string Token { get; set; } = "";
    public string StagingToken { get; set; } = "";
    /// <summary>marketplace (Shadowfax picks up from the seller) | warehouse (you hand over at a Shadowfax facility).</summary>
    public string OrderType { get; set; } = "marketplace";
    /// <summary>Regular or Surface, as provisioned for this Shadowfax account.</summary>
    public string ServiceTier { get; set; } = "Regular";
    /// <summary>Value Shadowfax sends in the callback Authorization header (set in the Shadowfax client portal, Webhook tab).</summary>
    public string WebhookToken { get; set; } = "";
    public ShadowfaxAddress Pickup { get; set; } = new();
    /// <summary>Return-to-seller / return-to-origin address. Falls back to <see cref="Pickup"/> when empty.</summary>
    public ShadowfaxAddress Return { get; set; } = new();
    public bool IsProduction => Env.Equals("production", StringComparison.OrdinalIgnoreCase);
    public string ApiUrl => IsProduction ? "https://dale.shadowfax.in/api" : "https://dale.staging.shadowfax.in/api";
    public string ActiveToken => IsProduction ? Token : StagingToken;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ActiveToken);
}

public class EmailOptions
{
    public const string Section = "Email";
    public string ResendApiKey { get; set; } = "";
    public string From { get; set; } = "Nura Herbex <care@nuraherbex.com>";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ResendApiKey);
}

public class WhatsAppOptions
{
    public const string Section = "WhatsApp";
    /// <summary>The string you type into "Verify token" in Meta's webhook configuration.</summary>
    public string VerifyToken { get; set; } = "";
    /// <summary>Meta App Secret — used to validate X-Hub-Signature-256 on incoming events.</summary>
    public string AppSecret { get; set; } = "";
    /// <summary>Permanent system-user access token for the Cloud API (outbound messages).</summary>
    public string AccessToken { get; set; } = "";
    public string PhoneNumberId { get; set; } = "";
    public string ApiVersion { get; set; } = "v21.0";
    public string DefaultCountryCode { get; set; } = "91";
    /// <summary>Approved template names. Leave empty to send plain text (only deliverable inside the 24h customer-service window).</summary>
    public string OrderConfirmationTemplate { get; set; } = "";
    public string OrderShippedTemplate { get; set; } = "";
    public string OrderDeliveredTemplate { get; set; } = "";
    public string TemplateLanguage { get; set; } = "en";
    public bool AutoReplyEnabled { get; set; } = true;
    public bool CanSend => !string.IsNullOrWhiteSpace(AccessToken) && !string.IsNullOrWhiteSpace(PhoneNumberId);
}
