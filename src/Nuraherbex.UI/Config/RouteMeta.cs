namespace Nuraherbex.UI.Config;

public record PageMeta(string Title, string Description, string? Canonical = null, string? OgTitle = null);

/// <summary>Port of ROUTE_CONFIGS in src/utils/router.js — drives document title, description and canonical tags.</summary>
public static class RouteMeta
{
    public static readonly PageMeta Home = new(
        "Nura Herbex — Stamix | Botanical Vitality Formula for Men",
        "Stamix is a herbal health supplement for adult men — 17 botanicals, herbs, seeds and nuts, at a full 20 g daily serving. Expertly formulated to support everyday energy, stamina, focus and recovery.",
        "https://nuraherbex.com/");

    public static readonly PageMeta Shop = new(
        "Shop Stamix™ Botanical Formula | Official Nura Herbex Store",
        "Explore authentic Stamix 15-day canisters and wellness formulations with verified third-party ICP-MS lab testing.",
        "https://nuraherbex.com/shop");

    public static readonly PageMeta Pdp = new(
        "Stamix™ Botanical Vitality Formula (300g Canister) | Nura Herbex",
        "Buy Stamix™ 300g Canister (15-Day Supply). 17 botanicals, herbs, seeds and nuts, zero synthetic fillers, zero maltodextrin for peak male baseline vitality.",
        "https://nuraherbex.com/products/stamix",
        "Stamix™ Botanical Vitality Formula (300g Canister) — Nura Herbex");

    public static readonly PageMeta Checkout = new(
        "Checkout | Nura Herbex — Stamix™ Vitality Formula",
        "Complete your secure order for Stamix™ Botanical Vitality Formula for Men. Cash on Delivery and Fast Free Express Shipping across India.",
        "https://nuraherbex.com/checkout",
        "Secure Checkout · Stamix™ | Nura Herbex");

    public static readonly PageMeta Track = new(
        "Track Your Order & Batch Verification | Nura Herbex — Stamix™",
        "Check real-time Shiprocket AWB logistics telemetry, shipment milestones, and verify batch authenticity for Stamix™ Botanical Vitality Formula for Men.",
        "https://nuraherbex.com/track",
        "Track Your Stamix™ Order | Nura Herbex");

    public static readonly PageMeta TrustPassport = new(
        "Trust Passport | Stamix™ Batch Verification · Nura Herbex",
        "Official Nura Herbex Trust Passport. Review verified batch details, Certificate of Analysis (COA), ICP-MS heavy metals testing, and FSSAI compliance.",
        "https://nuraherbex.com/trust-passport",
        "Trust Passport — Verified Stamix™ Batch Dossier | Nura Herbex");

    public static readonly PageMeta Formulation = new(
        "Full Formulation & Botanical Ingredients | Stamix™ Nura Herbex",
        "Explore the full clinical formulation of Stamix™: 17 whole botanicals, herbs, seeds and nuts with exact milligrams added per 20g serving. 100% transparent and lab verified.",
        "https://nuraherbex.com/formulation",
        "Stamix™ Clinical Formulation & Botanical Ingredients | Nura Herbex");

    public static readonly PageMeta Account = new(
        "Vitality Account & Order Tracking | Nura Herbex",
        "Sign in to your Nura Herbex Vitality Account. View live Shiprocket courier tracking, batch Certificate of Analysis dossiers, and manage delivery addresses.",
        "https://nuraherbex.com/account",
        "Vitality Account & Order Tracking · Nura Herbex");

    public static readonly PageMeta Admin = new(
        "Admin Portal & Logistics Telemetry | Nura Herbex",
        "Internal administration operations center for Nura Herbex. Real-time order fulfillment tracking, Shiprocket API sync, and Trust Passport batch certificate governance.",
        "https://nuraherbex.com/admin",
        "Nura Herbex Admin Portal & Fulfillment Operations");

    public static readonly PageMeta ReviewModal = new("Submit Clinical Verification & Review | Nura Herbex", "Verified reviews and community feedback for Stamix™ Botanical Formula.");
    public static readonly PageMeta PrivacyModal = new("Privacy Policy & Data Security | Nura Herbex", "Military-grade privacy protection and 256-bit encrypted checkout compliance.");
    public static readonly PageMeta TermsModal = new("Terms of Service & 60-Day Guarantee | Nura Herbex", "Terms of service, warranty, and 60-day empty-jar satisfaction guarantee for Stamix™.");
}
