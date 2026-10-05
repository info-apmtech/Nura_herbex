using System.Reflection;
using System.Text.Json;

namespace Nuraherbex.Shared.Models;

/// <summary>Editable marketing copy. Mirrors src/content/siteContent.json of the React storefront.</summary>
public class SiteContent
{
    public BrandContent Brand { get; set; } = new();
    public HeroContent Hero { get; set; } = new();
    public List<string> Marquee { get; set; } = new();
    public ScienceSectionContent ScienceSection { get; set; } = new();
    public SolutionSectionContent SolutionSection { get; set; } = new();
    public JourneySectionContent JourneySection { get; set; } = new();
    public DailyRitualSectionContent DailyRitualSection { get; set; } = new();
    public IngredientsSectionContent IngredientsSection { get; set; } = new();
    public ShopContent Shop { get; set; } = new();
    public FooterContent Footer { get; set; } = new();

    public static SiteContent Default()
    {
        var asm = typeof(SiteContent).Assembly;
        using var stream = asm.GetManifestResourceStream("siteContent.default.json")
            ?? throw new InvalidOperationException("Embedded default site content missing.");
        return JsonSerializer.Deserialize<SiteContent>(stream, NuraJson.Options) ?? new SiteContent();
    }

    public SiteContent Clone() =>
        JsonSerializer.Deserialize<SiteContent>(JsonSerializer.Serialize(this, NuraJson.Options), NuraJson.Options)!;
}

public class BrandContent
{
    public string Name { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string Tagline { get; set; } = "";
}

public class HeroStat
{
    public string Label { get; set; } = "";
    public string Sub { get; set; } = "";
}

public class HeroContent
{
    public string Badge { get; set; } = "";
    public string Headline { get; set; } = "";
    public string Subtext { get; set; } = "";
    public string CtaText { get; set; } = "";
    public string SecondaryCtaText { get; set; } = "";
    public List<HeroStat> Stats { get; set; } = new();
}

public class TitledItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
}

public class ScienceSectionContent
{
    public string Subtitle { get; set; } = "";
    public string Title { get; set; } = "";
    public string Intro { get; set; } = "";
    public List<TitledItem> Factors { get; set; } = new();
}

public class SolutionSectionContent
{
    public string Subtitle { get; set; } = "";
    public string Title { get; set; } = "";
    public string Intro { get; set; } = "";
    public List<TitledItem> Pillars { get; set; } = new();
}

public class JourneyMilestone
{
    public string Period { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
}

public class JourneySectionContent
{
    public string Subtitle { get; set; } = "";
    public string Title { get; set; } = "";
    public string Intro { get; set; } = "";
    public List<JourneyMilestone> Milestones { get; set; } = new();
}

public class RitualStep
{
    public string Step { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
}

public class DailyRitualSectionContent
{
    public string Subtitle { get; set; } = "";
    public string Title { get; set; } = "";
    public string Intro { get; set; } = "";
    public List<RitualStep> Steps { get; set; } = new();
}

public class IngredientItem
{
    public string Name { get; set; } = "";
    public string Dose { get; set; } = "";
    public string Desc { get; set; } = "";
}

public class IngredientsSectionContent
{
    public string Subtitle { get; set; } = "";
    public string Title { get; set; } = "";
    public string Intro { get; set; } = "";
    public List<IngredientItem> Items { get; set; } = new();
}

public class ProductCardContent
{
    public string Title { get; set; } = "";
    public string Pack { get; set; } = "";
    public decimal Price { get; set; }
    public decimal OriginalPrice { get; set; }
    public string Discount { get; set; } = "";
}

public class PdpContent
{
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string Description { get; set; } = "";
    public string ServingSize { get; set; } = "";
    public string NetQuantity { get; set; } = "";
    public string ItemForm { get; set; } = "";
    public string Formulation { get; set; } = "";
}

public class ShopContent
{
    public string BadgeTitle { get; set; } = "";
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public ProductCardContent ProductCard { get; set; } = new();
    public PdpContent Pdp { get; set; } = new();
}

public class FooterContent
{
    public string Tagline { get; set; } = "";
    public string Copyright { get; set; } = "";
}
