namespace Nuraherbex.Shared.Models;

/// <summary>
/// The certifications shown on the Trust Passport for a batch that has none of its own, as in the approved design.
/// Names, dates and links must match the certificates the company really holds: add the batch's own list in Admin → Batch reports → Certifications to replace these.
/// </summary>
public static class BatchCertDefaults
{
    public static readonly List<BatchDocumentDto> All =
    [
        new() { Id = "d-fssai", Kind = "product", Title = "FSSAI License", ValidTill = "Dec 2026" },
        new() { Id = "d-iso22000", Kind = "product", Title = "ISO 22000", ValidTill = "May 2027" },
        new() { Id = "d-gmp", Kind = "product", Title = "GMP Certified", ValidTill = "Jun 2027" },
        new() { Id = "d-usda", Kind = "brand", Title = "USDA Organic", ValidTill = "Jan 2027" },
        new() { Id = "d-haccp", Kind = "brand", Title = "HACCP Certification", ValidTill = "Jun 2027" },
        new() { Id = "d-iso9001", Kind = "brand", Title = "ISO 9001:2015", ValidTill = "Aug 2027" },
    ];
}
