using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nuraherbex.Api.Services;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Controllers;

[ApiController]
[Route("api/admin/consultations")]
[Authorize(Roles = "admin,sales")]
public class ConsultationController(ConsultationService consultations, RightsService rights) : StaffControllerBase(rights)
{
    /// <summary>Returns available 20-min slots for a given date (IST).</summary>
    [HttpGet("slots")]
    public async Task<IActionResult> GetSlots([FromQuery] string? date, [FromQuery] long? doctorId)
    {
        var targetDate = DateTime.TryParse(date, out var d) ? d.Date : FollowUpService.IstNow.Date;
        var res = await consultations.GetSlotsAsync(targetDate, doctorId);
        return Ok(res);
    }

    /// <summary>Books a consultation slot for a lead.</summary>
    [HttpPost("book")]
    public async Task<IActionResult> Book([FromBody] BookConsultationRequest req)
    {
        var bookedBy = MyId ?? 0;
        var res = await consultations.BookAsync(req, bookedBy, MyName);
        return res.Success ? Ok(res) : BadRequest(res);
    }

    /// <summary>Returns booked consultations for the Doctor Consultation Portal.</summary>
    [HttpGet("doctor")]
    public async Task<IActionResult> DoctorList([FromQuery] string? date, [FromQuery] string? status)
    {
        DateTime? targetDate = DateTime.TryParse(date, out var d) ? d.Date : null;
        var doctorId = IsAdmin || CanAll("doctor_portal") ? null : MyId;
        var res = await consultations.GetDoctorListAsync(doctorId, targetDate, status);
        return Ok(res);
    }

    /// <summary>Doctor attaches consultation notes and marks done.</summary>
    [HttpPut("{id}/complete")]
    public async Task<IActionResult> Complete(long id, [FromBody] CompleteConsultationRequest req)
    {
        var doctorId = MyId ?? 0;
        var res = await consultations.CompleteAsync(id, req, doctorId, MyName);
        return res.Success ? Ok(res) : BadRequest(res);
    }

    /// <summary>Returns leads that have completed doctor consultation awaiting sales follow-up.</summary>
    [HttpGet("completed-from-doctor")]
    public async Task<IActionResult> CompletedFromDoctor([FromQuery] string? q)
    {
        var salesId = IsAdmin || CanAll("doctor_completed") ? null : MyId;
        var res = await consultations.GetCompletedFromDoctorAsync(salesId, q);
        return Ok(res);
    }

    /// <summary>Salesperson records outcome: converted | not_interested | join_later.</summary>
    [HttpPost("{id}/sales-followup")]
    public async Task<IActionResult> SalesFollowUp(long id, [FromBody] SalesFollowUpRequest req)
    {
        var salesId = MyId ?? 0;
        var res = await consultations.ProcessSalesFollowUpAsync(id, req, salesId, MyName);
        return res.Success ? Ok(res) : BadRequest(res);
    }
}
