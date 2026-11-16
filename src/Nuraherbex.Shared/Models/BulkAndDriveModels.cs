namespace Nuraherbex.Shared.Models;

// ---------------------------------------------------------------------------
// Bulk Messaging (4 audience categories) & Google Drive Integration
// ---------------------------------------------------------------------------

public static class BulkCategories
{
    public const string Customers = "customers";
    public const string NoAnswer = "no-answer";
    public const string HotLeads = "hot-leads";
    public const string AllLeads = "all-leads";
    public static readonly string[] All = [Customers, NoAnswer, HotLeads, AllLeads];

    public static string Label(string? c) => c switch
    {
        Customers => "Customers",
        NoAnswer => "No Answer",
        HotLeads => "Hot Leads",
        AllLeads => "All Leads",
        _ => c ?? "",
    };
}

public class BulkPreviewResponse : ApiResult
{
    public string Category { get; set; } = BulkCategories.AllLeads;
    public int TotalMatched { get; set; }
    public int OptedOutExcluded { get; set; }
    public int EligibleCount { get; set; }
    public int InsideWindowCount { get; set; }
    public int OutsideWindowCount { get; set; }
    public List<BulkAudienceSampleDto> Sample { get; set; } = new();
}

public class BulkAudienceSampleDto
{
    public long LeadId { get; set; }
    public string Name { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Status { get; set; } = "";
    public bool InsideWindow { get; set; }
}

public class BulkSendRequest
{
    public string Category { get; set; } = BulkCategories.AllLeads;
    public string? Title { get; set; }
    /// <summary>Optional Meta-approved template name for outside-24h recipients (and optionally everyone when SendMode == "template").</summary>
    public string? TemplateName { get; set; }
    public List<string> TemplateParams { get; set; } = new();
    /// <summary>Free-form WhatsApp text used for recipients inside the 24-hour customer service window.</summary>
    public string? CustomMessage { get; set; }
    /// <summary>Optional Google Drive share link appended to custom messages or mapped into template params.</summary>
    public string? DriveFileUrl { get; set; }
    public string? DriveFileName { get; set; }
}

public class BulkCampaignDto
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string Category { get; set; } = BulkCategories.AllLeads;
    public string? TemplateName { get; set; }
    public string? CustomMessage { get; set; }
    public string? DriveFileUrl { get; set; }
    public string? DriveFileName { get; set; }
    /// <summary>queued | running | completed | stopped | failed</summary>
    public string Status { get; set; } = "queued";
    public int TotalTarget { get; set; }
    public int SentCount { get; set; }
    public int FailedCount { get; set; }
    public int SkippedCount { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public class BulkCampaignListResponse : ApiResult
{
    public List<BulkCampaignDto> Campaigns { get; set; } = new();
}

public class BulkCampaignRecipientDto
{
    public long Id { get; set; }
    public long LeadId { get; set; }
    public string Name { get; set; } = "";
    public string Phone { get; set; } = "";
    /// <summary>pending | sent | failed | skipped</summary>
    public string Status { get; set; } = "pending";
    public string Mode { get; set; } = "text";
    public string? Error { get; set; }
    public DateTime? SentAt { get; set; }
}

public class BulkCampaignDetailResponse : ApiResult
{
    public BulkCampaignDto? Campaign { get; set; }
    public List<BulkCampaignRecipientDto> Recipients { get; set; } = new();
}

public class DriveFileDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string MimeType { get; set; } = "";
    public string Kind { get; set; } = LibraryKinds.Document;
    public long Size { get; set; }
    public string? WebViewLink { get; set; }
    public string? WebContentLink { get; set; }
    public DateTime? ModifiedTime { get; set; }
}

public class DriveListResponse : ApiResult
{
    public bool Configured { get; set; }
    public string? RootFolderId { get; set; }
    public List<DriveFileDto> Files { get; set; } = new();
}

public class DriveSettingsDto : ApiResult
{
    public bool Configured { get; set; }
    public string? ServiceAccountEmail { get; set; }
    public bool HasPrivateKey { get; set; }
    public string? RootFolderId { get; set; }
    public bool MakePublicOnShare { get; set; } = true;
}

public class DriveSettingsSaveRequest
{
    /// <summary>Optional full Google Service Account JSON paste (extracts client_email and private_key automatically).</summary>
    public string? ServiceAccountJson { get; set; }
    public string? ServiceAccountEmail { get; set; }
    public string? PrivateKeyPem { get; set; }
    public string? RootFolderId { get; set; }
    public bool MakePublicOnShare { get; set; } = true;
}

public class DriveShareRequest
{
    public string FileId { get; set; } = "";
    public string? FileName { get; set; }
    public string? Caption { get; set; }
}

public class DriveShareResponse : ApiResult
{
    public string? ShareUrl { get; set; }
    public DriveFileDto? File { get; set; }
}

