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

    public static string DefaultOf(string key) => Fields.FirstOrDefault(f => f.Key == key)?.Default ?? "";
}
