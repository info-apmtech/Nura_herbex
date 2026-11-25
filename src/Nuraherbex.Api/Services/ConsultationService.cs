using Microsoft.EntityFrameworkCore;
using Nuraherbex.Api.Data;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Services;

public class ConsultationService(NuraDbContext db, ILogger<ConsultationService> log)
{
    private const int DefaultStartHour = 10;
    private const int DefaultEndHour = 19;
    private const int DefaultSlotMinutes = 20;

    /// <summary>Generates 20-minute slots between 10:00 AM and 7:00 PM IST for a given date, checking existing bookings.</summary>
    public async Task<ConsultationSlotsResponse> GetSlotsAsync(DateTime dateIst, long? doctorId = null)
    {
        var dayDate = dateIst.Date;
        var startIst = dayDate.AddHours(DefaultStartHour);
        var endIst = dayDate.AddHours(DefaultEndHour);

        var startUtc = FollowUpService.ToUtc(startIst);
        var endUtc = FollowUpService.ToUtc(endIst);

        var query = db.DoctorConsultations.AsNoTracking()
            .Where(c => c.ScheduledAtUtc >= startUtc && c.ScheduledAtUtc < endUtc && c.Status != ConsultationStatus.Cancelled);

        if (doctorId.HasValue && doctorId > 0)
            query = query.Where(c => c.DoctorEmployeeId == doctorId.Value || c.DoctorEmployeeId == null);

        var booked = await query.ToListAsync();

        var slots = new List<ConsultationSlotDto>();
        var currentIst = startIst;

        while (currentIst < endIst)
        {
            var nextIst = currentIst.AddMinutes(DefaultSlotMinutes);
            var slotUtc = FollowUpService.ToUtc(currentIst);

            var existing = booked.FirstOrDefault(b => Math.Abs((b.ScheduledAtUtc - slotUtc).TotalMinutes) < 15);

            slots.Add(new ConsultationSlotDto
            {
                SlotTimeUtc = slotUtc,
                FormattedTime = $"{currentIst:hh:mm tt} - {nextIst:hh:mm tt}",
                IsAvailable = existing == null,
                BookedConsultationId = existing?.Id
            });

            currentIst = nextIst;
        }

        return new ConsultationSlotsResponse
        {
            Success = true,
            Date = dayDate,
            Slots = slots
        };
    }

    /// <summary>Books a 20-min consultation slot for a lead.</summary>
    public async Task<ApiResult> BookAsync(BookConsultationRequest req, long bookedByEmployeeId, string actorName = "Staff")
    {
        var lead = await db.Leads.FirstOrDefaultAsync(l => l.Id == req.LeadId);
        if (lead is null) return new ApiResult { Success = false, Message = "Lead not found." };

        // Ensure slot is not already booked for same doctor
        var slotEndUtc = req.ScheduledAtUtc.AddMinutes(req.DurationMinutes);
        var conflict = await db.DoctorConsultations.AnyAsync(c =>
            c.Status != ConsultationStatus.Cancelled &&
            Math.Abs(EF.Functions.DateDiffMinute(c.ScheduledAtUtc, req.ScheduledAtUtc)) < req.DurationMinutes &&
            (req.DoctorEmployeeId == null || c.DoctorEmployeeId == req.DoctorEmployeeId));

        if (conflict)
            return new ApiResult { Success = false, Message = "This slot is already booked. Please choose another slot." };

        var consultation = new DoctorConsultation
        {
            LeadId = lead.Id,
            DoctorEmployeeId = req.DoctorEmployeeId,
            BookedByEmployeeId = bookedByEmployeeId,
            Type = req.Type,
            Status = ConsultationStatus.Booked,
            ScheduledAtUtc = req.ScheduledAtUtc,
            DurationMinutes = req.DurationMinutes,
            DoctorNotes = req.Notes,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.DoctorConsultations.Add(consultation);

        // Update lead tracking
        lead.ActiveConsultationId = consultation.Id;
        lead.ConsultationStatus = "booked";
        lead.ConsultationScheduledAt = req.ScheduledAtUtc;
        lead.Status = LeadStatus.Interested;
        lead.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync();

        log.LogInformation("[Consultation] Lead {LeadId} booked for {ScheduledAt} by {Actor}", lead.Id, req.ScheduledAtUtc, actorName);
        return new ApiResult { Success = true, Message = $"Consultation booked successfully for {FollowUpService.ToIst(req.ScheduledAtUtc):d MMM yyyy, hh:mm tt}." };
    }

    /// <summary>Doctor attaches notes and marks the consultation completed.</summary>
    public async Task<ApiResult> CompleteAsync(long consultationId, CompleteConsultationRequest req, long doctorEmployeeId, string doctorName = "Doctor")
    {
        var consultation = await db.DoctorConsultations.FirstOrDefaultAsync(c => c.Id == consultationId);
        if (consultation is null) return new ApiResult { Success = false, Message = "Consultation record not found." };

        consultation.DoctorNotes = req.DoctorNotes.Trim();
        consultation.RecommendationsJson = req.RecommendationsJson;
        consultation.Status = ConsultationStatus.Completed;
        consultation.CompletedAtUtc = DateTime.UtcNow;
        consultation.CompletedByDoctorId = doctorEmployeeId;
        consultation.UpdatedAt = DateTime.UtcNow;

        var lead = await db.Leads.FirstOrDefaultAsync(l => l.Id == consultation.LeadId);
        if (lead is not null)
        {
            lead.ConsultationStatus = "completed_from_doctor";
            lead.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();

        log.LogInformation("[Consultation] Consultation {Id} completed by Doctor {Doctor}", consultationId, doctorName);
        return new ApiResult { Success = true, Message = "Consultation marked as completed. Lead is now moved to 'Completed from Doctor' queue." };
    }

    /// <summary>Returns list of consultations for Doctor Portal view.</summary>
    public async Task<ConsultationListResponse> GetDoctorListAsync(long? doctorEmployeeId, DateTime? date = null, string? status = null)
    {
        var query = db.DoctorConsultations.AsNoTracking().AsQueryable();

        if (doctorEmployeeId.HasValue && doctorEmployeeId > 0)
            query = query.Where(c => c.DoctorEmployeeId == doctorEmployeeId.Value || c.DoctorEmployeeId == null);

        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<ConsultationStatus>(status, true, out var stEnum))
            query = query.Where(c => c.Status == stEnum);

        if (date.HasValue)
        {
            var dayStartUtc = FollowUpService.ToUtc(date.Value.Date);
            var dayEndUtc = FollowUpService.ToUtc(date.Value.Date.AddDays(1));
            query = query.Where(c => c.ScheduledAtUtc >= dayStartUtc && c.ScheduledAtUtc < dayEndUtc);
        }

        var list = await query.OrderByDescending(c => c.ScheduledAtUtc).Take(150).ToListAsync();
        var leadIds = list.Select(c => c.LeadId).Distinct().ToList();
        var leads = await db.Leads.AsNoTracking().Where(l => leadIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id);
        var staff = await db.Employees.AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.Name);

        var dtos = list.Select(c =>
        {
            var lead = leads.GetValueOrDefault(c.LeadId);
            return new DoctorConsultationDto
            {
                Id = c.Id,
                LeadId = c.LeadId,
                LeadName = lead?.Name ?? "Lead",
                LeadPhone = lead?.Phone ?? "",
                LeadEmail = lead?.Email,
                DoctorEmployeeId = c.DoctorEmployeeId,
                DoctorName = c.DoctorEmployeeId.HasValue ? staff.GetValueOrDefault(c.DoctorEmployeeId.Value) : "Any Doctor",
                BookedByEmployeeId = c.BookedByEmployeeId,
                BookedByName = staff.GetValueOrDefault(c.BookedByEmployeeId) ?? "Staff",
                Type = c.Type,
                Status = c.Status,
                ScheduledAtUtc = c.ScheduledAtUtc,
                DurationMinutes = c.DurationMinutes,
                MeetingUrl = c.MeetingUrl,
                DoctorNotes = c.DoctorNotes,
                CompletedAtUtc = c.CompletedAtUtc,
                Assessment = lead == null ? null : new MidFitAssessmentDto
                {
                    Age = lead.Age,
                    Gender = lead.Gender,
                    HeightCm = lead.HeightCm,
                    WeightKg = lead.WeightKg,
                    TargetWeightKg = lead.TargetWeightKg,
                    Goals = lead.FitnessGoals ?? new(),
                    DietPreference = lead.DietPreference,
                    ActivityLevel = lead.ActivityLevel,
                    Notes = lead.AssessmentNotes
                },
                CreatedAt = c.CreatedAt
            };
        }).ToList();

        return new ConsultationListResponse { Success = true, Consultations = dtos, TotalCount = dtos.Count };
    }

    /// <summary>Returns list of consultations completed by doctor awaiting post-consultation sales follow-up.</summary>
    public async Task<ConsultationListResponse> GetCompletedFromDoctorAsync(long? salesEmployeeId, string? q = null)
    {
        var query = db.DoctorConsultations.AsNoTracking()
            .Where(c => c.Status == ConsultationStatus.Completed && !c.SalesFollowUpDone);

        var list = await query.OrderByDescending(c => c.CompletedAtUtc).Take(150).ToListAsync();
        var leadIds = list.Select(c => c.LeadId).Distinct().ToList();
        var leads = await db.Leads.AsNoTracking().Where(l => leadIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id);
        var staff = await db.Employees.AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.Name);

        var dtos = list.Select(c =>
        {
            var lead = leads.GetValueOrDefault(c.LeadId);
            return new DoctorConsultationDto
            {
                Id = c.Id,
                LeadId = c.LeadId,
                LeadName = lead?.Name ?? "Lead",
                LeadPhone = lead?.Phone ?? "",
                LeadEmail = lead?.Email,
                DoctorEmployeeId = c.DoctorEmployeeId,
                DoctorName = c.DoctorEmployeeId.HasValue ? staff.GetValueOrDefault(c.DoctorEmployeeId.Value) : "Doctor",
                Type = c.Type,
                Status = c.Status,
                ScheduledAtUtc = c.ScheduledAtUtc,
                DoctorNotes = c.DoctorNotes,
                CompletedAtUtc = c.CompletedAtUtc,
                Assessment = lead == null ? null : new MidFitAssessmentDto
                {
                    Age = lead.Age,
                    Gender = lead.Gender,
                    HeightCm = lead.HeightCm,
                    WeightKg = lead.WeightKg,
                    TargetWeightKg = lead.TargetWeightKg,
                    Goals = lead.FitnessGoals ?? new(),
                    DietPreference = lead.DietPreference,
                    ActivityLevel = lead.ActivityLevel,
                    Notes = lead.AssessmentNotes
                },
                CreatedAt = c.CreatedAt
            };
        })
        .Where(d => string.IsNullOrWhiteSpace(q) || d.LeadName.Contains(q, StringComparison.OrdinalIgnoreCase) || d.LeadPhone.Contains(q))
        .ToList();

        return new ConsultationListResponse { Success = true, Consultations = dtos, TotalCount = dtos.Count };
    }

    /// <summary>Handles post-consultation sales follow-up branching: Converted (Paid) | Not Interested | Join Later.</summary>
    public async Task<ApiResult> ProcessSalesFollowUpAsync(long consultationId, SalesFollowUpRequest req, long salesEmployeeId, string salesName = "Salesperson")
    {
        var consultation = await db.DoctorConsultations.FirstOrDefaultAsync(c => c.Id == consultationId);
        if (consultation is null) return new ApiResult { Success = false, Message = "Consultation not found." };

        consultation.SalesFollowUpDone = true;
        consultation.SalesFollowUpOutcome = req.Outcome.ToLowerInvariant();
        consultation.SalesFollowUpReason = req.Reason;
        consultation.SalesFollowUpAtUtc = DateTime.UtcNow;
        consultation.SalesFollowUpByEmployeeId = salesEmployeeId;

        var lead = await db.Leads.FirstOrDefaultAsync(l => l.Id == consultation.LeadId);
        if (lead is not null)
        {
            var outcome = req.Outcome.ToLowerInvariant();
            if (outcome == "converted")
            {
                lead.Status = LeadStatus.Converted;
                lead.ConvertedAt = DateTime.UtcNow;
                lead.ConvertedByEmployeeId = salesEmployeeId;
                lead.ConsultationStatus = "converted_paid";
            }
            else if (outcome == "not_interested")
            {
                lead.Status = LeadStatus.NotInterested;
                lead.ConsultationStatus = "closed_not_interested";
                if (!string.IsNullOrWhiteSpace(req.Reason))
                    lead.Notes = (lead.Notes + $" | Not interested: {req.Reason}").TrimStart('|', ' ');
            }
            else if (outcome == "join_later")
            {
                lead.Status = LeadStatus.FollowUp;
                lead.ConsultationStatus = "join_later";
                var dueAt = req.NextFollowUpAtUtc ?? DateTime.UtcNow.AddDays(7);
                db.FollowUps.Add(new FollowUp
                {
                    LeadId = lead.Id,
                    AssignedToEmployeeId = salesEmployeeId,
                    Type = "call",
                    Title = "MidFit Post-Consultation Join Later Follow-up",
                    Notes = req.Notes ?? req.Reason ?? "Customer requested to join later after doctor consultation.",
                    DueAt = dueAt,
                    Status = FollowUpStatus.Pending,
                    CreatedAt = DateTime.UtcNow
                });
            }
            lead.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();

        log.LogInformation("[Consultation] Post-consultation sales follow-up on #{Id} set to {Outcome} by {Sales}", consultationId, req.Outcome, salesName);
        return new ApiResult { Success = true, Message = $"Sales follow-up saved as '{req.Outcome}'." };
    }
}
