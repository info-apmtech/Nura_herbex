using Nuraherbex.Shared.Models;

namespace Nuraherbex.UI.Components.Admin;

/// <summary>The 17 standard clinical formulation ingredients (mirrors DEFAULT_FORMULATION_INGREDIENTS / SettingsService.DefaultIngredients()).</summary>
public static class AdminDefaults
{
    public static List<FormulationIngredientDto> Ingredients() =>
    [
        I(1, "Ashwagandha", "Withania somnifera (Root)", "1,500 mg", "Adaptogen & Vigor", "Supports adrenal resilience, reduces serum cortisol, and boosts stamina."),
        I(2, "Purified Himalayan Shilajit", "Asphaltum punjabianum (Exudate)", "500 mg", "Bioactive Minerals", "Contains >50% Fulvic Acid; accelerates mitochondrial ATP cellular energy."),
        I(3, "Safed Musli", "Chlorophytum borivilianum (Tuber)", "1,200 mg", "Strength & Vitality", "Rich in natural saponins to support male reproductive vitality and physical vigor."),
        I(4, "Gokshura (Tribulus)", "Tribulus terrestris (Fruit)", "1,000 mg", "Hormonal Balance", "Standardized to protodioscin to optimize nitric oxide and free testosterone."),
        I(5, "Kaunch Beej", "Mucuna pruriens (Seed)", "1,000 mg", "Neuro-Endocrine", "Natural L-DOPA precursor promoting dopamine, drive, and peak physical performance."),
        I(6, "Shatavari", "Asparagus racemosus (Root)", "800 mg", "Cellular Nourishment", "Rasayana rejuvenator that enhances tissue recovery and oxidative defense."),
        I(7, "Vidarikand", "Pueraria tuberosa (Tuber)", "750 mg", "Endurance & Bulk", "Supports healthy lean muscle tone and replenishes depleted vital fluids."),
        I(8, "Akarkara", "Anacyclus pyrethrum (Root)", "400 mg", "Nerve & Microcirculation", "Enhances nervous system response, libido, and vascular blood flow."),
        I(9, "Jaiphal (Nutmeg)", "Myristica fragrans (Seed)", "250 mg", "Restorative Synergy", "Aromatic therapeutic compound supporting deep neuromuscular relaxation."),
        I(10, "Elaichi (Green Cardamom)", "Elettaria cardamomum (Pod)", "300 mg", "Bioavailability Enhancer", "Improves digestion, cellular uptake, and nutrient bioavailability."),
        I(11, "Dalchini (Ceylon Cinnamon)", "Cinnamomum verum (Bark)", "300 mg", "Metabolic Support", "Supports healthy insulin sensitivity and blood glucose metabolism."),
        I(12, "California Almond Powder", "Prunus dulcis (Whole Nut)", "4,000 mg", "Nuts & Seed Matrix", "Plant protein, healthy monosaturated lipids, and bioavailable Vitamin E."),
        I(13, "Kashmiri Walnut Kernel", "Juglans regia (Nut)", "3,000 mg", "Nuts & Seed Matrix", "High-density plant Omega-3 ALA fatty acids for cardiovascular and brain health."),
        I(14, "Pistachio Matrix", "Pistacia vera (Nut)", "2,000 mg", "Nuts & Seed Matrix", "Potassium, lutein, and amino acids for sustained stamina output."),
        I(15, "Watermelon Seed Extract", "Citrullus lanatus (Seed)", "1,000 mg", "Nuts & Seed Matrix", "Naturally rich in L-Citrulline, promoting healthy arterial vasodilation."),
        I(16, "Pumpkin Seed Protein", "Cucurbita pepo (Seed)", "1,000 mg", "Trace Mineral Zinc", "Rich bioavailable zinc and magnesium crucial for prostate and hormone synthesis."),
        I(17, "Sunflower & Sesame Meal", "Helianthus annuus & Sesamum indicum", "1,000 mg", "Nuts & Seed Matrix", "Provides structural lignans, calcium, and complete amino acid profile."),
    ];

    private static FormulationIngredientDto I(int n, string name, string botanical, string amount, string category, string benefit) =>
        new() { Id = $"ing-{n}", Name = name, BotanicalName = botanical, Amount = amount, Category = category, Benefit = benefit };
}
