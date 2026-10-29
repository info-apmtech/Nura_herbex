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
    /// <summary>Preview of the most recent message in the conversation (list view).</summary>
    public string? LastMessage { get; set; }
    public string? LastMessageDirection { get; set; }
    public DateTime? ServiceWindowExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastContactAt { get; set; }
    public DateTime? ConvertedAt { get; set; }
    public long? AssignedToEmployeeId { get; set; }
    public string? AssignedToName { get; set; }
    public DateTime? AssignedAt { get; set; }
    /// <summary>Soonest pending follow-up, if any.</summary>
    public DateTime? NextFollowUpAt { get; set; }
    /// <summary>Orders placed by this customer (matched by phone) and what they are worth.</summary>
    public int OrderCount { get; set; }
    public decimal LifetimeValue { get; set; }
    public DateTime? LastOrderAt { get; set; }
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
    /// <summary>Leads nobody owns yet (admin view).</summary>
    public int UnassignedCount { get; set; }
    /// <summary>Lead count per status (ignores the status filter, honours search/source).</summary>
    public Dictionary<string, int> StatusCounts { get; set; } = new();
}

public class LeadDetailResponse : ApiResult
{
    public LeadDto? Lead { get; set; }
    public List<WhatsAppMessageDto> Messages { get; set; } = new();
    public List<LeadStatusHistoryDto> History { get; set; } = new();
    public List<LeadAssignmentDto> Assignments { get; set; } = new();
    public List<FollowUpDto> FollowUps { get; set; } = new();
    public List<LeadOrderDto> Orders { get; set; } = new();
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

public class LeadAssignmentDto
{
    public string? FromName { get; set; }
    public string? ToName { get; set; }
    public string? Reason { get; set; }
    public string ChangedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class LeadAssignRequest
{
    /// <summary>Employee to own the lead; null unassigns it (admin only).</summary>
    public long? EmployeeId { get; set; }
    public string? Reason { get; set; }
}

public class LeadReplyRequest
{
    public string Text { get; set; } = "";
}

// ---------------------------------------------------------------------------
// Employees (CRM users)
// ---------------------------------------------------------------------------
public static class EmployeeRoles
{
    public const string Admin = "admin";
    public const string Sales = "sales";
}

public class EmployeeDto
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    /// <summary>admin | sales</summary>
    public string Role { get; set; } = EmployeeRoles.Sales;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
    /// <summary>Assigned leads that are not yet converted / closed.</summary>
    public int OpenLeads { get; set; }
    public int TotalLeads { get; set; }
    public long? DepartmentId { get; set; }
    public string? DepartmentName { get; set; }
    public long? DesignationId { get; set; }
    public string? DesignationName { get; set; }
    public int OpenTasks { get; set; }
    /// <summary>Signed in to the portal right now (checked in within the last two minutes).</summary>
    public bool IsOnline { get; set; }
    /// <summary>The person switched "WhatsApp active" on, so new WhatsApp leads can be assigned to them.</summary>
    public bool WhatsAppActive { get; set; }
}

public class EmployeeSaveRequest
{
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Role { get; set; } = EmployeeRoles.Sales;
    public bool IsActive { get; set; } = true;
    /// <summary>Required when creating; when editing, leave empty to keep the current password.</summary>
    public string? Password { get; set; }
    public long? DepartmentId { get; set; }
    public long? DesignationId { get; set; }
}

public class EmployeeListResponse : ApiResult
{
    public List<EmployeeDto> Employees { get; set; } = new();
    /// <summary>New leads are assigned round-robin to active sales employees.</summary>
    public bool AutoAssign { get; set; }
}

public class EmployeeResponse : ApiResult
{
    public EmployeeDto? Employee { get; set; }
}

public class EmployeeRemoveRequest
{
    /// <summary>auto = spread their open leads across the other active sales team | unassigned = leave them in the admin queue</summary>
    public string Reassign { get; set; } = "auto";
}

public class LeadSettingsRequest
{
    public bool AutoAssign { get; set; }
}

public class LeadConsentRequest
{
    /// <summary>opted_in | unknown</summary>
    public string Status { get; set; } = "opted_in";
}

/// <summary>The automatic reply a new WhatsApp message gets when nobody on the team is active (Sundays, holidays, nights).</summary>
public class AfterHoursSettings
{
    public const string DefaultMessage = "Hello {name}, thank you for contacting Nura Herbex 🌿\nOur team is away right now (Sunday, holiday or outside working hours). We have received your message and will reply as soon as we are back.\nIn the meantime, send *track* or your order ID (e.g. NH-123456) to get your order status.";
    public bool Enabled { get; set; } = true;
    public string Message { get; set; } = DefaultMessage;
}

public class AfterHoursResponse : ApiResult
{
    public AfterHoursSettings Settings { get; set; } = new();
}
