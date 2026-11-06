namespace Nuraherbex.Api.Options;

/// <summary>
/// Optional link to the Meta Marketing API. With it, the Ad campaigns report shows campaign, ad set and ad names plus spend and reach.
/// Without it the report still works from the ad details WhatsApp attaches to each lead's first message.
/// </summary>
public class MetaAdsOptions
{
    public const string Section = "MetaAds";
    /// <summary>The ad account, with or without the "act_" prefix.</summary>
    public string AdAccountId { get; set; } = "";
    /// <summary>A system-user access token with the ads_read permission.</summary>
    public string AccessToken { get; set; } = "";
    public string ApiVersion { get; set; } = "v21.0";
    /// <summary>How many days back each sync reads (Meta restates recent days, so they are refreshed every time).</summary>
    public int SyncDays { get; set; } = 60;
    /// <summary>Set to false on any other process that shares the database, so only one server syncs.</summary>
    public bool WorkerEnabled { get; set; } = true;
    /// <summary>Minutes between automatic syncs (5 to 360).</summary>
    public int SyncMinutes { get; set; } = 10;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(AdAccountId) && !string.IsNullOrWhiteSpace(AccessToken);
    public string AccountPath => "act_" + AdAccountId.Trim().Replace("act_", "", StringComparison.OrdinalIgnoreCase);
}
