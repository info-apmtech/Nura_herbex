using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nuraherbex.Api.Data;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Services;

/// <summary>JSON key/value store (site_settings) for editable site content, formulation ingredients and metrics.</summary>
public class SettingsService(NuraDbContext db)
{
    public const string SiteContentKey = "site_content";
    public const string IngredientsKey = "formulation_ingredients";
    public const string MetricsKey = "formulation_metrics";

    public async Task<T?> GetAsync<T>(string key) where T : class
    {
        var row = await db.SiteSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key);
        if (row is null) return null;
        try { return JsonSerializer.Deserialize<T>(row.Value, NuraJson.Options); } catch { return null; }
    }

    public async Task SetAsync<T>(string key, T value)
    {
        var json = JsonSerializer.Serialize(value, NuraJson.Options);
        var row = await db.SiteSettings.FirstOrDefaultAsync(s => s.Key == key);
        if (row is null) db.SiteSettings.Add(new SiteSetting { Key = key, Value = json });
        else { row.Value = json; row.UpdatedAt = DateTime.UtcNow; }
        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(string key)
    {
        var row = await db.SiteSettings.FirstOrDefaultAsync(s => s.Key == key);
        if (row is null) return;
        db.SiteSettings.Remove(row);
        await db.SaveChangesAsync();
    }

    public static List<FormulationIngredientDto> DefaultIngredients() =>
    [
        new() { Id = "ing-1", Name = "Ashwagandha", BotanicalName = "Withania somnifera (Root)", Amount = "1,500 mg", Category = "Adaptogen & Vigor", Benefit = "Supports adrenal resilience, reduces serum cortisol, and boosts stamina." },
        new() { Id = "ing-2", Name = "Purified Himalayan Shilajit", BotanicalName = "Asphaltum punjabianum (Exudate)", Amount = "500 mg", Category = "Bioactive Minerals", Benefit = "Contains >50% Fulvic Acid; accelerates mitochondrial ATP cellular energy." },
        new() { Id = "ing-3", Name = "Safed Musli", BotanicalName = "Chlorophytum borivilianum (Tuber)", Amount = "1,200 mg", Category = "Strength & Vitality", Benefit = "Rich in natural saponins to support male reproductive vitality and physical vigor." },
        new() { Id = "ing-4", Name = "Gokshura (Tribulus)", BotanicalName = "Tribulus terrestris (Fruit)", Amount = "1,000 mg", Category = "Hormonal Balance", Benefit = "Standardized to protodioscin to optimize nitric oxide and free testosterone." },
        new() { Id = "ing-5", Name = "Kaunch Beej", BotanicalName = "Mucuna pruriens (Seed)", Amount = "1,000 mg", Category = "Neuro-Endocrine", Benefit = "Natural L-DOPA precursor promoting dopamine, drive, and peak physical performance." },
        new() { Id = "ing-6", Name = "Shatavari", BotanicalName = "Asparagus racemosus (Root)", Amount = "800 mg", Category = "Cellular Nourishment", Benefit = "Rasayana rejuvenator that enhances tissue recovery and oxidative defense." },
        new() { Id = "ing-7", Name = "Vidarikand", BotanicalName = "Pueraria tuberosa (Tuber)", Amount = "750 mg", Category = "Endurance & Bulk", Benefit = "Supports healthy lean muscle tone and replenishes depleted vital fluids." },
        new() { Id = "ing-8", Name = "Akarkara", BotanicalName = "Anacyclus pyrethrum (Root)", Amount = "400 mg", Category = "Nerve & Microcirculation", Benefit = "Enhances nervous system response, libido, and vascular blood flow." },
        new() { Id = "ing-9", Name = "Jaiphal (Nutmeg)", BotanicalName = "Myristica fragrans (Seed)", Amount = "250 mg", Category = "Restorative Synergy", Benefit = "Aromatic therapeutic compound supporting deep neuromuscular relaxation." },
        new() { Id = "ing-10", Name = "Elaichi (Green Cardamom)", BotanicalName = "Elettaria cardamomum (Pod)", Amount = "300 mg", Category = "Bioavailability Enhancer", Benefit = "Improves digestion, cellular uptake, and nutrient bioavailability." },
        new() { Id = "ing-11", Name = "Dalchini (Ceylon Cinnamon)", BotanicalName = "Cinnamomum verum (Bark)", Amount = "300 mg", Category = "Metabolic Support", Benefit = "Supports healthy insulin sensitivity and blood glucose metabolism." },
        new() { Id = "ing-12", Name = "California Almond Powder", BotanicalName = "Prunus dulcis (Whole Nut)", Amount = "4,000 mg", Category = "Nuts & Seed Matrix", Benefit = "Plant protein, healthy monosaturated lipids, and bioavailable Vitamin E." },
        new() { Id = "ing-13", Name = "Kashmiri Walnut Kernel", BotanicalName = "Juglans regia (Nut)", Amount = "3,000 mg", Category = "Nuts & Seed Matrix", Benefit = "High-density plant Omega-3 ALA fatty acids for cardiovascular and brain health." },
        new() { Id = "ing-14", Name = "Pistachio Matrix", BotanicalName = "Pistacia vera (Nut)", Amount = "2,000 mg", Category = "Nuts & Seed Matrix", Benefit = "Potassium, lutein, and amino acids for sustained stamina output." },
        new() { Id = "ing-15", Name = "Watermelon Seed Extract", BotanicalName = "Citrullus lanatus (Seed)", Amount = "1,000 mg", Category = "Nuts & Seed Matrix", Benefit = "Naturally rich in L-Citrulline, promoting healthy arterial vasodilation." },
        new() { Id = "ing-16", Name = "Pumpkin Seed Protein", BotanicalName = "Cucurbita pepo (Seed)", Amount = "1,000 mg", Category = "Trace Mineral Zinc", Benefit = "Rich bioavailable zinc and magnesium crucial for prostate and hormone synthesis." },
        new() { Id = "ing-17", Name = "Sunflower & Sesame Meal", BotanicalName = "Helianthus annuus & Sesamum indicum", Amount = "1,000 mg", Category = "Nuts & Seed Matrix", Benefit = "Provides structural lignans, calcium, and complete amino acid profile." },
    ];
}
