namespace Nuraherbex.Shared.Models;

// ---------------------------------------------------------------------------
// CRM leads (admin view)
// ---------------------------------------------------------------------------
public static class LeadStatuses
{
    public const string New = "new";
    public const string Interested = "interested";
    public const string FollowUp = "follow-up";
    public const string Converted = "converted";
    public const string NotInterested = "not-interested";
    public static readonly string[] All = [New, Interested, FollowUp, Converted, NotInterested];
}

public class LeadDto
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Phone { get; set; } = "";
    public string? Email { get; set; }
    public string Source { get; set; } = "whatsapp";
    public string? SourceDetail { get; set; }
    public string Status { get; set; } = LeadStatuses.New;
    /// <summary>unknown | opted_in | opted_out</summary>
    public string ConsentStatus { get; set; } = "unknown";
    public string? FirstMessage { get; set; }
    public string? Notes { get; set; }
    public string? CustomerId { get; set; }
    public int UnreadCount { get; set; }
    public DateTime? ServiceWindowExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastContactAt { get; set; }
    public DateTime? ConvertedAt { get; set; }
}

public class LeadStatusHistoryDto
{
    public string? FromStatus { get; set; }
    public string ToStatus { get; set; } = "";
    public string? Note { get; set; }
    public string ChangedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class LeadListResponse : ApiResult
{
    public List<LeadDto> Leads { get; set; } = new();
    public int Total { get; set; }
    /// <summary>Lead count per status (ignores the status filter, honours search/source).</summary>
    public Dictionary<string, int> StatusCounts { get; set; } = new();
}

public class LeadDetailResponse : ApiResult
{
    public LeadDto? Lead { get; set; }
    public List<WhatsAppMessageDto> Messages { get; set; } = new();
    public List<LeadStatusHistoryDto> History { get; set; } = new();
}

public class LeadStatusRequest
{
    public string Status { get; set; } = LeadStatuses.New;
    public string? Note { get; set; }
}

public class LeadNotesRequest
{
    public string? Notes { get; set; }
}
