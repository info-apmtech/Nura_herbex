namespace Nuraherbex.Shared.Models;

// ---------------------------------------------------------------------------
// Follow-ups (tasks), journey plan, notifications and reports
// ---------------------------------------------------------------------------
public static class FollowUpTypes
{
    public const string Call = "call";
    public const string WhatsApp = "whatsapp";
    public const string Review = "review";
    public const string Reorder = "reorder";
    public const string Order = "order";
    public const string Other = "other";
    public static readonly string[] All = [Call, WhatsApp, Review, Reorder, Order, Other];
}

/// <summary>What happened when a follow-up was completed. Some outcomes also update the lead.</summary>
public static class FollowUpOutcomes
{
    public const string Done = "done";
    public const string Interested = "interested";
    public const string NotInterested = "not-interested";
    public const string Converted = "converted";
    public const string NoAnswer = "no-answer";
    public const string Callback = "callback";
    public static readonly string[] All = [Done, Interested, NotInterested, Converted, NoAnswer, Callback];
}

public class FollowUpDto
{
    public long Id { get; set; }
    public long LeadId { get; set; }
    public string LeadName { get; set; } = "";
    public string LeadPhone { get; set; } = "";
    public string? OrderId { get; set; }
    public long? AssignedToEmployeeId { get; set; }
    public string? AssignedToName { get; set; }
    public string Type { get; set; } = FollowUpTypes.Call;
    public string Title { get; set; } = "";
    public string? Notes { get; set; }
    public string? SuggestedMessage { get; set; }
    public DateTime DueAt { get; set; }
    /// <summary>pending | done | skipped</summary>
    public string Status { get; set; } = "pending";
    public bool IsOverdue { get; set; }
    public string? Outcome { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? CompletedBy { get; set; }
    /// <summary>manual | journey | system</summary>
    public string Source { get; set; } = "manual";
    public string? StepKey { get; set; }
    public DateTime? RemindAt { get; set; }
    /// <summary>The task carries a WhatsApp message that can be sent from the app.</summary>
    public bool CanMessage { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class FollowUpCounts
{
    public int Overdue { get; set; }
    public int Today { get; set; }
    public int Upcoming { get; set; }
    public int Done { get; set; }
}

public class FollowUpListResponse : ApiResult
{
    public List<FollowUpDto> Items { get; set; } = new();
    public FollowUpCounts Counts { get; set; } = new();
}

public class FollowUpCreateRequest
{
    public long LeadId { get; set; }
    public string Type { get; set; } = FollowUpTypes.Call;
    public string? Title { get; set; }
    public string? Notes { get; set; }
    public DateTime DueAt { get; set; }
    /// <summary>Admin only: who should do it. Defaults to the lead owner.</summary>
    public long? AssignedToEmployeeId { get; set; }
}

public class FollowUpCompleteRequest
{
    public string Outcome { get; set; } = FollowUpOutcomes.Done;
    public string? Note { get; set; }
    /// <summary>Required for "callback"; optional otherwise (a "no-answer" defaults to tomorrow).</summary>
    public DateTime? NextDueAt { get; set; }
}

public class FollowUpRescheduleRequest
{
    public DateTime DueAt { get; set; }
    public long? AssignedToEmployeeId { get; set; }
}

public class FollowUpSettingsDto : ApiResult
{
    /// <summary>Start the 15-day journey for every delivered order.</summary>
    public bool JourneyEnabled { get; set; } = true;
    /// <summary>Send journey messages by WhatsApp template without waiting for an employee (needs approved templates and customer opt-in).</summary>
    public bool AutoSend { get; set; }
    public int ReminderDays { get; set; } = 2;
    public List<JourneyStepDto> Steps { get; set; } = new();
}

public class FollowUpSettingsRequest
{
    public bool JourneyEnabled { get; set; } = true;
    public bool AutoSend { get; set; }
    public int ReminderDays { get; set; } = 2;
}

public class JourneyStepDto
{
    public string Key { get; set; } = "";
    public int Day { get; set; }
    public string Type { get; set; } = FollowUpTypes.WhatsApp;
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    /// <summary>True when a WhatsApp template is configured for automatic sending.</summary>
    public bool HasTemplate { get; set; }
}

public class NotificationDto
{
    public long Id { get; set; }
    public string Kind { get; set; } = "info";
    public string Title { get; set; } = "";
    public string? Body { get; set; }
    public string? Link { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsRead { get; set; }
}

public class NotificationListResponse : ApiResult
{
    public List<NotificationDto> Items { get; set; } = new();
    public int Unread { get; set; }
}

// ---- Reports ----

public class FollowUpReportResponse : ApiResult
{
    public int Days { get; set; }
    public DateTime From { get; set; }
    public int Due { get; set; }
    public int Done { get; set; }
    public int DoneOnTime { get; set; }
    public int Overdue { get; set; }
    public int Pending { get; set; }
    /// <summary>Done divided by due, 0-100.</summary>
    public int CompletionRate { get; set; }
    public int OnTimeRate { get; set; }
    public int AutoSent { get; set; }
    public int NewLeads { get; set; }
    public int ConvertedLeads { get; set; }
    public int ReviewRequests { get; set; }
    public int ReviewsReceived { get; set; }
    public int JourneysStarted { get; set; }
    public int Reorders { get; set; }
    public List<FollowUpReportRow> ByEmployee { get; set; } = new();
    public List<FollowUpReportRow> ByType { get; set; } = new();
    public List<JourneyReportRow> Journey { get; set; } = new();
    public List<FollowUpTrendPoint> Trend { get; set; } = new();
}

public class FollowUpReportRow
{
    public string Label { get; set; } = "";
    public long? EmployeeId { get; set; }
    public int Due { get; set; }
    public int Done { get; set; }
    public int Overdue { get; set; }
    public int OnTimePercent { get; set; }
    public int OpenLeads { get; set; }
    public int ConvertedLeads { get; set; }
}

public class JourneyReportRow
{
    public string StepKey { get; set; } = "";
    public string Title { get; set; } = "";
    public int Day { get; set; }
    public int Total { get; set; }
    public int Done { get; set; }
    public int AutoSent { get; set; }
    public int Pending { get; set; }
    public int Overdue { get; set; }
    public int Skipped { get; set; }
}

public class FollowUpTrendPoint
{
    public string Day { get; set; } = "";
    public int Due { get; set; }
    public int Done { get; set; }
}
