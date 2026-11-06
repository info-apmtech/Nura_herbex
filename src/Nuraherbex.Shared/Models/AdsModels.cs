namespace Nuraherbex.Shared.Models;

// ---------------------------------------------------------------------------
// Ad campaigns report: who came from Facebook / Instagram (Meta) Click-to-WhatsApp ads
// ---------------------------------------------------------------------------

/// <summary>The headline numbers for the people (and spend) that match the current filters.</summary>
public class AdsKpis
{
    public int AdLeads { get; set; }
    /// <summary>Interested leads who are not customers yet.</summary>
    public int HotLeads { get; set; }
    public int Customers { get; set; }
    public int NotInterested { get; set; }
    public int ConversionPercent { get; set; }
    public decimal Revenue { get; set; }
    public int Orders { get; set; }
    public decimal AverageOrderValue { get; set; }
    /// <summary>Spend is known (Meta is connected and this view is not narrowed to part of the people).</summary>
    public bool HasSpend { get; set; }
    public decimal Spend { get; set; }
    public decimal? CostPerLead { get; set; }
    public decimal? CostPerCustomer { get; set; }
    /// <summary>Revenue divided by spend.</summary>
    public decimal? Roas { get; set; }
    public long Impressions { get; set; }
    public long Clicks { get; set; }
    /// <summary>Conversations Meta counted as started from the ads.</summary>
    public long Conversations { get; set; }
}

public class AdsTrendPoint
{
    /// <summary>Label of the day (or first day of the week), e.g. "12 Oct".</summary>
    public string Day { get; set; } = "";
    public string Date { get; set; } = "";
    public int Leads { get; set; }
    public int Customers { get; set; }
    public decimal Spend { get; set; }
}

public class AdsFunnelRow
{
    /// <summary>new | follow-up | interested | customer | not-interested</summary>
    public string Status { get; set; } = "";
    public int Count { get; set; }
}

public class AdsCampaignRow
{
    public string Key { get; set; } = "";
    public string Campaign { get; set; } = "";
    public int Ads { get; set; }
    public int Leads { get; set; }
    public int Hot { get; set; }
    public int Customers { get; set; }
    public int ConversionPercent { get; set; }
    public decimal Revenue { get; set; }
    public decimal Spend { get; set; }
    public decimal? CostPerLead { get; set; }
    public decimal? CostPerCustomer { get; set; }
    public decimal? Roas { get; set; }
}

public class AdsAdRow
{
    public string Key { get; set; } = "";
    public string Ad { get; set; } = "";
    public string Campaign { get; set; } = "";
    public string CampaignKey { get; set; } = "";
    /// <summary>ad | post</summary>
    public string AdType { get; set; } = "ad";
    public int Leads { get; set; }
    public int Hot { get; set; }
    public int Customers { get; set; }
    public int ConversionPercent { get; set; }
    public decimal Revenue { get; set; }
    public decimal Spend { get; set; }
    public decimal? CostPerLead { get; set; }
}

public class AdsPersonRow
{
    public long LeadId { get; set; }
    public string Name { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Campaign { get; set; } = "";
    public string Ad { get; set; } = "";
    public string AdType { get; set; } = "ad";
    /// <summary>Facebook | Instagram | Meta</summary>
    public string Platform { get; set; } = "Meta";
    public DateTime FirstAdContactAt { get; set; }
    /// <summary>The lead status, or "customer" once a paid order exists.</summary>
    public string Status { get; set; } = "new";
    public bool IsCustomer { get; set; }
    public string? OwnerName { get; set; }
    public int Orders { get; set; }
    public decimal Revenue { get; set; }
    public DateTime LastContactAt { get; set; }
}

public class AdsOption
{
    public string Value { get; set; } = "";
    public string Label { get; set; } = "";
}

public class AdsFilterOptions
{
    public List<AdsOption> Campaigns { get; set; } = new();
    public List<AdsOption> Ads { get; set; } = new();
    public List<AdsOption> Owners { get; set; } = new();
}

public class AdsReportResponse : ApiResult
{
    /// <summary>First and last day included, yyyy-MM-dd (Indian calendar days).</summary>
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Period { get; set; } = "";
    /// <summary>Meta is connected, so campaign names, spend and reach are available.</summary>
    public bool MetaConnected { get; set; }
    /// <summary>Cost figures are hidden because the view is narrowed to part of the people (status, owner, search) and spend cannot be split that way.</summary>
    public bool CostHidden { get; set; }
    /// <summary>Shown to people who only see their own leads.</summary>
    public string? ScopeNote { get; set; }
    public AdsKpis Kpis { get; set; } = new();
    public List<AdsTrendPoint> Trend { get; set; } = new();
    /// <summary>True when each point is a week rather than a day.</summary>
    public bool TrendWeekly { get; set; }
    public List<AdsFunnelRow> Funnel { get; set; } = new();
    public List<AdsCampaignRow> Campaigns { get; set; } = new();
    public List<AdsAdRow> Ads { get; set; } = new();
    public List<AdsPersonRow> People { get; set; } = new();
    /// <summary>More people matched than are listed.</summary>
    public int PeopleTotal { get; set; }
    public AdsFilterOptions Filters { get; set; } = new();
}

public class AdsStatusResponse : ApiResult
{
    /// <summary>The three MetaAds settings are present on the server.</summary>
    public bool Configured { get; set; }
    public DateTime? LastSyncAt { get; set; }
    public string? LastError { get; set; }
    /// <summary>Daily ad rows stored from Meta.</summary>
    public int Rows { get; set; }
    public string? FirstDate { get; set; }
    public string? LastDate { get; set; }
    /// <summary>The signed-in person may start a sync.</summary>
    public bool CanSync { get; set; }
    /// <summary>The signed-in person may enter or change the ad account and token (administrators).</summary>
    public bool CanConnect { get; set; }
    /// <summary>A sync is running right now (started by the schedule or by someone pressing Sync now).</summary>
    public bool Syncing { get; set; }
    /// <summary>Minutes between automatic syncs.</summary>
    public int IntervalMinutes { get; set; } = 10;
    /// <summary>When the next automatic sync is due (null when Meta is not connected or the schedule is off).</summary>
    public DateTime? NextSyncAt { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public string? AdAccountId { get; set; }
    public string? AccountName { get; set; }
    /// <summary>app | server | none: where the connection details come from.</summary>
    public string Source { get; set; } = "none";
}

// ---------------------------------------------------------------------------
// Meta connection (ad account and access token entered in the app)
// ---------------------------------------------------------------------------

/// <summary>What is connected. The access token itself is never sent to the browser, only whether one is saved and its last four characters.</summary>
public class MetaConnectionDto
{
    public string AdAccountId { get; set; } = "";
    public string ApiVersion { get; set; } = "v21.0";
    public bool HasToken { get; set; }
    /// <summary>Last four characters of the token, so people can tell which one is saved.</summary>
    public string TokenHint { get; set; } = "";
    /// <summary>app = saved here in the app | server = from the server settings (appsettings / environment) | none</summary>
    public string Source { get; set; } = "none";
    public DateTime? UpdatedAt { get; set; }
    public string? AccountName { get; set; }
}

public class MetaConnectionSaveRequest
{
    /// <summary>The ad account number, with or without "act_".</summary>
    public string AdAccountId { get; set; } = "";
    /// <summary>Leave empty to keep the saved token.</summary>
    public string? AccessToken { get; set; }
    public string? ApiVersion { get; set; }
}

public class MetaConnectionResponse : ApiResult
{
    public MetaConnectionDto Connection { get; set; } = new();
    /// <summary>Set by the connection test: the account Meta reported, e.g. "Nura Herbex Ads (INR)".</summary>
    public string? AccountName { get; set; }
}
