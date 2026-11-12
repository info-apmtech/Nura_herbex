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
        if (forPage is "leads" or "followups" or "interested" or "care") allowed = await rightsService.EmployeesWithPageAsync(forPage);
        else if (forPage is null) allowed = await rightsService.EmployeesWithPageAsync("leads"); // the original meaning: people who can own leads
        if (allowed is not null) q = q.Where(e => allowed.Contains(e.Id));
        var team = await q.OrderBy(e => e.Name).Select(e => new EmployeeDto { Id = e.Id, Name = e.Name, Role = e.Role == EmployeeRole.Admin ? EmployeeRoles.Admin : EmployeeRoles.Sales, IsActive = true, DepartmentId = e.DepartmentId, IsOnline = e.LastSeenAt != null && e.LastSeenAt >= StaffAvailability.OnlineCutoff(), WhatsAppActive = e.WhatsAppActive }).ToListAsync();
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

    /// <summary>Which page's rights cover a person: paid customers are Customer care, interested leads are Hot leads, everyone else is Leads.</summary>
    private static string PageOf(Lead l) => l.CustomerSince != null ? LeadViews.Care : l.Status == Nuraherbex.Shared.Models.LeadStatus.Interested ? LeadViews.Interested : LeadViews.Leads;

    private static IQueryable<Lead> InView(IQueryable<Lead> q, string view) => view switch
    {
        LeadViews.Care => q.Where(l => l.CustomerSince != null),
        LeadViews.Interested => q.Where(l => l.CustomerSince == null && l.Status == Nuraherbex.Shared.Models.LeadStatus.Interested),
        _ => q.Where(l => l.CustomerSince == null && l.Status != Nuraherbex.Shared.Models.LeadStatus.Interested),
    };

    /// <param name="view">leads (new and follow-up), interested (hot leads) or care (paid customers).</param>
    /// <param name="assignee">managers only: "unassigned", "me" or an employee id. Everyone else always gets just their own people.</param>
    [HttpGet("leads")]
    public async Task<LeadListResponse> Leads([FromQuery] string? status, [FromQuery] string? source, [FromQuery] string? q, [FromQuery] string? assignee, [FromQuery] int take = 100, [FromQuery] string? view = null)
    {
        view = LeadViews.All.Contains(view ?? "") ? view! : LeadViews.Leads;
        if (Level(view) < 1) return new LeadListResponse { Success = false, Message = "Your designation does not give you access to this page." };

        // Counts for the panel tabs: how many people sit in each panel the caller may open (scoped to their own when they only see their own).
        var viewCounts = new Dictionary<string, int>();
        foreach (var v in LeadViews.All)
        {
            if (Level(v) < 1) continue;
            var vq = InView(db.Leads.AsNoTracking(), v);
            if (!CanAll(v)) vq = vq.Where(l => l.AssignedToEmployeeId == MyId);
            viewCounts[v] = await vq.CountAsync();
        }

        var baseQuery = InView(db.Leads.AsNoTracking(), view);
        if (!CanAll(view)) baseQuery = baseQuery.Where(l => l.AssignedToEmployeeId == MyId);
        else if (assignee == "unassigned") baseQuery = baseQuery.Where(l => l.AssignedToEmployeeId == null);
        else if (assignee == "me") baseQuery = baseQuery.Where(l => l.AssignedToEmployeeId == MyId);
        else if (long.TryParse(assignee, out var aid)) baseQuery = baseQuery.Where(l => l.AssignedToEmployeeId == aid);
        var scopeQuery = baseQuery; // before search, for the unread total

        if (!string.IsNullOrWhiteSpace(source))
            baseQuery = LeadSources.TryParse(source, out var sourceEnum) ? baseQuery.Where(l => l.Source == sourceEnum) : baseQuery.Where(_ => false);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            var digits = new string(term.Where(char.IsDigit).ToArray());
            baseQuery = baseQuery.Where(l => l.Name.Contains(term) || (digits.Length > 0 && l.Phone.Contains(digits)) || (l.Email != null && l.Email.Contains(term)));
        }

        var counts = await baseQuery.GroupBy(l => l.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync();
        var filtered = string.IsNullOrWhiteSpace(status)
            ? baseQuery
            : LeadStatuses.TryParse(status, out var statusEnum)
                ? baseQuery.Where(l => l.Status == statusEnum)
                : baseQuery.Where(_ => false);
        var rows = await filtered.OrderByDescending(l => l.LastContactAt).Take(Math.Clamp(take, 1, 500))
            .GroupJoin(db.Conversations.AsNoTracking(), l => l.Id, c => c.LeadId, (l, cs) => new { l, cs })
            .SelectMany(x => x.cs.DefaultIfEmpty(), (x, c) => new { x.l, c })
            .ToListAsync();
        rows.Reverse(); // the newest N were fetched; show them oldest first so the latest lead sits at the bottom

        var ids = rows.Select(r => r.l.Id).ToList();
        var last = await db.WhatsAppMessages.AsNoTracking()
            .Where(m => m.LeadId != null && ids.Contains(m.LeadId.Value)
                && m.Id == db.WhatsAppMessages.Where(x => x.LeadId == m.LeadId).Max(x => x.Id))
            .ToListAsync();
        var lastByLead = last.GroupBy(m => m.LeadId!.Value).ToDictionary(g => g.Key, g => g.First());
        var names = await db.Employees.AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.Name);

        var nextFu = (await db.FollowUps.AsNoTracking().Where(f => f.Status == FollowUpStatus.Pending && ids.Contains(f.LeadId)).GroupBy(f => f.LeadId).Select(g => new { g.Key, At = g.Min(x => x.DueAt) }).ToListAsync()).ToDictionary(x => x.Key, x => x.At);
        var orderStats = await OrderStatsAsync(rows.Select(r => r.l));

        return new LeadListResponse
        {
            Success = true,
            Leads = rows.Select(r =>
            {
                var dto = r.l.ToDto(r.c);
                dto.AssignedToName = r.l.AssignedToEmployeeId is { } eid ? names.GetValueOrDefault(eid) : null;
                dto.ConvertedByName = r.l.ConvertedByEmployeeId is { } cid ? names.GetValueOrDefault(cid) : null;
                if (nextFu.TryGetValue(r.l.Id, out var nf)) dto.NextFollowUpAt = nf;
                if (orderStats.TryGetValue(r.l.Id, out var os)) { dto.OrderCount = os.Count; dto.LifetimeValue = os.Value; dto.LastOrderAt = os.Last; dto.OpenOrders = os.Open; }
                if (lastByLead.TryGetValue(r.l.Id, out var m))
                {
                    dto.LastMessage = string.IsNullOrEmpty(m.Body) ? Mapping.MessageLabel(m.Type) : m.Body.Length > 90 ? m.Body[..(char.IsHighSurrogate(m.Body[89]) ? 89 : 90)] + "…" : m.Body;
                    dto.LastMessageDirection = m.Direction;
                }
                return dto;
            }).ToList(),
            Total = await filtered.CountAsync(),
            StatusCounts = counts.ToDictionary(c => c.Key.ToCode(), c => c.N),
            ViewCounts = viewCounts,
            UnreadTotal = await db.Conversations.AsNoTracking().Where(c => c.UnreadCount > 0 && scopeQuery.Any(l => l.Id == c.LeadId)).SumAsync(c => (int?)c.UnreadCount) ?? 0,
            UnassignedCount = CanAll(view) ? await InView(db.Leads.AsNoTracking(), view).CountAsync(l => l.AssignedToEmployeeId == null) : 0,
        };
    }

    // ---- Customer view: orders matched to the lead by phone ------------------------------------

    private static string Last10(string phone) { var d = new string((phone ?? "").Where(char.IsDigit).ToArray()); return d.Length > 10 ? d[^10..] : d; }

    /// <summary>Order phone numbers are saved as typed (10 digits, or with 91 / 0 in front), so match every common form.</summary>
    private static string[] PhoneForms(string phone) { var l = Last10(phone); return [l, "91" + l, "0" + l, "+91" + l, phone]; }

    /// <summary>Orders that count as revenue: confirmed (paid or cash on delivery) and not cancelled or returned.</summary>
    private static bool Counts(Order o) => o.PaymentStatus is OrderPaymentStatus.Paid or OrderPaymentStatus.CodPending or OrderPaymentStatus.CodCollected && o.FulfillmentStatus is not ("CANCELLED" or "RTO");

    private async Task<List<Order>> OrdersForAsync(Lead lead)
    {
        var forms = PhoneForms(lead.Phone);
        return await db.Orders.AsNoTracking().Where(o => forms.Contains(o.CustomerPhone)).OrderByDescending(o => o.CreatedAt).Take(50).ToListAsync();
    }

    private sealed record OrderStat(int Count, decimal Value, DateTime? Last, int Open);

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
            if (mine.Count > 0) result[l.Id] = new OrderStat(mine.Count, mine.Sum(o => o.TotalAmount), mine.Max(o => o.CreatedAt), mine.Count(o => o.FulfillmentStatus != "DELIVERED"));
        }
        return result;
    }

    private static LeadOrderDto ToLeadOrder(Order o) => new()
    {
        Id = o.Id, CreatedAt = o.CreatedAt, TotalAmount = o.TotalAmount, PaymentMethod = o.PaymentMethod.ToCode(), PaymentStatus = o.PaymentStatus.ToCode(),
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
        if (!string.IsNullOrWhiteSpace(r.Email) && !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(r.Email.Trim()))
            return BadRequest(new ApiResult { Success = false, Message = "That email address is not valid. Fix it or leave it blank." });
        if (lead.ConsentStatus == "opted_out")
            return BadRequest(new ApiResult { Success = false, Message = "This customer opted out of messages, so order updates cannot be sent. Ask them to message you first." });

        try
        {
            var resolvedCustomerId = !string.IsNullOrWhiteSpace(lead.CustomerId) ? lead.CustomerId : null;
            if (resolvedCustomerId is null)
            {
                var pForms = PhoneForms(lead.Phone);
                if (pForms.Length > 0)
                    resolvedCustomerId = await db.Customers.AsNoTracking().Where(c => pForms.Contains(c.Phone)).Select(c => c.Id).FirstOrDefaultAsync();
                if (resolvedCustomerId is null)
                {
                    var lMail = (!string.IsNullOrWhiteSpace(r.Email) ? r.Email : lead.Email ?? "").Trim().ToLowerInvariant();
                    if (lMail != "")
                        resolvedCustomerId = await db.Customers.AsNoTracking().Where(c => c.Email == lMail).Select(c => c.Id).FirstOrDefaultAsync();
                }
            }

            var order = await orders.CreateAsync(new CreateOrderRequest
            {
                Customer = new CheckoutCustomerDto { Id = resolvedCustomerId, FullName = string.IsNullOrWhiteSpace(r.CustomerName) ? lead.Name : r.CustomerName.Trim(), Phone = Last10(lead.Phone), Email = (r.Email ?? "").Trim() },
                Shipping = r.Address,
                Items = r.Items.GroupBy(i => i.ProductId).Select(g => new CartLineDto { Id = g.Key, Quantity = g.Sum(x => x.Quantity) }).ToList(),
                CouponCode = string.IsNullOrWhiteSpace(r.CouponCode) ? null : r.CouponCode.Trim(),
                PaymentMethod = r.AlreadyPaid ? "ONLINE" : "COD",
            }, resolvedCustomerId, emailOptional: true);

            if (r.AlreadyPaid)
                order = await orders.MarkPaidAsync(order.Id, string.IsNullOrWhiteSpace(r.PaymentReference) ? $"CRM-{DateTime.UtcNow:yyyyMMddHHmmss}" : r.PaymentReference.Trim(), null, "{\"source\":\"crm-manual\"}");

            var tracked = await db.Orders.FirstAsync(o => o.Id == order.Id);
            tracked.Notes = string.IsNullOrWhiteSpace(tracked.Notes) ? $"Created in CRM by {MyName}" : $"{tracked.Notes} | Created in CRM by {MyName}";
            await db.SaveChangesAsync();

            var fresh = await db.Leads.FirstAsync(l => l.Id == id);
            if (string.IsNullOrEmpty(fresh.Email) && !string.IsNullOrWhiteSpace(r.Email)) fresh.Email = r.Email.Trim();
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
        if (lead is null) return null;
        var page = PageOf(lead); // customers follow Customer care rights, interested leads follow Hot leads rights
        if (Level(page) < 1) return null;
        return !CanAll(page) && lead.AssignedToEmployeeId != MyId ? null : lead;
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
        if (lead.ConvertedByEmployeeId is { } cid) dto.ConvertedByName = await db.Employees.AsNoTracking().Where(e => e.Id == cid).Select(e => e.Name).FirstOrDefaultAsync();
        if (convo is { UnreadCount: > 0 }) { convo.UnreadCount = 0; await db.SaveChangesAsync(); }

        var messages = await db.WhatsAppMessages.AsNoTracking().Where(m => m.LeadId == id).OrderByDescending(m => m.CreatedAt).Take(200).ToListAsync();
        var history = await db.LeadStatusHistory.AsNoTracking().Where(h => h.LeadId == id).OrderByDescending(h => h.CreatedAt).ToListAsync();
        var assignments = await db.LeadAssignmentHistory.AsNoTracking().Where(h => h.LeadId == id).OrderByDescending(h => h.CreatedAt).ToListAsync();
        var names = await db.Employees.AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.Name);
        var fus = await db.FollowUps.AsNoTracking().Where(f => f.LeadId == id)
            .OrderBy(f => f.Status == FollowUpStatus.Pending ? 0 : 1).ThenBy(f => f.Status == FollowUpStatus.Pending ? f.DueAt : DateTime.MaxValue).ThenByDescending(f => f.CompletedAt)
            .Take(40).ToListAsync();
        dto.NextFollowUpAt = fus.Where(f => f.Status == FollowUpStatus.Pending).Select(f => (DateTime?)f.DueAt).Min();
        var leadOrders = await OrdersForAsync(lead);
        var counted = leadOrders.Where(Counts).ToList();
        dto.OrderCount = counted.Count; dto.LifetimeValue = counted.Sum(o => o.TotalAmount); dto.LastOrderAt = counted.Select(o => (DateTime?)o.CreatedAt).Max();
        dto.OpenOrders = counted.Count(o => o.FulfillmentStatus != "DELIVERED");
        return Ok(new LeadDetailResponse
        {
            Success = true, Lead = dto,
            FollowUps = fus.Select(f => f.ToDto(lead.Name, lead.Phone, f.AssignedToEmployeeId is { } oid ? names.GetValueOrDefault(oid) : null, names)).ToList(),
            Orders = leadOrders.Select(ToLeadOrder).ToList(),
            Messages = messages.OrderBy(m => m.CreatedAt).Select(m => m.ToDto(signer)).ToList(),
            History = history.Select(h => new LeadStatusHistoryDto { FromStatus = h.FromStatus?.ToCode(), ToStatus = h.ToStatus.ToCode(), Note = h.Note, ChangedBy = ResolveChangedBy(h.ChangedBy, names, "system"), CreatedAt = h.CreatedAt }).ToList(),
            Assignments = assignments.Select(a => new LeadAssignmentDto
            {
                FromName = a.FromEmployeeId is { } f ? names.GetValueOrDefault(f) ?? "Former employee" : null,
                ToName = a.ToEmployeeId is { } t ? names.GetValueOrDefault(t) ?? "Former employee" : null,
                Reason = a.Reason, ChangedBy = ResolveChangedBy(a.ChangedBy, names, "auto"), CreatedAt = a.CreatedAt,
            }).ToList(),
            Audit = BuildAudit(history, assignments, names),
        });
    }

    private static bool IsAutomatic(string? who) => string.IsNullOrEmpty(who) || who is "auto" or "system" or "journey";

    private static string ResolveChangedBy(string? who, Dictionary<long, string> names, string fallback) =>
        Mapping.ResolveActor(who, names) ?? fallback;

    /// <summary>Merges status and owner changes into one timeline so "who moved this, and when" is answered in one place.</summary>
    private static List<LeadAuditDto> BuildAudit(List<LeadStatusHistory> status, List<LeadAssignmentHistory> owners, Dictionary<long, string> names)
    {
        string Who(long? id) => id is { } i ? names.GetValueOrDefault(i) ?? "Former employee" : "nobody";
        var rows = status.Select(h => new LeadAuditDto
        {
            Kind = "status", CreatedAt = h.CreatedAt, Actor = ResolveChangedBy(h.ChangedBy, names, "system"), Automatic = IsAutomatic(h.ChangedBy), Detail = h.Note,
            Summary = h.FromStatus is { } prev ? $"Status {prev.ToCode()} → {h.ToStatus.ToCode()}" : $"Status set to {h.ToStatus.ToCode()}",
        }).Concat(owners.Select(a => new LeadAuditDto
        {
            Kind = "owner", CreatedAt = a.CreatedAt, Actor = ResolveChangedBy(a.ChangedBy, names, "system"), Automatic = IsAutomatic(a.ChangedBy), Detail = a.Reason,
            Summary = a.FromEmployeeId is null ? $"Assigned to {Who(a.ToEmployeeId)}" : $"Owner {Who(a.FromEmployeeId)} → {Who(a.ToEmployeeId)}",
        }));
        return rows.OrderByDescending(r => r.CreatedAt).ToList();
    }

    [HttpPut("leads/{id:long}/status")]
    public async Task<IActionResult> LeadStatus(long id, LeadStatusRequest r)
    {
        if (!LeadStatuses.All.Contains(r.Status)) return BadRequest(new ApiResult { Success = false, Message = "Unknown status" });
        var lead = await FindLeadAsync(id);
        if (lead is null) return NoLead();
        if (LeadService.StatusBlock(lead, r.Status) is { } blocked) return BadRequest(new ApiResult { Success = false, Message = blocked });
        await leads.ChangeStatusAsync(id, r.Status, r.Note, MyActorId);
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
        if (lead.ConsentStatus == "opted_out" && r.Status == "opted_in" && !CanAll(PageOf(lead)))
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
        if (r.EmployeeId is null && !CanAll(PageOf(lead))) return StatusCode(403, new ApiResult { Success = false, Message = "Only an administrator can unassign a lead." });
        if (r.EmployeeId is { } target)
        {
            var emp = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == target && e.IsActive);
            if (emp is null) return BadRequest(new ApiResult { Success = false, Message = "That employee is not active." });
            if (!CanAll(PageOf(lead)) && emp.Role != EmployeeRole.Sales) return BadRequest(new ApiResult { Success = false, Message = "Leads can only be transferred to a sales employee." });
        }
        await leads.AssignAsync(lead, r.EmployeeId, string.IsNullOrWhiteSpace(r.Reason) ? (CanAll(PageOf(lead)) ? "Assigned by manager" : "Transferred by owner") : r.Reason, MyActorId);
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
        return Ok(new ApiResult { Success = ok, Message = ok ? "Message sent" : whatsapp.LastError ?? "Message could not be sent. Check the WhatsApp credentials in the server configuration." });
    }

    /// <summary>Whether a free-form message (text, file or link) may go out now: not opted out and inside the 24-hour window. Otherwise why not.</summary>
    private async Task<string?> CannotSendFreeFormAsync(Lead lead)
    {
        if (lead.ConsentStatus == "opted_out") return "This customer opted out of messages.";
        var window = await db.Conversations.AsNoTracking().Where(c => c.LeadId == lead.Id).Select(c => c.ServiceWindowExpiresAt).FirstOrDefaultAsync();
        return window is null || window < DateTime.UtcNow
            ? "The 24-hour window is closed. Send an approved template first; once the customer replies you can send files and messages again."
            : null;
    }

    /// <summary>Send a file from the library: as the WhatsApp attachment itself, or as a message with its link. Only inside the 24-hour window.</summary>
    [HttpPost("leads/{id:long}/send-file")]
    public async Task<IActionResult> SendFile(long id, SendFileRequest r, [FromServices] LibraryService library, [FromServices] Microsoft.Extensions.Options.IOptions<Nuraherbex.Api.Options.StoreOptions> store)
    {
        var lead = await FindLeadAsync(id);
        if (lead is null) return NoLead();
        if (await CannotSendFreeFormAsync(lead) is { } why) return BadRequest(new ApiResult { Success = false, Message = why });
        var publicBase = string.IsNullOrWhiteSpace(store.Value.ApiPublicUrl) ? $"{Request.Scheme}://{Request.Host}" : store.Value.ApiPublicUrl;
        var file = await library.FindAsync(r.FileId ?? "", publicBase);
        if (file is null) return NotFound(new ApiResult { Success = false, Message = "That file is no longer in the library." });
        var caption = string.IsNullOrWhiteSpace(r.Caption) ? null : r.Caption.Trim()[..Math.Min(r.Caption.Trim().Length, 900)];

        bool ok;
        if (r.Mode == "link")
            ok = await whatsapp.SendTextAsync(lead.Phone, (caption is null ? "" : caption + "\n") + $"{file.Title}: {file.Url}", sentBy: MyName);
        else
        {
            if (!file.CanAttach) return BadRequest(new ApiResult { Success = false, Message = $"This {LibraryKinds.Label(file.Kind).ToLowerInvariant()} is too large for WhatsApp to deliver as an attachment. Send it as a link instead." });
            ok = await whatsapp.SendFileAsync(lead.Phone, file.Kind, file.Url, file.FileName, caption, file.Title, MyName);
        }
        if (ok) { await library.CountSentAsync(file.Id); log.LogInformation("[Lead] {Who} sent library file {File} to lead {Id} ({Mode})", MyName, file.Title, id, r.Mode); }
        return Ok(new ApiResult { Success = ok, Message = ok ? (r.Mode == "link" ? "Link sent" : "File sent") : whatsapp.LastError ?? "The file could not be sent. Check the WhatsApp credentials in the server configuration." });
    }

    /// <summary>
    /// Send an approved WhatsApp template. The only message allowed to start a chat or reopen one after the 24-hour window; the customer's reply reopens it.
    /// Name, sender and order values are filled in here, the rest comes from the person sending.
    /// </summary>
    [HttpPost("leads/{id:long}/send-template")]
    public async Task<IActionResult> SendTemplate(long id, SendTemplateRequest r, [FromServices] MessageTemplateService templates)
    {
        var lead = await FindLeadAsync(id);
        if (lead is null) return NoLead();
        if (lead.ConsentStatus == "opted_out") return BadRequest(new ApiResult { Success = false, Message = "This customer opted out of messages." });
        var def = MetaTemplates.Find(r.Name);
        if (def is null) return BadRequest(new ApiResult { Success = false, Message = "Unknown template." });
        if (!(await templates.GetAsync()).ApprovedMetaTemplates.Contains(def.Name))
            return BadRequest(new ApiResult { Success = false, Message = "Meta has not approved this template yet. An administrator marks it approved in Messages & templates after Meta accepts it." });

        var orderId = (await OrdersForAsync(lead)).FirstOrDefault(Counts)?.Id ?? (await OrdersForAsync(lead)).FirstOrDefault()?.Id;
        var values = new List<string>();
        for (var i = 0; i < def.Params.Length; i++)
        {
            var p = def.Params[i];
            string? v = p.Kind switch
            {
                "name" => LeadService.FirstName(lead.Name, lead.Phone),
                "agent" => MyName,
                "order" => orderId,
                _ => i < r.Params.Count ? r.Params[i] : null,
            };
            v = string.Join(' ', (v ?? "").Split(['\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
            if (v.Length == 0) return BadRequest(new ApiResult { Success = false, Message = p.Kind == "order" ? "This template mentions an order, but this person has no order yet." : $"Fill in: {p.Label}." });
            if (v.Length > 120) return BadRequest(new ApiResult { Success = false, Message = $"“{p.Label}” is too long (120 characters at most)." });
            values.Add(v);
        }

        var ok = await whatsapp.SendTemplateAsync(lead.Phone, def.Name, values, orderId, MyName, def.Filled(values));
        if (ok) log.LogInformation("[Lead] {Who} sent template {Template} to lead {Id}", MyName, def.Name, id);
        return Ok(new ApiResult { Success = ok, Message = ok ? "Template sent. When the customer replies you can message them freely again." : whatsapp.LastError ?? "The template could not be sent. Check that Meta has approved it under this exact name." });
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
            .Select(g => new { Id = g.Key, Total = g.Count(), Open = g.Count(l => l.Status != Nuraherbex.Shared.Models.LeadStatus.Converted && l.Status != Nuraherbex.Shared.Models.LeadStatus.NotInterested) })
            .ToDictionaryAsync(x => x.Id);
        var openTasks = await db.WorkTasks.AsNoTracking().Where(t => t.AssignedToEmployeeId != null && t.Status != WorkTaskStatus.Done)
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
        if (existing is { Role: EmployeeRole.Admin } && !IsAdmin) return "Only an administrator can change an administrator's account.";
        if (r.DepartmentId is { } d && !await db.Departments.AnyAsync(x => x.Id == d && x.IsActive)) return "That department is not available.";
        if (r.DesignationId is { } g)
        {
            var des = await db.Designations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == g && x.IsActive);
            if (des is null) return "That designation is not available.";
            if (!IsAdmin)
                foreach (var (page, level) in PageCatalog.FromJson(des.RightsJson))
                    if (level > Level(page)) return $"That designation gives more access to {PageCatalog.Find(page)?.Label ?? page} than you have yourself.";
        }
        if (existing is not null && existing.Id == MyId && !IsAdmin && (existing.DesignationId != r.DesignationId || existing.Role.ToCode() != r.Role))
            return "You cannot change your own designation or role.";
        return null;
    }

    /// <summary>
    /// An administrator (or anyone with Team rights) switches a person's "WhatsApp active" on or off. On needs the person to be signed in
    /// (otherwise the system would switch them straight back off); off moves their not-yet-started leads to other active people.
    /// </summary>
    [HttpPut("employees/{id:long}/whatsapp")]
    public async Task<IActionResult> SetEmployeeWhatsApp(long id, WhatsAppActiveRequest r, [FromServices] StaffAvailability presence)
    {
        var emp = await db.Employees.FirstOrDefaultAsync(e => e.Id == id);
        if (emp is null) return NotFound(new ApiResult { Success = false, Message = "Employee not found" });
        if (r.Active)
        {
            if (!emp.IsActive) return BadRequest(new ApiResult { Success = false, Message = $"{emp.Name} is not an active employee." });
            if (emp.Role != EmployeeRole.Sales) return BadRequest(new ApiResult { Success = false, Message = "Only sales staff receive WhatsApp leads." });
            if (!StaffAvailability.IsOnline(emp)) return BadRequest(new ApiResult { Success = false, Message = $"{emp.Name} is not signed in right now, so they cannot be set active. They can switch it on themselves when they sign in." });
            emp.WhatsAppActive = true;
            await db.SaveChangesAsync();
            var given = await leads.AssignWaitingAsync();
            return Ok(new ApiResult { Success = true, Message = given > 0 ? $"{emp.Name} is WhatsApp active. {given} waiting lead(s) were given to them." : $"{emp.Name} is WhatsApp active." });
        }
        var moved = await leads.SwitchOffWhatsAppAsync(id, $"WhatsApp switched off by {MyName}", MyActorId);
        return Ok(new ApiResult { Success = true, Message = moved > 0 ? $"{emp.Name} is WhatsApp off. {moved} pending lead(s) moved to other active people, or back to the admin queue if nobody else is active." : $"{emp.Name} is WhatsApp off." });
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
            Name = r.Name.Trim(), Email = email, Phone = (r.Phone ?? "").Trim(), Role = EmployeeRoles.TryParse(r.Role, out var parsedRole) ? parsedRole : EmployeeRole.Sales, IsActive = r.IsActive,
            PasswordHash = AdminPassword.Hash(r.Password!), DepartmentId = r.DepartmentId, DesignationId = r.DesignationId,
        };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();
        RightsService.Invalidate();
        log.LogInformation("[Staff] {Admin} created {Role} employee {Email}", MyName, emp.Role.ToCode(), emp.Email);
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
        emp.Name = r.Name.Trim(); emp.Email = email; emp.Phone = (r.Phone ?? "").Trim(); emp.Role = EmployeeRoles.TryParse(r.Role, out var parsedRole) ? parsedRole : EmployeeRole.Sales; emp.IsActive = r.IsActive;
        emp.DepartmentId = r.DepartmentId; emp.DesignationId = r.DesignationId;
        if (!string.IsNullOrEmpty(r.Password)) emp.PasswordHash = AdminPassword.Hash(r.Password);
        await db.SaveChangesAsync();
        RightsService.Invalidate();

        // A deactivated person, or one who can no longer work leads, must not keep leads silently.
        var moved = 0;
        var canWorkLeads = emp.IsActive && emp.Role == EmployeeRole.Sales
            && (await rightsService.EmployeesWithPageAsync("leads")).Concat(await rightsService.EmployeesWithPageAsync("interested")).Concat(await rightsService.EmployeesWithPageAsync("care")).Contains(emp.Id);
        if ((wasActive && !emp.IsActive) || !canWorkLeads)
            moved = await leads.ReassignOpenLeadsAsync(emp.Id, spreadAcrossTeam: true, MyActorId);
        return Ok(new EmployeeResponse { Success = true, Message = moved > 0 ? $"Employee updated; {moved} open lead(s) were reassigned." : "Employee updated", Employee = emp.ToDto() });
    }

    /// <summary>Removing an employee deactivates the login (history stays intact), moves their open leads and returns their open tasks to unassigned.</summary>
    [HttpDelete("employees/{id:long}")]
    public async Task<IActionResult> RemoveEmployee(long id, [FromQuery] string? reassign)
    {
        var emp = await db.Employees.FirstOrDefaultAsync(e => e.Id == id);
        if (emp is null) return NotFound(new ApiResult { Success = false, Message = "Employee not found" });
        if (id == MyId) return BadRequest(new ApiResult { Success = false, Message = "You cannot remove your own account." });
        if (emp.Role == EmployeeRole.Admin && !IsAdmin) return BadRequest(new ApiResult { Success = false, Message = "Only an administrator can remove an administrator." });

        emp.IsActive = false;
        foreach (var t in await db.WorkTasks.Where(t => t.AssignedToEmployeeId == id && t.Status != WorkTaskStatus.Done).ToListAsync())
        {
            t.AssignedToEmployeeId = null;
            db.TaskActivities.Add(new TaskActivity { TaskId = t.Id, Actor = MyName, Text = $"Left unassigned because {emp.Name} was removed" });
        }
        if (await db.Departments.FirstOrDefaultAsync(d => d.HeadEmployeeId == id) is { } headOf) headOf.HeadEmployeeId = null;
        await db.SaveChangesAsync();
        RightsService.Invalidate();
        var moved = await leads.ReassignOpenLeadsAsync(emp.Id, spreadAcrossTeam: !string.Equals(reassign, "unassigned", StringComparison.OrdinalIgnoreCase), MyActorId);
        log.LogInformation("[Staff] {Admin} removed employee {Email}; {Moved} lead(s) reassigned", MyName, emp.Email, moved);
        return Ok(new ApiResult { Success = true, Message = moved > 0 ? $"{emp.Name} removed; {moved} open lead(s) were reassigned." : $"{emp.Name} removed." });
    }

    /// <summary>The message library: automatic replies and ready-made replies per stage. Everyone who works leads or customers may read it; only Team rights change it.</summary>
    [HttpGet("leads/templates")]
    public async Task<MessageTemplatesResponse> GetTemplates([FromServices] MessageTemplateService templates) => new() { Success = true, Settings = await templates.GetAsync() };

    [HttpPut("leads/templates")]
    public async Task<IActionResult> SaveTemplates(MessageTemplatesSettings r, [FromServices] MessageTemplateService templates)
    {
        if (await templates.SaveAsync(r) is { } error) return BadRequest(new ApiResult { Success = false, Message = error });
        return Ok(new ApiResult { Success = true, Message = "Messages saved." });
    }

    [HttpPut("leads/settings")]
    public async Task<IActionResult> LeadSettings(LeadSettingsRequest r)
    {
        await leads.SetAutoAssignAsync(r.AutoAssign);
        return Ok(new ApiResult { Success = true, Message = r.AutoAssign ? "New leads go to whoever is signed in and has the least open work." : "New leads will wait in the admin queue." });
    }

    private static string? Validate(EmployeeSaveRequest r, bool creating)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) return "Name is required.";
        if (!string.IsNullOrWhiteSpace(r.Email) && !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(r.Email.Trim())) return "A valid email is required.";
        if (!EmployeeRoles.TryParse(r.Role, out _)) return "Role must be admin or sales.";
        if (creating && (r.Password is null || r.Password.Length < 8)) return "Password must be at least 8 characters.";
        if (!creating && !string.IsNullOrEmpty(r.Password) && r.Password.Length < 8) return "Password must be at least 8 characters.";
        return null;
    }
}
