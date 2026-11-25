namespace Nuraherbex.Shared.Models;

// ---------------------------------------------------------------------------
// Multi-Project Configuration Helpers
// ---------------------------------------------------------------------------
public static class ProjectConfig
{
    public const string NeuraHerbex = "NeuraHerbex";
    public const string MidFit = "MidFit";

    public static bool IsMidFit(string? projectName) =>
        string.Equals(projectName?.Trim(), MidFit, StringComparison.OrdinalIgnoreCase);

    public static bool IsNeuraHerbex(string? projectName) =>
        !IsMidFit(projectName);
}

// ---------------------------------------------------------------------------
// MidFit Doctor Consultations & Appointments
// ---------------------------------------------------------------------------
public enum ConsultationType
{
    FreeInitial = 1,
    PaidOnboarding = 2,
    FollowUpCheckin = 3
}

public enum ConsultationStatus
{
    Booked = 1,
    InProgress = 2,
    Completed = 3,
    NoShow = 4,
    Cancelled = 5
}

public class DoctorConsultationDto
{
    public long Id { get; set; }
    public long LeadId { get; set; }
    public string LeadName { get; set; } = "";
    public string LeadPhone { get; set; } = "";
    public string? LeadEmail { get; set; }

    public long? DoctorEmployeeId { get; set; }
    public string? DoctorName { get; set; }
    public long BookedByEmployeeId { get; set; }
    public string? BookedByName { get; set; }

    public ConsultationType Type { get; set; } = ConsultationType.FreeInitial;
    public ConsultationStatus Status { get; set; } = ConsultationStatus.Booked;

    public DateTime ScheduledAtUtc { get; set; }
    public int DurationMinutes { get; set; } = 20;

    public string? MeetingUrl { get; set; }
    public string? DoctorNotes { get; set; }
    public string? RecommendationsJson { get; set; }

    public DateTime? CompletedAtUtc { get; set; }
    public long? CompletedByDoctorId { get; set; }

    // Post-Consultation Sales Follow-up
    public bool SalesFollowUpDone { get; set; }
    public string? SalesFollowUpOutcome { get; set; } // "converted", "not_interested", "join_later"
    public string? SalesFollowUpReason { get; set; }
    public DateTime? SalesFollowUpAtUtc { get; set; }
    public long? SalesFollowUpByEmployeeId { get; set; }
    public string? SalesFollowUpByName { get; set; }

    // Assessment preview for doctor
    public MidFitAssessmentDto? Assessment { get; set; }

    public DateTime CreatedAt { get; set; }
}

public class ConsultationSlotDto
{
    public DateTime SlotTimeUtc { get; set; }
    public string FormattedTime { get; set; } = "";
    public bool IsAvailable { get; set; } = true;
    public long? BookedConsultationId { get; set; }
}

public class BookConsultationRequest
{
    public long LeadId { get; set; }
    public long? DoctorEmployeeId { get; set; }
    public DateTime ScheduledAtUtc { get; set; }
    public int DurationMinutes { get; set; } = 20;
    public ConsultationType Type { get; set; } = ConsultationType.FreeInitial;
    public string? Notes { get; set; }
}

public class CompleteConsultationRequest
{
    public string DoctorNotes { get; set; } = "";
    public string? RecommendationsJson { get; set; }
}

public class SalesFollowUpRequest
{
    /// <summary>"converted" | "not_interested" | "join_later"</summary>
    public string Outcome { get; set; } = "converted";
    public string? Reason { get; set; }
    public DateTime? NextFollowUpAtUtc { get; set; }
    public string? Notes { get; set; }
    public decimal? MembershipAmount { get; set; }
}

public class ConsultationListResponse : ApiResult
{
    public List<DoctorConsultationDto> Consultations { get; set; } = new();
    public int TotalCount { get; set; }
}

public class ConsultationSlotsResponse : ApiResult
{
    public DateTime Date { get; set; }
    public List<ConsultationSlotDto> Slots { get; set; } = new();
}

public class MidFitAssessmentDto
{
    public int? Age { get; set; }
    public string? Gender { get; set; }
    public decimal? HeightCm { get; set; }
    public decimal? WeightKg { get; set; }
    public decimal? TargetWeightKg { get; set; }
    public List<string> Goals { get; set; } = new();
    public string? DietPreference { get; set; }
    public string? ActivityLevel { get; set; }
    public string? MedicalConditions { get; set; }
    public string? PastInjuries { get; set; }
    public string? Notes { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
