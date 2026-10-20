using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Nuraherbex.Api.Data;
using Nuraherbex.Api.Services;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Controllers;

/// <summary>
/// CRM endpoints for employees. Both roles may sign in: "admin" sees every lead and manages the team,
/// "sales" only sees (and replies to) leads assigned to them. Admin-only actions add their own role attribute
/// (attributes combine, so those require "admin").
/// </summary>
[ApiController]
[Route("api/admin")]
[Authorize(Roles = "admin,sales")]
public class StaffController(NuraDbContext db, LeadService leads, WhatsAppService whatsapp, OrderService orders, MediaSigner signer, RightsService rightsService, ILogger<StaffController> log) : StaffControllerBase(rightsService)
{
    private static readonly string[] Roles = [EmployeeRoles.Admin, EmployeeRoles.Sales];
    // ---- Session ---------------------------------------------------------------------

    [HttpGet("verify")]
    public IActionResult Verify() => Ok(new AdminLoginResponse
    {
        Success = true, Valid = true,
        User = new AdminUserDto
        {
            Email = User.FindFirstValue(ClaimTypes.Email) ?? "", Role = IsAdmin ? "admin" : "sales", Name = MyId is null ? "Administrator" : MyName, EmployeeId = MyId,
            Rights = new Dictionary<string, int>(Rights.Levels), Department = Rights.Department, Designation = Rights.Designation,
        },
    });

    /// <summary>
    /// People who can be picked as an owner: "leads" or "followups" lists staff who may work those pages, "tasks" lists everyone active.
    /// Only names are returned, so any signed-in employee may call it.
    /// </summary>
    [HttpGet("team")]
    public async Task<IActionResult> Team([FromQuery(Name = "for")] string? forPage = null)
    {
        var q = db.Employees.AsNoTracking().Where(e => e.IsActive);
        List<long>? allowed = null;
        if (forPage is "leads" or "followups") allowed = await rightsService.EmployeesWithPageAsync(forPage);
        else if (forPage is null) allowed = await rightsService.EmployeesWithPageAsync("leads"); // the original meaning: people who can own leads
        if (allowed is not null) q = q.Where(e => allowed.Contains(e.Id));
        var team = await q.OrderBy(e => e.Name).Select(e => new EmployeeDto { Id = e.Id, Name = e.Name, Role = e.Role, IsActive = true, DepartmentId = e.DepartmentId }).ToListAsync();
        return Ok(new EmployeeListResponse { Success = true, Employees = team, AutoAssign = await leads.IsAutoAssignEnabledAsync() });
    }

    /// <summary>Names of active departments, for pickers on pages (such as tasks) that anybody may use.</summary>
    [HttpGet("team/departments")]
    public async Task<DepartmentListResponse> TeamDepartments() => new()
    {
        Success = true,
        Departments = await db.Departments.AsNoTracking().Where(d => d.IsActive).OrderBy(d => d.Name).Select(d => new DepartmentDto { Id = d.Id, Name = d.Name, IsActive = true }).ToListAsync(),
    };

    // ---- Leads -----------------------------------------------------------------------

    /// <param name="assignee">admin only: "unassigned", "me" or an employee id. Sales users always get just their own leads.</param>
    [HttpGet("leads")]
    public async Task<LeadListResponse> Leads([FromQuery] string? status, [FromQuery] string? source, [FromQuery] string? q, [FromQuery] string? assignee, [FromQuery] int take = 100)
    {
        var baseQuery = db.Leads.AsNoTracking().AsQueryable();
        if (!CanAll("leads")) baseQuery = baseQuery.Where(l => l.AssignedToEmployeeId == MyId);
        else if (assignee == "unassigned") baseQuery = baseQuery.Where(l => l.AssignedToEmployeeId == null);
        else if (assignee == "me") baseQuery = baseQuery.Where(l => l.AssignedToEmployeeId == MyId);
        else if (long.TryParse(assignee, out var aid)) baseQuery = baseQuery.Where(l => l.AssignedToEmployeeId == aid);

        if (!string.IsNullOrWhiteSpace(source)) baseQuery = baseQuery.Where(l => l.Source == source);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            var digits = new string(term.Where(char.IsDigit).ToArray());
            baseQuery = baseQuery.Where(l => l.Name.Contains(term) || (digits.Length > 0 && l.Phone.Contains(digits)) || (l.Email != null && l.Email.Contains(term)));
        }

        var counts = await baseQuery.GroupBy(l => l.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync();
        var filtered = string.IsNullOrWhiteSpace(status) ? baseQuery : baseQuery.Where(l => l.Status == status);
        var rows = await filtered.OrderByDescending(l => l.LastContactAt).Take(Math.Clamp(take, 1, 500))
            .GroupJoin(db.Conversations.AsNoTracking(), l => l.Id, c => c.LeadId, (l, cs) => new { l, cs })
            .SelectMany(x => x.cs.DefaultIfEmpty(), (x, c) => new { x.l, c })
            .ToListAsync();

        var ids = rows.Select(r => r.l.Id).ToList();
        var last = await db.WhatsAppMessages.AsNoTracking()
            .Where(m => m.LeadId != null && ids.Contains(m.LeadId.Value)
                && m.Id == db.WhatsAppMessages.Where(x => x.LeadId == m.LeadId).Max(x => x.Id))
            .ToListAsync();
        var lastByLead = last.GroupBy(m => m.LeadId!.Value).ToDictionary(g => g.Key, g => g.First());
        var names = await db.Employees.AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.Name);

        var nextFu = (await db.FollowUps.AsNoTracking().Where(f => f.Status == "pending" && ids.Contains(f.LeadId)).GroupBy(f => f.LeadId).Select(g => new { g.Key, At = g.Min(x => x.DueAt) }).ToListAsync()).ToDictionary(x => x.Key, x => x.At);
        var orderStats = await OrderStatsAsync(rows.Select(r => r.l));

        return new LeadListResponse
        {
            Success = true,
            Leads = rows.Select(r =>
            {
                var dto = r.l.ToDto(r.c);
                dto.AssignedToName = r.l.AssignedToEmployeeId is { } eid ? names.GetValueOrDefault(eid) : null;
                if (nextFu.TryGetValue(r.l.Id, out var nf)) dto.NextFollowUpAt = nf;
                if (orderStats.TryGetValue(r.l.Id, out var os)) { dto.OrderCount = os.Count; dto.LifetimeValue = os.Value; dto.LastOrderAt = os.Last; }
                if (lastByLead.TryGetValue(r.l.Id, out var m))
                {
                    dto.LastMessage = string.IsNullOrEmpty(m.Body) ? Mapping.MessageLabel(m.Type) : m.Body.Length > 90 ? m.Body[..(char.IsHighSurrogate(m.Body[89]) ? 89 : 90)] + "…" : m.Body;
                    dto.LastMessageDirection = m.Direction;
                }
                return dto;
            }).ToList(),
            Total = await filtered.CountAsync(),
            StatusCounts = counts.ToDictionary(c => c.Key, c => c.N),
            UnassignedCount = CanAll("leads") ? await db.Leads.CountAsync(l => l.AssignedToEmployeeId == null) : 0,
        };
    }

    // ---- Customer view: orders matched to the lead by phone ------------------------------------

    private static string Last10(string phone) { var d = new string((phone ?? "").Where(char.IsDigit).ToArray()); return d.Length > 10 ? d[^10..] : d; }

    /// <summary>Order phone numbers are saved as typed (10 digits, or with 91 / 0 in front), so match every common form.</summary>
    private static string[] PhoneForms(string phone) { var l = Last10(phone); return [l, "91" + l, "0" + l, "+91" + l, phone]; }

    /// <summary>Orders that count as revenue: confirmed (paid or cash on delivery) and not cancelled or returned.</summary>
    private static bool Counts(Order o) => o.PaymentStatus is "PAID" or "COD_PENDING" or "COD_COLLECTED" && o.FulfillmentStatus is not ("CANCELLED" or "RTO");

    private async Task<List<Order>> OrdersForAsync(Lead lead)
    {
        var forms = PhoneForms(lead.Phone);
        return await db.Orders.AsNoTracking().Where(o => forms.Contains(o.CustomerPhone)).OrderByDescending(o => o.CreatedAt).Take(50).ToListAsync();
    }

    private sealed record OrderStat(int Count, decimal Value, DateTime? Last);

    private async Task<Dictionary<long, OrderStat>> OrderStatsAsync(IEnumerable<Lead> page)
    {
        var list = page.ToList();
        var forms = list.SelectMany(l => PhoneForms(l.Phone)).Distinct().ToList();
        if (forms.Count == 0) return [];
        var orders = await db.Orders.AsNoTracking().Where(o => forms.Contains(o.CustomerPhone))
            .Select(o => new Order { CustomerPhone = o.CustomerPhone, TotalAmount = o.TotalAmount, CreatedAt = o.CreatedAt, PaymentStatus = o.PaymentStatus, FulfillmentStatus = o.FulfillmentStatus }).ToListAsync();
        var result = new Dictionary<long, OrderStat>();
        foreach (var l in list)
        {
            var mine = orders.Where(o => Last10(o.CustomerPhone) == Last10(l.Phone) && Counts(o)).ToList();
            if (mine.Count > 0) result[l.Id] = new OrderStat(mine.Count, mine.Sum(o => o.TotalAmount), mine.Max(o => o.CreatedAt));
        }
        return result;
    }

    private static LeadOrderDto ToLeadOrder(Order o) => new()
    {
        Id = o.Id, CreatedAt = o.CreatedAt, TotalAmount = o.TotalAmount, PaymentMethod = o.PaymentMethod, PaymentStatus = o.PaymentStatus,
        DeliveryStatus = o.DeliveryStatus, FulfillmentStatus = o.FulfillmentStatus, Courier = o.Courier,
        Awb = string.IsNullOrEmpty(o.ShiprocketAwb) || o.ShiprocketAwb.StartsWith("SR-PENDING-") ? null : o.ShiprocketAwb,
        DeliveredAt = o.DeliveredAt, Items = string.Join(", ", o.Items.Select(i => $"{i.Name} x {i.Quantity}")),
    };

    /// <summary>
    /// Sell to a lead: creates a real order (cash on delivery, or already paid by UPI / bank transfer), marks the lead as a customer
    /// and keeps it with its owner. The order then follows the normal flow (courier booking, status messages, 15-day journey).
    /// </summary>
    [HttpPost("leads/{id:long}/order")]
    public async Task<IActionResult> LeadOrder(long id, LeadOrderRequest r)
    {
        var lead = await FindLeadAsync(id);
        if (lead is null) return NoLead();
        if (r.Items.Count == 0 || r.Items.Any(i => string.IsNullOrWhiteSpace(i.ProductId) || i.Quantity < 1 || i.Quantity > 20))
            return BadRequest(new ApiResult { Success = false, Message = "Choose at least one product (1 to 20 of each)." });
        if (string.IsNullOrWhiteSpace(r.Email) || !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(r.Email.Trim()))
            return BadRequest(new ApiResult { Success = false, Message = "A valid customer email is required for the order confirmation." });
        if (lead.ConsentStatus == "opted_out")
            return BadRequest(new ApiResult { Success = false, Message = "This customer opted out of messages, so order updates cannot be sent. Ask them to message you first." });

        try
        {
            var order = await orders.CreateAsync(new CreateOrderRequest
            {
                Customer = new CheckoutCustomerDto { FullName = string.IsNullOrWhiteSpace(r.CustomerName) ? lead.Name : r.CustomerName.Trim(), Phone = Last10(lead.Phone), Email = r.Email.Trim() },
                Shipping = r.Address,
                Items = r.Items.GroupBy(i => i.ProductId).Select(g => new CartLineDto { Id = g.Key, Quantity = g.Sum(x => x.Quantity) }).ToList(),
                CouponCode = string.IsNullOrWhiteSpace(r.CouponCode) ? null : r.CouponCode.Trim(),
                PaymentMethod = r.AlreadyPaid ? "ONLINE" : "COD",
            }, null);

            if (r.AlreadyPaid)
                order = await orders.MarkPaidAsync(order.Id, string.IsNullOrWhiteSpace(r.PaymentReference) ? $"CRM-{DateTime.UtcNow:yyyyMMddHHmmss}" : r.PaymentReference.Trim(), null, "{\"source\":\"crm-manual\"}");

            var tracked = await db.Orders.FirstAsync(o => o.Id == order.Id);
            tracked.Notes = string.IsNullOrWhiteSpace(tracked.Notes) ? $"Created in CRM by {MyName}" : $"{tracked.Notes} | Created in CRM by {MyName}";
            await db.SaveChangesAsync();

            var fresh = await db.Leads.FirstAsync(l => l.Id == id);
            if (string.IsNullOrEmpty(fresh.Email)) fresh.Email = r.Email.Trim();
            await leads.UpsertFromOrderAsync(tracked);
            log.LogInformation("[Lead] {Who} created order {Order} for lead {Id}", MyName, order.Id, id);
            return StatusCode(201, new LeadOrderResponse { Success = true, OrderId = order.Id, TotalAmount = order.TotalAmount, Message = $"Order {order.Id} created for ₹{order.TotalAmount:N0}. The customer will get the confirmation on WhatsApp and e-mail." });
        }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResult { Success = false, Message = ex.Message }); }
    }

    /// <summary>Loads a lead the caller may see: any lead for admins, only their own for sales (404 otherwise, so ids do not leak).</summary>
    private async Task<Lead?> FindLeadAsync(long id)
    {
        var lead = await db.Leads.FirstOrDefaultAsync(l => l.Id == id);
        return lead is null || (!CanAll("leads") && lead.AssignedToEmployeeId != MyId) ? null : lead;
    }

    private static NotFoundObjectResult NoLead() => new(new ApiResult { Success = false, Message = "Lead not found" });

    [HttpGet("leads/{id:long}")]
    public async Task<IActionResult> LeadDetail(long id)
    {
        var lead = await FindLeadAsync(id);
        if (lead is null) return NoLead();
        var convo = await db.Conversations.FirstOrDefaultAsync(c => c.LeadId == id);
        var dto = lead.ToDto(convo);
        if (lead.AssignedToEmployeeId is { } eid) dto.AssignedToName = await db.Employees.AsNoTracking().Where(e => e.Id == eid).Select(e => e.Name).FirstOrDefaultAsync();
        if (convo is { UnreadCount: > 0 }) { convo.UnreadCount = 0; await db.SaveChangesAsync(); }

        var messages = await db.WhatsAppMessages.AsNoTracking().Where(m => m.LeadId == id).OrderByDescending(m => m.CreatedAt).Take(200).ToListAsync();
        var history = await db.LeadStatusHistory.AsNoTracking().Where(h => h.LeadId == id).OrderByDescending(h => h.CreatedAt).ToListAsync();
        var assignments = await db.LeadAssignmentHistory.AsNoTracking().Where(h => h.LeadId == id).OrderByDescending(h => h.CreatedAt).ToListAsync();
        var names = await db.Employees.AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.Name);
        var fus = await db.FollowUps.AsNoTracking().Where(f => f.LeadId == id)
            .OrderBy(f => f.Status == "pending" ? 0 : 1).ThenBy(f => f.Status == "pending" ? f.DueAt : DateTime.MaxValue).ThenByDescending(f => f.CompletedAt)
            .Take(40).ToListAsync();
        dto.NextFollowUpAt = fus.Where(f => f.Status == "pending").Select(f => (DateTime?)f.DueAt).Min();
        var leadOrders = await OrdersForAsync(lead);
        var counted = leadOrders.Where(Counts).ToList();
        dto.OrderCount = counted.Count; dto.LifetimeValue = counted.Sum(o => o.TotalAmount); dto.LastOrderAt = counted.Select(o => (DateTime?)o.CreatedAt).Max();
        return Ok(new LeadDetailResponse
        {
            Success = true, Lead = dto,
            FollowUps = fus.Select(f => f.ToDto(lead.Name, lead.Phone, f.AssignedToEmployeeId is { } oid ? names.GetValueOrDefault(oid) : null)).ToList(),
            Orders = leadOrders.Select(ToLeadOrder).ToList(),
            Messages = messages.OrderBy(m => m.CreatedAt).Select(m => m.ToDto(signer)).ToList(),
            History = history.Select(h => new LeadStatusHistoryDto { FromStatus = h.FromStatus, ToStatus = h.ToStatus, Note = h.Note, ChangedBy = h.ChangedBy, CreatedAt = h.CreatedAt }).ToList(),
            Assignments = assignments.Select(a => new LeadAssignmentDto
            {
                FromName = a.FromEmployeeId is { } f ? names.GetValueOrDefault(f) ?? "Former employee" : null,
                ToName = a.ToEmployeeId is { } t ? names.GetValueOrDefault(t) ?? "Former employee" : null,
                Reason = a.Reason, ChangedBy = a.ChangedBy, CreatedAt = a.CreatedAt,
            }).ToList(),
        });
    }

    [HttpPut("leads/{id:long}/status")]
    public async Task<IActionResult> LeadStatus(long id, LeadStatusRequest r)
    {
        if (!LeadStatuses.All.Contains(r.Status)) return BadRequest(new ApiResult { Success = false, Message = "Unknown status" });
        if (await FindLeadAsync(id) is null) return NoLead();
        await leads.ChangeStatusAsync(id, r.Status, r.Note, MyName);
        return Ok(new ApiResult { Success = true, Message = "Status updated" });
    }

    [HttpPut("leads/{id:long}/notes")]
    public async Task<IActionResult> LeadNotes(long id, LeadNotesRequest r)
    {
        var lead = await FindLeadAsync(id);
        if (lead is null) return NoLead();
        var notes = r.Notes?.Trim();
        lead.Notes = string.IsNullOrEmpty(notes) ? null : notes[..Math.Min(notes.Length, 4000)];
        lead.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new ApiResult { Success = true, Message = "Notes saved" });
    }

    /// <summary>Record the customer's WhatsApp consent after asking them (on a call or in chat). Unlocks automatic follow-up messages.</summary>
    [HttpPut("leads/{id:long}/consent")]
    public async Task<IActionResult> LeadConsent(long id, LeadConsentRequest r)
    {
        var lead = await FindLeadAsync(id);
        if (lead is null) return NoLead();
        if (r.Status is not ("opted_in" or "unknown")) return BadRequest(new ApiResult { Success = false, Message = "Use opted_in or unknown." });
        if (lead.ConsentStatus == "opted_out" && r.Status == "opted_in" && !CanAll("leads"))
            return BadRequest(new ApiResult { Success = false, Message = "This customer sent STOP. Only an administrator can restore consent, and only if the customer asked." });
        lead.ConsentStatus = r.Status; lead.ConsentAt = DateTime.UtcNow; lead.ConsentSource = $"recorded by {MyName}"; lead.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new ApiResult { Success = true, Message = r.Status == "opted_in" ? "Marked as opted in" : "Consent cleared" });
    }

    /// <summary>Assign / transfer. Admins can assign anyone or unassign; a sales owner can hand their lead to another sales employee.</summary>
    [HttpPut("leads/{id:long}/assign")]
    public async Task<IActionResult> LeadAssign(long id, LeadAssignRequest r)
    {
        var lead = await FindLeadAsync(id);
        if (lead is null) return NoLead();
        if (r.EmployeeId is null && !CanAll("leads")) return StatusCode(403, new ApiResult { Success = false, Message = "Only an administrator can unassign a lead." });
        if (r.EmployeeId is { } target)
        {
            var emp = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == target && e.IsActive);
            if (emp is null) return BadRequest(new ApiResult { Success = false, Message = "That employee is not active." });
            if (!CanAll("leads") && emp.Role != EmployeeRoles.Sales) return BadRequest(new ApiResult { Success = false, Message = "Leads can only be transferred to a sales employee." });
        }
        await leads.AssignAsync(lead, r.EmployeeId, string.IsNullOrWhiteSpace(r.Reason) ? (CanAll("leads") ? "Assigned by manager" : "Transferred by owner") : r.Reason, MyName);
        await db.SaveChangesAsync();
        return Ok(new ApiResult { Success = true, Message = r.EmployeeId is null ? "Lead unassigned" : "Lead assigned" });
    }

    /// <summary>Reply to a lead on WhatsApp. Sales users can only reply to their own leads; consent and the 24h window are enforced here too.</summary>
    [HttpPost("leads/{id:long}/reply")]
    public async Task<IActionResult> LeadReply(long id, LeadReplyRequest r)
    {
        var lead = await FindLeadAsync(id);
        if (lead is null) return NoLead();
        if (string.IsNullOrWhiteSpace(r.Text)) return BadRequest(new ApiResult { Success = false, Message = "Type a message first." });
        if (lead.ConsentStatus == "opted_out") return BadRequest(new ApiResult { Success = false, Message = "This customer opted out of messages." });
        var window = await db.Conversations.AsNoTracking().Where(c => c.LeadId == id).Select(c => c.ServiceWindowExpiresAt).FirstOrDefaultAsync();
        if (window is null || window < DateTime.UtcNow)
            return BadRequest(new ApiResult { Success = false, Message = "The 24-hour reply window is closed. Only approved templates can be sent until the customer writes again." });

        var ok = await whatsapp.SendTextAsync(lead.Phone, r.Text.Trim(), sentBy: MyName);
        if (ok) log.LogInformation("[Lead] {Who} replied to lead {Id}", MyName, id);
        return Ok(new ApiResult { Success = ok, Message = ok ? "Message sent" : "Message could not be sent. Check the WhatsApp credentials in the server configuration." });
    }

    // ---- Reply assistant ------------------------------------------------------------------------

    /// <summary>
    /// A drafted reply to the customer's latest message, for the employee to edit and send. Optionally rewrites the current draft
    /// ("shorter", "in Tamil"). Sales users can only ask about their own leads.
    /// </summary>
    [HttpPost("leads/{id:long}/suggest-reply")]
    public async Task<IActionResult> SuggestReply(long id, ReplySuggestRequest r, [FromServices] ReplyAssistantService assistant)
    {
        var lead = await FindLeadAsync(id);
        if (lead is null) return NoLead();
        var res = await assistant.SuggestAsync(lead, r ?? new ReplySuggestRequest());
        return res.Success ? Ok(res) : StatusCode(409, res);
    }

    [HttpGet("ai/reply-settings")]
    public async Task<ReplyAssistantSettingsDto> ReplySettings([FromServices] ReplyAssistantService assistant) => await assistant.GetSettingsAsync();

    [HttpPut("ai/reply-settings")]
    public async Task<IActionResult> SaveReplySettings(ReplyAssistantSettingsRequest r, [FromServices] ReplyAssistantService assistant)
    {
        await assistant.SaveSettingsAsync(r);
        return Ok(new ApiResult { Success = true, Message = "Reply assistant settings saved" });
    }

    // ---- Employees (admin only) --------------------------------------------------------

    [HttpGet("employees")]
    public async Task<EmployeeListResponse> Employees()
    {
        var emps = await db.Employees.AsNoTracking().OrderByDescending(e => e.IsActive).ThenBy(e => e.Name).ToListAsync();
        var stats = await db.Leads.AsNoTracking().Where(l => l.AssignedToEmployeeId != null)
            .GroupBy(l => l.AssignedToEmployeeId!.Value)
            .Select(g => new { Id = g.Key, Total = g.Count(), Open = g.Count(l => l.Status != LeadStatuses.Converted && l.Status != LeadStatuses.NotInterested) })
            .ToDictionaryAsync(x => x.Id);
        var openTasks = await db.WorkTasks.AsNoTracking().Where(t => t.AssignedToEmployeeId != null && t.Status != TaskStatuses.Done)
            .GroupBy(t => t.AssignedToEmployeeId!.Value).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        var deps = await db.Departments.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Name);
        var desig = await db.Designations.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Name);
        return new EmployeeListResponse
        {
            Success = true, AutoAssign = await leads.IsAutoAssignEnabledAsync(),
            Employees = emps.Select(e =>
            {
                var dto = e.ToDto();
                if (stats.TryGetValue(e.Id, out var s)) { dto.TotalLeads = s.Total; dto.OpenLeads = s.Open; }
                dto.OpenTasks = openTasks.GetValueOrDefault(e.Id);
                dto.DepartmentName = e.DepartmentId is { } d ? deps.GetValueOrDefault(d) : null;
                dto.DesignationName = e.DesignationId is { } g ? desig.GetValueOrDefault(g) : null;
                return dto;
            }).ToList(),
        };
    }

    /// <summary>Checks who may be given which role, department and designation. Nobody can hand out more access than they hold.</summary>
    private async Task<string?> CheckOrgAsync(EmployeeSaveRequest r, Employee? existing)
    {
        if (r.Role == EmployeeRoles.Admin && !IsAdmin) return "Only an administrator can create or promote administrators.";
        if (existing is { Role: EmployeeRoles.Admin } && !IsAdmin) return "Only an administrator can change an administrator's account.";
        if (r.DepartmentId is { } d && !await db.Departments.AnyAsync(x => x.Id == d && x.IsActive)) return "That department is not available.";
        if (r.DesignationId is { } g)
        {
            var des = await db.Designations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == g && x.IsActive);
            if (des is null) return "That designation is not available.";
            if (!IsAdmin)
                foreach (var (page, level) in PageCatalog.FromJson(des.RightsJson))
                    if (level > Level(page)) return $"That designation gives more access to {PageCatalog.Find(page)?.Label ?? page} than you have yourself.";
        }
        if (existing is not null && existing.Id == MyId && !IsAdmin && (existing.DesignationId != r.DesignationId || existing.Role != r.Role))
            return "You cannot change your own designation or role.";
        return null;
    }

    [HttpPost("employees")]
    public async Task<IActionResult> CreateEmployee(EmployeeSaveRequest r)
    {
        var error = Validate(r, creating: true) ?? await CheckOrgAsync(r, null);
        if (error is not null) return BadRequest(new ApiResult { Success = false, Message = error });
        var email = r.Email.Trim().ToLowerInvariant();
        if (await db.Employees.AnyAsync(e => e.Email == email)) return BadRequest(new ApiResult { Success = false, Message = "An employee with this email already exists." });

        var emp = new Employee
        {
            Name = r.Name.Trim(), Email = email, Phone = (r.Phone ?? "").Trim(), Role = r.Role, IsActive = r.IsActive,
            PasswordHash = AdminPassword.Hash(r.Password!), DepartmentId = r.DepartmentId, DesignationId = r.DesignationId,
        };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();
        RightsService.Invalidate();
        log.LogInformation("[Staff] {Admin} created {Role} employee {Email}", MyName, emp.Role, emp.Email);
        return StatusCode(201, new EmployeeResponse { Success = true, Message = "Employee created", Employee = emp.ToDto() });
    }

    [HttpPut("employees/{id:long}")]
    public async Task<IActionResult> UpdateEmployee(long id, EmployeeSaveRequest r)
    {
        var emp = await db.Employees.FirstOrDefaultAsync(e => e.Id == id);
        if (emp is null) return NotFound(new ApiResult { Success = false, Message = "Employee not found" });
        var error = Validate(r, creating: false) ?? await CheckOrgAsync(r, emp);
        if (error is not null) return BadRequest(new ApiResult { Success = false, Message = error });
        var email = r.Email.Trim().ToLowerInvariant();
        if (await db.Employees.AnyAsync(e => e.Email == email && e.Id != id)) return BadRequest(new ApiResult { Success = false, Message = "Another employee already uses this email." });
        if (id == MyId && (!r.IsActive || r.Role != EmployeeRoles.Admin) && IsAdmin)
            return BadRequest(new ApiResult { Success = false, Message = "You cannot deactivate or demote your own account." });
        if (id == MyId && !r.IsActive)
            return BadRequest(new ApiResult { Success = false, Message = "You cannot deactivate your own account." });

        var wasActive = emp.IsActive;
        emp.Name = r.Name.Trim(); emp.Email = email; emp.Phone = (r.Phone ?? "").Trim(); emp.Role = r.Role; emp.IsActive = r.IsActive;
        emp.DepartmentId = r.DepartmentId; emp.DesignationId = r.DesignationId;
        if (!string.IsNullOrEmpty(r.Password)) emp.PasswordHash = AdminPassword.Hash(r.Password);
        await db.SaveChangesAsync();
        RightsService.Invalidate();

        // A deactivated person, or one who can no longer work leads, must not keep leads silently.
        var moved = 0;
        var canWorkLeads = emp.IsActive && emp.Role == EmployeeRoles.Sales && (await rightsService.EmployeesWithPageAsync("leads")).Contains(emp.Id);
        if ((wasActive && !emp.IsActive) || !canWorkLeads)
            moved = await leads.ReassignOpenLeadsAsync(emp.Id, spreadAcrossTeam: true, MyName);
        return Ok(new EmployeeResponse { Success = true, Message = moved > 0 ? $"Employee updated; {moved} open lead(s) were reassigned." : "Employee updated", Employee = emp.ToDto() });
    }

    /// <summary>Removing an employee deactivates the login (history stays intact), moves their open leads and returns their open tasks to unassigned.</summary>
    [HttpDelete("employees/{id:long}")]
    public async Task<IActionResult> RemoveEmployee(long id, [FromQuery] string? reassign)
    {
        var emp = await db.Employees.FirstOrDefaultAsync(e => e.Id == id);
        if (emp is null) return NotFound(new ApiResult { Success = false, Message = "Employee not found" });
        if (id == MyId) return BadRequest(new ApiResult { Success = false, Message = "You cannot remove your own account." });
        if (emp.Role == EmployeeRoles.Admin && !IsAdmin) return BadRequest(new ApiResult { Success = false, Message = "Only an administrator can remove an administrator." });

        emp.IsActive = false;
        foreach (var t in await db.WorkTasks.Where(t => t.AssignedToEmployeeId == id && t.Status != TaskStatuses.Done).ToListAsync())
        {
            t.AssignedToEmployeeId = null;
            db.TaskActivities.Add(new TaskActivity { TaskId = t.Id, Actor = MyName, Text = $"Left unassigned because {emp.Name} was removed" });
        }
        if (await db.Departments.FirstOrDefaultAsync(d => d.HeadEmployeeId == id) is { } headOf) headOf.HeadEmployeeId = null;
        await db.SaveChangesAsync();
        RightsService.Invalidate();
        var moved = await leads.ReassignOpenLeadsAsync(emp.Id, spreadAcrossTeam: !string.Equals(reassign, "unassigned", StringComparison.OrdinalIgnoreCase), MyName);
        log.LogInformation("[Staff] {Admin} removed employee {Email}; {Moved} lead(s) reassigned", MyName, emp.Email, moved);
        return Ok(new ApiResult { Success = true, Message = moved > 0 ? $"{emp.Name} removed; {moved} open lead(s) were reassigned." : $"{emp.Name} removed." });
    }

    [HttpPut("leads/settings")]
    public async Task<IActionResult> LeadSettings(LeadSettingsRequest r)
    {
        await leads.SetAutoAssignAsync(r.AutoAssign);
        return Ok(new ApiResult { Success = true, Message = r.AutoAssign ? "New leads will be assigned round-robin." : "New leads will wait in the admin queue." });
    }

    private static string? Validate(EmployeeSaveRequest r, bool creating)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) return "Name is required.";
        if (string.IsNullOrWhiteSpace(r.Email) || !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(r.Email.Trim())) return "A valid email is required.";
        if (!Roles.Contains(r.Role)) return "Role must be admin or sales.";
        if (creating && (r.Password is null || r.Password.Length < 8)) return "Password must be at least 8 characters.";
        if (!creating && !string.IsNullOrEmpty(r.Password) && r.Password.Length < 8) return "Password must be at least 8 characters.";
        return null;
    }
}
