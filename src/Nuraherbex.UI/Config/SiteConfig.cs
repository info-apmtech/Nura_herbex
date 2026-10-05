namespace Nuraherbex.UI.Config;

/// <summary>Port of src/config/site.js and src/config/media.js.</summary>
public static class SiteConfig
{
    public const string Name = "Nura Herbex";
    public const string Tagline = "Stamix | Botanical Vitality Formula for Men";
    public const string Currency = "₹";
    public const string CurrencyCode = "INR";
    public const decimal FreeShippingThreshold = 999m;

    public const string ContactPhone = "08888003430";
    public const string ContactEmail = "care@nuraherbex.com";
    public const string ContactAddress = "Plot No. 79, SF No. 93/51, SV Nagar, Senneerkuppam, Poonamallee-600056";

    public const string Instagram = "https://instagram.com/nuraherbex";
    public const string Facebook = "https://facebook.com/nuraherbex";
    public const string YouTube = "https://youtube.com/nuraherbex";
    public const string IosAppUrl = "https://apps.apple.com";
    public const string AndroidAppUrl = "https://play.google.com";

    /// <summary>Formats an amount like the storefront does: ₹1,899.</summary>
    public static string Money(decimal amount) =>
        Currency + amount.ToString("#,##0", System.Globalization.CultureInfo.GetCultureInfo("en-IN"));
}

/// <summary>Cloudflare R2 media CDN (same bucket as the React storefront).</summary>
public static class Media
{
    public const string R2Base = "https://pub-8de00f4078374ac49c3851282661ae17.r2.dev/cloudflare-r2-assets";

    public static string Url(string filename) => $"{R2Base}/{Uri.EscapeDataString(filename)}";

    public static readonly string HeroVideo = Url("WhatsApp_Video_2026-09-10_at_8.44.40_AM.mp4");
    public static readonly string SolutionVideo = Url("58985-490319213_medium.mp4");

    public static readonly string StamixMockup1 = Url("stamix mock up -1 .webp");
    public static readonly string StamixMockup2 = Url("stamix mock up -2 .webp");
    public static readonly string StamixMockup3 = Url("stamix mock up -3 .webp");

    public static readonly string ScienceBanner = Url("edgar-chaparro-sHfo3WOgGTU-unsplash.webp");

    public static readonly string Incred1 = Url("incred1.webp");
    public static readonly string Incred2 = Url("incred2.webp");
    public static readonly string Incred3 = Url("incred3.webp");
    public static readonly string Incred4 = Url("incred4.webp");

    public static readonly string Step1 = Url("step1.webp");
    public static readonly string Step2 = Url("step2.webp");
    public static readonly string Step3 = Url("step3.webp");

    public static readonly string Journey1 = Url("journey-stage-1.webp");
    public static readonly string Journey2 = Url("journey-stage-2.webp");
    public static readonly string Journey3 = Url("journey-stage-3.webp");
    public static readonly string Journey4 = Url("journey-stage-4.webp");
}
