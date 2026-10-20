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
        "Track Your Order & Batch Details | Nura Herbex — Stamix™",
        "View order delivery updates and product batch details for Stamix™ Botanical Nutrition Formula.",
        "https://nuraherbex.com/track",
        "Track Your Stamix™ Order | Nura Herbex");

    public static readonly PageMeta QualityReports = new(
        "Stamix™ Quality & Batch Test Reports | Nura Herbex",
        "Review available batch-specific Certificates of Analysis (COA), laboratory results, and product quality information for Stamix™ botanical nutrition formula.",
        "https://nuraherbex.com/quality-reports",
        "Stamix™ Product Quality & Batch Reports | Nura Herbex");

    public static readonly PageMeta Formulation = new(
        "Full Formulation & Botanical Ingredients | Stamix™ Nura Herbex",
        "Explore the full clinical formulation of Stamix™: 17 whole botanicals, herbs, seeds and nuts with exact milligrams added per 20g serving. 100% transparent and lab verified.",
        "https://nuraherbex.com/formulation",
        "Stamix™ Clinical Formulation & Botanical Ingredients | Nura Herbex");

    public static readonly PageMeta Account = new(
        "My Account, Orders & Reviews | Nura Herbex",
        "Sign in to manage Nura Herbex orders, delivery details, and product reviews. Review available batch quality reports and Certificates of Analysis.",
        "https://nuraherbex.com/account",
        "My Account & Order Management · Nura Herbex");

    public static readonly PageMeta Admin = new(
        "Store Admin & Operations | Nura Herbex",
        "Nura Herbex administration for products, orders, customers, review moderation, and batch quality report management.",
        "https://nuraherbex.com/admin",
        "Nura Herbex Store Administration");

    public static readonly PageMeta ReviewModal = new("Share Your Product Experience | Nura Herbex", "Submit a customer review for moderation and possible publication on the Nura Herbex storefront.");
    public static readonly PageMeta PrivacyModal = new("Privacy Policy & Data Security | Nura Herbex", "Military-grade privacy protection and 256-bit encrypted checkout compliance.");
    public static readonly PageMeta TermsModal = new("Terms of Service & 60-Day Guarantee | Nura Herbex", "Terms of service, warranty, and 60-day empty-jar satisfaction guarantee for Stamix™.");
}
