namespace Nuraherbex.Shared.Models;

/// <summary>Admin-edited overrides for the page copy, images and videos listed in <see cref="PageContentCatalog"/>. Only changed keys are stored.</summary>
public class PageContentDto
{
    public Dictionary<string, string> Values { get; set; } = new();
}

public enum PageFieldKind { Text, LongText, Image, Video }

/// <param name="Key">Stable id used by the page component, such as <c>home.story.title</c>.</param>
/// <param name="Default">What the page shows until an admin changes it.</param>
public record PageField(string Page, string Section, string Key, string Label, PageFieldKind Kind, string Default);

/// <summary>Every piece of page content an admin can change. To make another text, image or video editable, add it here and read it with <c>Content.P(key)</c>.</summary>
public static class PageContentCatalog
{
    private const string R2 = "https://pub-8de00f4078374ac49c3851282661ae17.r2.dev/cloudflare-r2-assets";
    private const string Ui = "_content/Nuraherbex.UI";

    public static readonly IReadOnlyList<PageField> Fields =
    [
        // ---- Home ----
        new("Home", "Announcement bar", "home.announce.1", "Message 1", PageFieldKind.Text, "Free shipping on orders above ₹999"),
        new("Home", "Announcement bar", "home.announce.2", "Message 2", PageFieldKind.Text, "100% natural Ayurvedic formulations"),
        new("Home", "Announcement bar", "home.announce.3", "Message 3", PageFieldKind.Text, "Batch-verified quality"),
        new("Home", "Announcement bar", "home.announce.4", "Message 4", PageFieldKind.Text, "STAMIX — natural stamina, every day"),

        new("Home", "Hero (first slide)", "home.hero.kicker", "Small heading", PageFieldKind.Text, "Natural wellness for a brighter you"),
        new("Home", "Hero (first slide)", "home.hero.line1", "Title line 1", PageFieldKind.Text, "Better Digestion"),
        new("Home", "Hero (first slide)", "home.hero.line2", "Title line 2 (last word is highlighted)", PageFieldKind.Text, "Brighter Living"),
        new("Home", "Hero (first slide)", "home.hero.sub", "Description", PageFieldKind.LongText, "Gut & liver enhancement made from time-honoured herbs — more balance, more energy, a healthier you."),

        new("Home", "At a glance (stats strip)", "home.stats.video", "Background video", PageFieldKind.Video, $"{R2}/58985-490319213_medium.mp4"),
        new("Home", "At a glance (stats strip)", "home.stats.label1", "Stat 1 label", PageFieldKind.Text, "Whole botanicals in every scoop"),
        new("Home", "At a glance (stats strip)", "home.stats.label2", "Stat 2 label", PageFieldKind.Text, "Day daily ritual per 500 g jar"),
        new("Home", "At a glance (stats strip)", "home.stats.label3", "Stat 3 label", PageFieldKind.Text, "Natural, plant-based formula"),
        new("Home", "At a glance (stats strip)", "home.stats.label4", "Stat 4 label", PageFieldKind.Text, "Added preservatives"),

        new("Home", "Our story teaser", "home.story.kicker", "Small heading", PageFieldKind.Text, "OUR STORY"),
        new("Home", "Our story teaser", "home.story.title1", "Title line 1", PageFieldKind.Text, "Ancient Wisdom"),
        new("Home", "Our story teaser", "home.story.title2", "Title line 2", PageFieldKind.Text, "for Modern Living"),
        new("Home", "Our story teaser", "home.story.text", "Description", PageFieldKind.LongText, "At Nuraherbex, we blend time-tested herbal knowledge with modern science to create natural wellness solutions for a healthier, brighter tomorrow."),
        new("Home", "Our story teaser", "home.story.button", "Button text", PageFieldKind.Text, "Our Story"),

        new("Home", "Footer movement band", "home.footer.video", "Background video", PageFieldKind.Video, $"{Ui}/videos/exercise.mp4"),
        new("Home", "Footer movement band", "home.footer.poster", "Video poster image", PageFieldKind.Image, $"{Ui}/images/hero-bg-nature.png"),
        new("Home", "Footer movement band", "home.footer.eyebrow", "Small heading", PageFieldKind.Text, "Move · Recover · Thrive"),
        new("Home", "Footer movement band", "home.footer.title", "Heading", PageFieldKind.Text, "Build strength, one daily ritual at a time."),
        new("Home", "Footer movement band", "home.footer.text", "Description", PageFieldKind.LongText, "Pair a short movement routine with STAMIX for steady energy, stamina and recovery."),

        // ---- Shop ----
        new("Shop", "Page banner", "shop.hero.kicker", "Small heading", PageFieldKind.Text, "NATURAL WELLNESS FOR A BRIGHTER YOU"),
        new("Shop", "Page banner", "shop.hero.sub", "Description", PageFieldKind.LongText, "Whole-botanical, plant-powered formulas, batch verified and made to be part of your daily ritual."),
        new("Shop", "Page banner", "shop.hero.image", "Banner flowers image", PageFieldKind.Image, $"{Ui}/images/shop-blossom-sprig.png"),
    ];

    /// <summary>A block of a storefront page that an admin can hide on phones. Stored as <c>mobile.hide.{Id}</c> = "1" in the same page-content settings.</summary>
    public record MobileSection(string Page, string Id, string Label);

    public const string MobileHidePrefix = "mobile.hide.";
    public static string MobileKey(string id) => MobileHidePrefix + id;
    public static bool IsMobileKey(string key) => MobileSections.Any(m => MobileKey(m.Id) == key);

    public static readonly IReadOnlyList<MobileSection> MobileSections =
    [
        new("Home", "home.announce", "Announcement bar"),
        new("Home", "home.hero", "Hero banner"),
        new("Home", "home.benefits", "Benefits bar"),
        new("Home", "home.featured", "Featured products"),
        new("Home", "home.stamina", "Stamina power"),
        new("Home", "home.stats", "At a glance (stats strip)"),
        new("Home", "home.why", "Why choose us"),
        new("Home", "home.goals", "Wellness goals"),
        new("Home", "home.ingredients", "Ingredient spotlight"),
        new("Home", "home.bundles", "Bundles"),
        new("Home", "home.reviews", "Customer reviews"),
        new("Home", "home.story", "Our story teaser"),
        new("Home", "home.perks", "Perks"),
        new("Home", "home.faq", "FAQ"),
        new("Home", "home.footer", "Newsletter sign-up"),

        new("Shop", "shop.extras", "Extra sections below the products"),

        new("Our Story", "story.hero", "Intro banner"),
        new("Our Story", "story.values", "Three promises"),
        new("Our Story", "story.detail", "Story details"),
        new("Our Story", "story.quote", "Quote"),
        new("Our Story", "story.cta", "Begin your ritual"),

        new("Formulation", "fm.hero", "Intro banner"),
        new("Formulation", "fm.ticker", "Ingredient ticker"),
        new("Formulation", "fm.matrix", "Ingredient matrix"),
        new("Formulation", "fm.principles", "Science principles"),
        new("Formulation", "fm.standards", "Standards"),
        new("Formulation", "fm.faq", "Questions"),
        new("Formulation", "fm.cta", "Batch lab reports"),

        new("Quality", "quality.hero", "Intro banner"),
        new("Quality", "quality.sourcing", "01 · Ingredient sourcing"),
        new("Quality", "quality.manufacturing", "02 · Manufacturing"),
        new("Quality", "quality.testing", "03 · Quality testing"),
        new("Quality", "quality.packaging", "04 · Packaging"),
        new("Quality", "quality.commitment", "Our commitment"),

        new("FAQ", "faq.hero", "Intro banner"),
        new("FAQ", "faq.help", "Still need help?"),

        new("Contact", "contact.hero", "Intro banner"),
        new("Contact", "contact.channels", "Contact options"),
        new("Contact", "contact.form", "Message form"),
        new("Contact", "contact.help", "Quick help"),
    ];

    public static string DefaultOf(string key) => Fields.FirstOrDefault(f => f.Key == key)?.Default ?? "";
}
