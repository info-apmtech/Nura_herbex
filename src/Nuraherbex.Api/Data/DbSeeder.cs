using Microsoft.EntityFrameworkCore;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Data;

public static class DbSeeder
{
    private const string R2 = "https://pub-8de00f4078374ac49c3851282661ae17.r2.dev/cloudflare-r2-assets";


    private static readonly Dictionary<string, (string Short, string? Badge, string Highlights, string Ingredients, string Directions)> StamixInfo = new()
    {
        ["stamix-single"] = ("STAMIX", "For Men", StamixHighlights, StamixIngredients, StamixDirections),
        ["stamix-duo"] = ("STAMIX Duo", "Most popular", StamixHighlights, StamixIngredients, StamixDirections),
        ["stamix-trio"] = ("STAMIX Trio", "Best value", StamixHighlights, StamixIngredients, StamixDirections),
        ["stamix-collector"] = ("STAMIX Collector Box", "Gift ready", StamixHighlights, StamixIngredients, StamixDirections),
    };
    private const string StamixHighlights = "Sustained energy through the day\nMental focus and clarity\nSupport for recovery and resilience\n17 whole botanicals, Ashwagandha-led";
    private const string StamixIngredients = "17 botanicals, herbs, seeds and nuts including whole Ashwagandha root, Kaunch beej (Mucuna), drumstick flower, bark and seed, and watermelon seed. See the pack label for the complete list.";
    private const string StamixDirections = "Mix a 20 g daily serving into warm milk or water and stir until smooth. Follow the directions on the pack label. Consult a healthcare professional if you are pregnant, nursing or on medication.";

    /// <summary>
    /// Idempotent catalogue top-up that also runs on databases that already have products: adds GUT 365 when it is missing and
    /// back-fills the descriptive fields (highlights, ingredients, directions...) only where they are still empty, so edits made in Admin are never overwritten.
    /// </summary>
    private static async Task EnsureCatalogAsync(NuraDbContext db)
    {
        if (!await db.Products.AnyAsync(p => p.Id == "gut-365"))
        {
            db.Products.Add(new Product
            {
                Id = "gut-365", Sku = "NH-GUT365-60C", Name = "GUT 365 — Gut & Liver Enhancement",
                Subtitle = "Daily powder · 30-day supply",
                Description = "Gut balance, digestive stimulant, natural energiser and antioxidant-rich botanicals in an easy daily powder.",
                Price = 1299, StockQuantity = 200, WeightKg = 0.12m, LengthCm = 8, BreadthCm = 8, HeightCm = 11,
                Category = "Gut & Liver Enhancement", ImageUrl = "/_content/Nuraherbex.UI/images/product-1.png",
            });
            await db.SaveChangesAsync();
        }

        foreach (var p in await db.Products.Where(x => x.Id.StartsWith("stamix") || x.Id == "gut-365").ToListAsync())
        {
            if (p.Id == "gut-365")
            {
                // GUT 365 is a powder: fix any earlier "capsule" wording.
                if (p.Subtitle?.Contains("capsule", StringComparison.OrdinalIgnoreCase) == true) p.Subtitle = "Daily powder · 30-day supply";
                if (p.ServingInfo?.Contains("capsule", StringComparison.OrdinalIgnoreCase) == true) p.ServingInfo = "Daily powder · 30-day supply";
                if (p.Description?.Contains("capsule", StringComparison.OrdinalIgnoreCase) == true) p.Description = p.Description.Replace("daily capsule", "daily powder", StringComparison.OrdinalIgnoreCase);
                p.ShortName ??= "GUT 365";
                p.Badge ??= "Bestseller";
                p.ServingInfo ??= "Daily powder · 30-day supply";
                p.Highlights ??= "Supports healthy digestion\nNatural daily energiser\nAntioxidant-rich botanicals\nGut balance and liver support";
                p.Ingredients ??= "A herbal gut-balance botanical blend. See the pack label for the complete ingredient list.";
                p.Directions ??= "Follow the directions on the pack label. Consult a healthcare professional if you are pregnant, nursing or on medication.";
            }
            else if (StamixInfo.TryGetValue(p.Id, out var info))
            {
                p.ShortName ??= info.Short;
                p.Badge ??= info.Badge;
                p.ServingInfo ??= p.Subtitle;
                p.Category ??= "Botanical Vitality Formula for Men";
                p.Highlights ??= info.Highlights;
                p.Ingredients ??= info.Ingredients;
                p.Directions ??= info.Directions;
            }
        }
        await db.SaveChangesAsync();
    }

    public static async Task SeedAsync(NuraDbContext db, bool seedDemoBatch = false)
    {
        if (!await db.Products.AnyAsync())
        {
            db.Products.AddRange(
                new Product
                {
                    Id = "stamix-single", Sku = "NH-STAMIX-300G", Name = "Stamix Botanical Vitality Formula (300g Canister)",
                    Subtitle = "15-Day Supply · Botanical Vitality Formula for Men",
                    Description = "17 botanicals, herbs, seeds and nuts at a full 20 g daily serving. Includes free 10g precision measuring scoop.",
                    Price = 1899, CompareAtPrice = 2499, StockQuantity = 250, WeightKg = 0.38m, LengthCm = 10, BreadthCm = 10, HeightCm = 14,
                    ImageUrl = $"{R2}/stamix%20mock%20up%20-1%20.webp",
                },
                new Product
                {
                    Id = "stamix-duo", Sku = "NH-STAMIX-600G", Name = "Stamix Duo Optimizer Pack (600g - Two 300g Canisters)",
                    Subtitle = "30-Day Supply (600g) · Peak Vitality Routine",
                    Description = "Two 300g canisters (600g total — full 30-day supply at 20g/day). Includes free 10g precision measuring scoop.",
                    Price = 3499, CompareAtPrice = 4999, StockQuantity = 180, WeightKg = 0.76m, LengthCm = 20, BreadthCm = 10, HeightCm = 14,
                    ImageUrl = $"{R2}/stamix%20mock%20up%20-1%20.webp",
                },
                new Product
                {
                    Id = "stamix-trio", Sku = "NH-STAMIX-900G", Name = "Stamix Master Vitality System (900g - Three 300g Canisters)",
                    Subtitle = "45-Day Supply (900g) · Complete Biological Reset",
                    Description = "Three 300g canisters (900g total — 45-day supply). Comprehensive daily vitality routine with free shaker bottle.",
                    Price = 4999, CompareAtPrice = 7499, StockQuantity = 120, WeightKg = 1.14m, LengthCm = 30, BreadthCm = 10, HeightCm = 14,
                    ImageUrl = $"{R2}/stamix%20mock%20up%20-2%20.webp",
                },
                new Product
                {
                    Id = "stamix-collector", Sku = "NH-STAMIX-BOX", Name = "Stamix Collector Presentation Box (15-Day)",
                    Subtitle = "Full Luxury Unboxing Experience",
                    Description = "Full luxury presentation suite with embossed gift box, 300g canister (15-day supply), precision scoop, and daily ritual handbook.",
                    Price = 2499, CompareAtPrice = 3299, StockQuantity = 50, WeightKg = 0.65m, LengthCm = 24, BreadthCm = 18, HeightCm = 12,
                    ImageUrl = $"{R2}/f14b6700d_1a16d4a6-ec6b-4738-b677-25006baf6d71.webp",
                });
        }

        await EnsureCatalogAsync(db);

        if (!await db.Coupons.AnyAsync())
        {
            db.Coupons.AddRange(
                new Coupon { Code = "STAMIX10", DiscountType = "PERCENTAGE", DiscountValue = 10, MinOrderAmount = 0, MaxDiscountCap = 500 },
                new Coupon { Code = "SAVE10", DiscountType = "PERCENTAGE", DiscountValue = 10, MinOrderAmount = 0, MaxDiscountCap = 500 },
                new Coupon { Code = "STAMIX20", DiscountType = "PERCENTAGE", DiscountValue = 20, MinOrderAmount = 3000, MaxDiscountCap = 1500 },
                new Coupon { Code = "WELCOME20", DiscountType = "PERCENTAGE", DiscountValue = 20, MinOrderAmount = 3000, MaxDiscountCap = 1500 });
        }

        // Off by default so a fresh install shows exactly what the React storefront shows (no batches until you add one in Admin).
        if (seedDemoBatch && !await db.TrustBatches.AnyAsync())
        {
            db.TrustBatches.Add(new TrustBatch
            {
                Id = "STX-2609-A17", BatchNo = "STX-2609-A17", ProductName = "STAMIX Vitality Mix 300G", PackSize = "300 g | 30 Servings",
                MfgDate = "08 Sep 2026", ExpDate = "07 Sep 2028", Status = "Active / Verified",
                DocumentName = "STAMIX_BATCH_STX-2609-A17_TRUST_PASSPORT.pdf", FileSize = "2.4 MB",
                LabName = "Apex Analytical Labs (NABL & FSSAI Accredited)",
                Notes = "Full ICP-MS heavy metals, microbial assay, and standardized withanolides HPLC testing certified.",
                QualityChecks =
                [
                    new() { Label = "Identity", Status = "Passed" },
                    new() { Label = "Purity", Status = "Passed" },
                    new() { Label = "Microbial Test", Status = "Passed" },
                    new() { Label = "Heavy Metals", Status = "Within Limits" },
                    new() { Label = "Label Match", Status = "Confirmed" },
                ],
                Documents =
                [
                    new() { Id = "coa", Title = "Certificate of Analysis (COA)", Desc = "Full laboratory test report", ActionText = "VIEW PDF", Tag = "Certified Lab Results",
                        Details = "Standardized 5% Withanolides (KSM-66® Ashwagandha), 50% Fulvic Acid (Himalayan Shilajit), 20% Ecdysteroids (Safed Musli). Zero adulterants detected." },
                    new() { Id = "microbial", Title = "Microbial Test Report", Desc = "Microbiological safety results", ActionText = "VIEW PDF", Tag = "Zero Pathogens",
                        Details = "Total Plate Count < 100 CFU/g, Yeast & Mold < 10 CFU/g. E. Coli, Salmonella, and Staphylococcus aureus: ABSENT." },
                    new() { Id = "heavy-metals", Title = "Heavy Metals Report", Desc = "Heavy metal testing results", ActionText = "VIEW PDF", Tag = "ICP-MS Certified",
                        Details = "Tested via Inductively Coupled Plasma Mass Spectrometry. Lead (Pb) < 0.01 ppm, Arsenic (As) < 0.02 ppm, Cadmium (Cd) < 0.005 ppm, Mercury (Hg) < 0.001 ppm." },
                    new() { Id = "fssai", Title = "FSSAI / Manufacturing Details", Desc = "License and manufacturing info", ActionText = "VIEW DETAILS", Tag = "FSSAI #11223999000181",
                        Details = "Manufactured in WHO-GMP & ISO 22000:2018 certified facility. FSSAI Central License No. 11223999000181. Formulated in accordance with botanical pharmacopoeia standards." },
                    new() { Id = "certs", Title = "Certification Documents", Desc = "Product certifications and compliance", ActionText = "VIEW FILES", Tag = "ISO / GMP / HACCP",
                        Details = "Complete regulatory dossier including Non-GMO declaration, 100% Vegetarian certification, and zero synthetic fillers validation." },
                ],
            });
        }

        await db.SaveChangesAsync();
    }
}
