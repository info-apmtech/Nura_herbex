namespace Nuraherbex.Api.Data;

/// <summary>One ad's numbers for one day, copied from the Meta Marketing API (level = ad).</summary>
public class AdInsightDaily
{
    public long Id { get; set; }
    /// <summary>The day in the ad account's time zone (date only).</summary>
    public DateTime Date { get; set; }
    public string AdId { get; set; } = "";
    public string AdName { get; set; } = "";
    public string AdSetId { get; set; } = "";
    public string AdSetName { get; set; } = "";
    public string CampaignId { get; set; } = "";
    public string CampaignName { get; set; } = "";
    public decimal Spend { get; set; }
    public long Impressions { get; set; }
    public long Clicks { get; set; }
    /// <summary>Messaging conversations started from the ad, as Meta counts them.</summary>
    public long Conversations { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
