using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Nuraherbex.Api.Data;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Services;

/// <summary>Builds the business reports (orders, leads, follow-ups, WhatsApp, tasks, customers, payments) as plain tables plus a short summary.</summary>
public class ReportService(NuraDbContext db, WhatsAppService whatsapp)
{
    private const int MaxRows = 20000;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static readonly ReportInfo[] Catalog =
    [
        new() { Key = "orders", Title = "Orders", Description = "Every order with customer, items, amounts, payment and delivery status." },
        new() { Key = "leads", Title = "Leads", Description = "New leads with source, status, owner and conversion." },
        new() { Key = "followups", Title = "Follow-ups", Description = "Calls and messages due: who did them, what is late, and what was sent on WhatsApp." },
        new() { Key = "whatsapp", Title = "WhatsApp messages", Description = "Every message in and out with delivery status and estimated cost." },
        new() { Key = "tasks", Title = "Tasks", Description = "Team tasks by owner, department, priority and status." },
        new() { Key = "customers", Title = "Customers", Description = "Customer accounts with orders placed and total spent." },
        new() { Key = "payments", Title = "Online payments", Description = "Online payment attempts and their outcome." },
    ];

    private static string Ist(DateTime? utc) => utc is null ? "" : FollowUpService.ToIst(utc.Value).ToString("yyyy-MM-dd HH:mm", Inv);
    private static string Money(decimal v) => "₹" + v.ToString("N0", Inv);
    private static string Money2(decimal v) => "₹" + v.ToString("N2", Inv);
    private static string Pct(int part, int whole) => whole == 0 ? "0%" : Math.Round(100.0 * part / whole).ToString(Inv) + "%";
    private static string Clip(string? s, int n) => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…").Replace("\r", " ").Replace("\n", " ");
    private static ReportStat S(string label, object value, string? hint = null, string tone = "neutral") => new() { Label = label, Value = Convert.ToString(value, Inv) ?? "", Hint = hint, Tone = tone };

    /// <param name="ownerOnly">When set, leads, follow-ups and tasks are limited to this employee's own records.</param>
    public async Task<ReportResponse?> BuildAsync(string key, DateTime fromUtc, DateTime toUtc, string periodLabel, long? ownerOnly = null)
    {
        var normKey = key.Trim().ToLowerInvariant();
        var r = normKey switch
        {
            "orders" => await OrdersAsync(fromUtc, toUtc),
            "leads" => await LeadsAsync(fromUtc, toUtc, ownerOnly),
            "followups" => await FollowUpsAsync(fromUtc, toUtc, ownerOnly),
            "whatsapp" => await WhatsAppAsync(fromUtc, toUtc),
            "tasks" => await TasksAsync(fromUtc, toUtc, ownerOnly),
            "customers" => await CustomersAsync(fromUtc, toUtc),
            "payments" => await PaymentsAsync(fromUtc, toUtc),
            "employee-performance" or "employee_performance" or "employee_report" or "employee-report" => await BuildEmployeePerformanceReportAsync(fromUtc, toUtc, periodLabel, ownerOnly),
            _ => null,
        };
        if (r is null) return null;
        var info = Catalog.FirstOrDefault(c => c.Key == normKey) ?? new ReportInfo { Key = normKey, Title = "Employee Performance Report", Description = "Leads completed, follow-ups done, and no-answer follow-ups tracked date-wise." };
        r.Success = true; r.Key = normKey; r.Title = info.Title; r.Description = info.Description; r.Period = periodLabel;
        r.Truncated = r.Rows.Count >= MaxRows;
        return r;
    }

    private async Task<ReportResponse> BuildEmployeePerformanceReportAsync(DateTime from, DateTime to, string periodLabel, long? employeeId)
    {
        var perf = await EmployeePerformanceAsync(from, to, periodLabel, employeeId);
        return new ReportResponse
        {
            Columns = ["Employee", "Role", "Designation", "Total Leads", "Orders Booked", "Follow-ups Done", "No Answer Follow-up", "Hot Leads (Interested)", "Not Interested", "Pending", "Overdue", "Win Rate %"],
            Rows = perf.ByEmployee.Select(e => new List<string>
            {
                e.Name,
                e.Role,
                e.Designation,
                e.TotalLeads.ToString(Inv),
                e.LeadsConverted.ToString(Inv),
                e.FollowUpsDone.ToString(Inv),
                e.NoAnswerCount.ToString(Inv),
                e.InterestedCount.ToString(Inv),
                e.NotInterestedCount.ToString(Inv),
                e.PendingFollowUps.ToString(Inv),
                e.OverdueFollowUps.ToString(Inv),
                $"{e.WinRatePercent}%"
            }).ToList(),
            Summary =
            [
                S("Orders Booked", perf.TotalLeadsConverted, "Customers placed orders in this period", "good"),
                S("Follow-ups Done", perf.TotalFollowUpsDone, "Completed call / message tasks", "good"),
                S("Hot Leads", perf.TotalInterestedCount, "Customers interested to buy", "good"),
                S("No Answer", perf.TotalNoAnswerCount, "Calls or messages with no answer", "warn"),
                S("Not Interested", perf.TotalNotInterestedCount, "Customers not interested", "neutral"),
                S("Active Staff", perf.ByEmployee.Count, "Employees in this report period", "neutral")
            ]
        };
    }

    // ---- Orders ---------------------------------------------------------------------------------

    private async Task<ReportResponse> OrdersAsync(DateTime from, DateTime to)
    {
        var orders = await db.Orders.AsNoTracking().Where(o => o.CreatedAt >= from && o.CreatedAt < to).OrderByDescending(o => o.CreatedAt).Take(MaxRows).ToListAsync();
        var res = new ReportResponse
        {
            Columns = ["Order ID", "Date", "Customer", "Phone", "City", "State", "Items", "Qty", "Subtotal", "Discount", "Shipping", "Total", "Payment", "Payment status", "Fulfilment", "Delivery status", "Courier", "AWB"],
        };
        foreach (var o in orders)
            res.Rows.Add([o.Id, Ist(o.CreatedAt), o.CustomerName, o.CustomerPhone, o.ShippingAddress.City, o.ShippingAddress.State,
                string.Join("; ", o.Items.Select(i => $"{i.Name} x{i.Quantity}")), o.Items.Sum(i => i.Quantity).ToString(Inv),
                Money(o.Subtotal), Money(o.DiscountAmount), Money(o.ShippingFee), Money(o.TotalAmount), o.PaymentMethod.ToCode(), o.PaymentStatus.ToCode(),
                o.FulfillmentStatus, o.DeliveryStatus, o.ShiprocketCourier ?? o.Courier ?? "", o.ShiprocketAwb ?? ""]);
        var paid = orders.Where(o => o.PaymentStatus is OrderPaymentStatus.Paid or OrderPaymentStatus.CodCollected).ToList();
        var delivered = orders.Count(o => o.FulfillmentStatus == "DELIVERED");
        var lost = orders.Count(o => o.FulfillmentStatus is "RTO" or "CANCELLED");
        var units = orders.SelectMany(o => o.Items).GroupBy(i => i.Name).Select(g => new { g.Key, Qty = g.Sum(i => i.Quantity) }).OrderByDescending(x => x.Qty).FirstOrDefault();
        res.Summary =
        [
            S("Orders", orders.Count), S("Revenue collected", Money(paid.Sum(o => o.TotalAmount)), "Paid online or COD collected", "good"),
            S("COD awaiting", Money(orders.Where(o => o.PaymentStatus == OrderPaymentStatus.CodPending).Sum(o => o.TotalAmount)), "Cash on delivery not yet collected", "warn"),
            S("Average order", Money(orders.Count == 0 ? 0 : orders.Average(o => o.TotalAmount))),
            S("Delivered", $"{delivered} ({Pct(delivered, orders.Count)})", null, "good"),
            S("Returned or cancelled", $"{lost} ({Pct(lost, orders.Count)})", null, lost > 0 ? "bad" : "neutral"),
            S("Prepaid / COD", $"{orders.Count(o => o.PaymentMethod != OrderPaymentMethod.Cod)} / {orders.Count(o => o.PaymentMethod == OrderPaymentMethod.Cod)}"),
            S("Best seller", units is null ? "None" : $"{units.Key} ({units.Qty})"),
        ];
        return res;
    }

    // ---- Leads ----------------------------------------------------------------------------------

    private async Task<ReportResponse> LeadsAsync(DateTime from, DateTime to, long? ownerOnly)
    {
        var owners = await db.Employees.AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.Name);
        // Same definition as the CRM dashboard: people who enquired in the period; records made only because an order was placed are not new leads.
        var periodQ = db.Leads.AsNoTracking().Where(l => l.CreatedAt >= from && l.CreatedAt < to && (ownerOnly == null || l.AssignedToEmployeeId == ownerOnly)).Where(LeadService.IsNewEnquiry);
        var leads = await periodQ.OrderByDescending(l => l.CreatedAt).Take(MaxRows).ToListAsync();
        // Summary figures come from the database, so they stay right when the period holds more leads than the sheet has rows.
        var total = await periodQ.CountAsync();
        var byStatus = (await periodQ.GroupBy(l => l.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync()).ToDictionary(x => x.Key, x => x.N);
        var adLeads = await periodQ.CountAsync(l => db.LeadSourceEvents.Any(e => e.LeadId == l.Id && e.ReferralJson != null));
        var noOwner = await periodQ.CountAsync(l => l.AssignedToEmployeeId == null);
        var bySource = (await periodQ.GroupBy(l => l.Source).Select(g => new { g.Key, N = g.Count() }).ToListAsync()).OrderByDescending(x => x.N).FirstOrDefault();
        // "No answer" includes both formal LeadStatus.NoAnswer and legacy New/Follow-up leads whose last finished follow-up got no answer.
        var naNew = await periodQ.Where(l => l.Status == LeadStatus.New).CountAsync(LeadService.NoAnswerOf(db, true));
        var naFollow = await periodQ.Where(l => l.Status == LeadStatus.FollowUp).CountAsync(LeadService.NoAnswerOf(db, true));
        var naIds = (await periodQ.Where(l => l.Status == LeadStatus.New || l.Status == LeadStatus.FollowUp).Where(LeadService.NoAnswerOf(db, true)).Select(l => l.Id).Take(MaxRows).ToListAsync()).ToHashSet();
        var ids = leads.Select(l => l.Id).ToList();
        // The newest comment staff left when changing the lead's status, plus how many follow-ups are done.
        var notes = (await db.LeadStatusHistory.AsNoTracking().Where(h => ids.Contains(h.LeadId) && h.Note != null && h.Note != "").OrderByDescending(h => h.CreatedAt).ToListAsync())
            .GroupBy(h => h.LeadId).ToDictionary(g => g.Key, g => g.First());
        var fu = await db.FollowUps.AsNoTracking().Where(f => ids.Contains(f.LeadId)).GroupBy(f => f.LeadId).Select(g => new { g.Key, Total = g.Count(), Done = g.Count(f => f.Status == FollowUpStatus.Done) }).ToDictionaryAsync(x => x.Key);
        // Where each lead stands with follow-ups: the result of the last finished one, and the next one booked.
        var lastResult = (await db.FollowUps.AsNoTracking().Where(f => f.Status == FollowUpStatus.Done && f.Outcome != null && ids.Contains(f.LeadId))
                .OrderByDescending(f => f.CompletedAt).ThenByDescending(f => f.Id).Select(f => new { f.LeadId, f.Outcome }).ToListAsync())
            .GroupBy(x => x.LeadId).ToDictionary(g => g.Key, g => g.First().Outcome!);
        var nextDue = (await db.FollowUps.AsNoTracking().Where(f => f.Status == FollowUpStatus.Pending && ids.Contains(f.LeadId)).GroupBy(f => f.LeadId).Select(g => new { g.Key, At = g.Min(x => x.DueAt) }).ToListAsync())
            .ToDictionary(x => x.Key, x => x.At);
        // "Status" shows what the Leads tabs show: no-answer for LeadStatus.NoAnswer or legacy leads whose last follow-up got no answer.
        string StatusOf(Lead l) => (l.Status == LeadStatus.NoAnswer || naIds.Contains(l.Id)) ? LeadStatuses.NoAnswer : l.Status.ToCode();
        var res = new ReportResponse { Columns = ["Lead ID", "Created", "Name", "Phone", "Email", "Source", "Campaign / detail", "Status", "Owner", "Notes", "Latest comment", "Follow-ups", "Last contact", "Converted on", "Consent", "First message", "Last follow-up result", "Next follow-up"] };
        foreach (var l in leads)
        {
            notes.TryGetValue(l.Id, out var h);
            fu.TryGetValue(l.Id, out var f);
            res.Rows.Add([l.Id.ToString(Inv), Ist(l.CreatedAt), l.Name, l.Phone, l.Email ?? "", l.Source.ToCode(), l.SourceDetail ?? "", StatusOf(l),
                l.AssignedToEmployeeId is { } id ? owners.GetValueOrDefault(id) ?? "" : "Unassigned", Clip(l.Notes, 300),
                h is null ? "" : $"{Clip(h.Note, 300)} ({Ist(h.CreatedAt)})", f is null ? "" : $"{f.Done} of {f.Total} done", Ist(l.LastContactAt), Ist(l.ConvertedAt), l.ConsentStatus, Clip(l.FirstMessage, 160),
                lastResult.GetValueOrDefault(l.Id) is { } lr ? Clip(lr, 160) : "", nextDue.TryGetValue(l.Id, out var nd) ? Ist(nd) : ""]);
        }
        int N(LeadStatus s) => byStatus.GetValueOrDefault(s);
        var won = N(LeadStatus.Converted);
        var noAnswerTotal = N(LeadStatus.NoAnswer) + naNew + naFollow;
        // Every lead is in exactly one of: New, In follow-up, No answer, Interested, Not interested, Converted.
        res.Summary =
        [
            S("Leads in this period", total, "Everyone who first enquired in the period; orders placed without an enquiry are not counted"),
            S("From Meta ads", adLeads, "Arrived through a Click-to-WhatsApp ad: compare this with Meta"),
            S("New, not started", N(LeadStatus.New) - naNew, "Nobody has worked on them yet"),
            S("In follow-up", N(LeadStatus.FollowUp) - naFollow, "Being chased"),
            S("No answer", noAnswerTotal, "The last call or message got no answer; a retry is booked", noAnswerTotal > 0 ? "warn" : "neutral"),
            S("Interested", N(LeadStatus.Interested), "Hot leads"),
            S("Not interested", N(LeadStatus.NotInterested), "Closed for now"),
            S("Converted", $"{won} ({Pct(won, total)})", "Placed an order; the percentage is of this period's leads", "good"),
            S("Unassigned", noOwner, "Leads with no owner", noOwner > 0 ? "warn" : "neutral"),
            S("Top source", bySource is null ? "None" : $"{bySource.Key.ToCode()} ({bySource.N})"),
        ];
        return res;
    }

    // ---- Follow-ups -----------------------------------------------------------------------------

    private async Task<ReportResponse> FollowUpsAsync(DateTime from, DateTime to, long? ownerOnly)
    {
        var owners = await db.Employees.AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.Name);
        var items = await db.FollowUps.AsNoTracking().Where(f => f.DueAt >= from && f.DueAt < to && (ownerOnly == null || f.AssignedToEmployeeId == ownerOnly)).OrderByDescending(f => f.DueAt).Take(MaxRows).ToListAsync();
        var leadIds = items.Select(i => i.LeadId).Distinct().ToList();
        var leads = await db.Leads.AsNoTracking().Where(l => leadIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => new { l.Name, l.Phone, l.Status });
        var today = FollowUpService.TodayStartUtc();
        var res = new ReportResponse { Columns = ["Due", "Lead", "Phone", "Type", "Task", "Notes", "Owner", "Status", "Completed", "Completed by", "Outcome / comment", "Lead status", "Step", "Source", "Order"] };
        foreach (var f in items)
        {
            leads.TryGetValue(f.LeadId, out var l);
            var status = f.Status == FollowUpStatus.Pending && f.DueAt < today ? "overdue" : f.Status.ToCode();
            res.Rows.Add([Ist(f.DueAt), l?.Name ?? "", l?.Phone ?? "", f.Type, f.Title, Clip(f.Notes, 300), f.AssignedToEmployeeId is { } id ? owners.GetValueOrDefault(id) ?? "" : "Unassigned",
                status, Ist(f.CompletedAt), Mapping.ResolveActor(f.CompletedBy, owners) ?? "", Clip(f.Outcome, 300), l?.Status.ToCode() ?? "", f.StepKey ?? "", f.Source.ToCode(), f.OrderId ?? ""]);
        }
        var done = items.Count(f => f.Status == FollowUpStatus.Done);
        var sent = items.Count(f => f.Status == FollowUpStatus.Done && f.Outcome != null && f.Outcome.StartsWith("Sent"));
        var overdue = items.Count(f => f.Status == FollowUpStatus.Pending && f.DueAt < today);
        res.Summary =
        [
            S("Follow-ups due", items.Count), S("Done", $"{done} ({Pct(done, items.Count)})", null, "good"),
            S("Overdue", overdue, "Still pending after their day", overdue > 0 ? "bad" : "neutral"), S("Pending", items.Count(f => f.Status == FollowUpStatus.Pending) - overdue),
            S("No answer", items.Count(f => f.Status == FollowUpStatus.Done && f.Outcome != null && f.Outcome.StartsWith(FollowUpOutcomes.NoAnswer)), "Calls or messages that got no answer (a retry was booked each time)"),
            S("Not interested", items.Count(f => f.Status == FollowUpStatus.Done && f.Outcome != null && f.Outcome.StartsWith(FollowUpOutcomes.NotInterested)), "Follow-ups that ended with the lead saying no"),
            S("Skipped", items.Count(f => f.Status == FollowUpStatus.Skipped), "Closed without being done, for example replaced by a newer follow-up"), S("WhatsApp messages sent", sent, "Sent from the follow-up list"),
            S("Sent automatically", items.Count(f => f.CompletedBy == "auto")), S("Journey steps", items.Count(f => f.Source == FollowUpSource.Journey)),
        ];
        return res;
    }

    // ---- WhatsApp -------------------------------------------------------------------------------

    private async Task<ReportResponse> WhatsAppAsync(DateTime from, DateTime to)
    {
        var limit = await whatsapp.GetLimitStatusAsync();
        var msgs = await db.WhatsAppMessages.AsNoTracking().Where(m => m.CreatedAt >= from && m.CreatedAt < to).OrderByDescending(m => m.CreatedAt).Take(MaxRows).ToListAsync();
        decimal Cost(WhatsAppMessage m) => m.Direction != "outbound" || m.Status == "failed" ? 0 : m.Type == "template" ? limit.CostTemplate : limit.CostText;
        var res = new ReportResponse { Columns = ["Time", "Direction", "Phone", "Name", "Type", "Status", "Sent by", "Order", "Message", "Est. cost"] };
        foreach (var m in msgs)
            res.Rows.Add([Ist(m.CreatedAt), m.Direction == "inbound" ? "In" : "Out", m.Phone, m.ProfileName ?? "", m.Type, m.Status, m.Direction == "outbound" ? (m.SentBy is null or "system" ? "Automatic" : m.SentBy) : "", m.OrderId ?? "", Clip(m.Body, 140), Money2(Cost(m))]);
        var outbound = msgs.Where(m => m.Direction == "outbound").ToList();
        var sentOk = outbound.Where(m => m.Status != "failed").ToList();
        var cost = msgs.Sum(Cost);
        res.Summary =
        [
            S("Sent", sentOk.Count, "Outgoing messages that were accepted"), S("Received", msgs.Count(m => m.Direction == "inbound")),
            S("Templates / free text", $"{sentOk.Count(m => m.Type == "template")} / {sentOk.Count(m => m.Type != "template")}"),
            S("Delivered", outbound.Count(m => m.Status is "delivered" or "read"), null, "good"), S("Read", outbound.Count(m => m.Status == "read")),
            S("Failed", outbound.Count(m => m.Status == "failed"), "Could not be sent or delivered", outbound.Any(m => m.Status == "failed") ? "bad" : "neutral"),
            S("Estimated cost", Money2(cost), limit.CostTemplate == 0 && limit.CostText == 0 ? "Set message rates on the WhatsApp page to see costs" : "Based on the rates set for templates and free text", cost > 0 ? "warn" : "neutral"),
            S($"This month ({limit.Month})", limit.Limit > 0 ? $"{limit.Used} of {limit.Limit} messages" : $"{limit.Used} messages", limit.Budget > 0 ? $"₹{limit.Spent:N2} of ₹{limit.Budget:N2} budget" : null, limit.Reached ? "bad" : limit.Warning ? "warn" : "neutral"),
        ];
        return res;
    }

    // ---- Tasks ----------------------------------------------------------------------------------

    private async Task<ReportResponse> TasksAsync(DateTime from, DateTime to, long? ownerOnly)
    {
        var people = await db.Employees.AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.Name);
        var depts = await db.Departments.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Name);
        var tasks = await db.WorkTasks.AsNoTracking().Where(t => t.CreatedAt >= from && t.CreatedAt < to && (ownerOnly == null || t.AssignedToEmployeeId == ownerOnly || t.CreatedByEmployeeId == ownerOnly)).OrderByDescending(t => t.CreatedAt).Take(MaxRows).ToListAsync();
        var taskIds = tasks.Select(t => t.Id).ToList();
        var checks = await db.TaskChecklistItems.AsNoTracking().Where(i => taskIds.Contains(i.TaskId)).GroupBy(i => i.TaskId).Select(g => new { g.Key, Total = g.Count(), Done = g.Count(i => i.Done) }).ToDictionaryAsync(x => x.Key);
        var lastComment = (await db.TaskComments.AsNoTracking().Where(c => taskIds.Contains(c.TaskId)).OrderByDescending(c => c.CreatedAt).ToListAsync()).GroupBy(c => c.TaskId).ToDictionary(g => g.Key, g => g.First());
        var now = FollowUpService.Clock();
        // Full audit trail per task, oldest first: every logged change (created, edits, status moves, checklist steps added / ticked / unticked / removed) and every comment.
        var activities = await db.TaskActivities.AsNoTracking().Where(a => taskIds.Contains(a.TaskId)).ToListAsync();
        var commentsAll = await db.TaskComments.AsNoTracking().Where(c => taskIds.Contains(c.TaskId)).ToListAsync();
        var trail = activities.Select(a => (a.TaskId, a.CreatedAt, Line: new ReportDetailLine { Actor = a.Actor, Text = a.Text, Kind = "activity" }))
            .Concat(commentsAll.Select(c => (c.TaskId, c.CreatedAt, Line: new ReportDetailLine { Actor = c.Author, Text = c.Body, Kind = "comment" })))
            .GroupBy(x => x.TaskId).ToDictionary(g => g.Key, g => g.OrderBy(x => x.CreatedAt).Select(x => { x.Line.When = Ist(x.CreatedAt); return x.Line; }).ToList());
        var res = new ReportResponse { Columns = ["Task ID", "Title", "Department", "Assigned to", "Created by", "Priority", "Status", "Checklist", "Latest comment", "Created", "Due", "Completed", "Late", "Audit trail"] };
        foreach (var t in tasks)
        {
            var late = t.Status != WorkTaskStatus.Done && t.DueAt is { } d && d < now;
            res.Rows.Add([t.Id.ToString(Inv), t.Title, t.DepartmentId is { } di ? depts.GetValueOrDefault(di) ?? "" : "", t.AssignedToEmployeeId is { } a ? people.GetValueOrDefault(a) ?? "" : "Unassigned",
                t.CreatedByName, t.Priority.ToCode(), t.Status.ToCode(), checks.TryGetValue(t.Id, out var ck) ? $"{ck.Done} of {ck.Total}" : "",
                lastComment.TryGetValue(t.Id, out var lc) ? $"{lc.Author}: {Clip(lc.Body, 300)}" : "", Ist(t.CreatedAt), Ist(t.DueAt), Ist(t.CompletedAt), late ? "Yes" : "",
                trail.TryGetValue(t.Id, out var tl) ? string.Join("\n", tl.Select(x => $"{x.When} · {x.Actor}: {Clip(x.Text, 400)}")) : ""]);
            if (trail.TryGetValue(t.Id, out var lines)) res.Details[t.Id.ToString(Inv)] = lines;
        }
        var doneCount = tasks.Count(t => t.Status == WorkTaskStatus.Done);
        var overdue = tasks.Count(t => t.Status != WorkTaskStatus.Done && t.DueAt is { } d && d < now);
        res.Summary =
        [
            S("Tasks created", tasks.Count), S("Done", $"{doneCount} ({Pct(doneCount, tasks.Count)})", null, "good"),
            S("In progress", tasks.Count(t => t.Status == WorkTaskStatus.InProgress)), S("Blocked", tasks.Count(t => t.Status == WorkTaskStatus.Blocked), null, tasks.Any(t => t.Status == WorkTaskStatus.Blocked) ? "warn" : "neutral"),
            S("Overdue", overdue, null, overdue > 0 ? "bad" : "neutral"), S("Urgent or high", tasks.Count(t => t.Priority is WorkTaskPriority.Urgent or WorkTaskPriority.High)),
            S("Unassigned", tasks.Count(t => t.AssignedToEmployeeId is null)),
        ];
        return res;
    }

    // ---- Customers ------------------------------------------------------------------------------

    private async Task<ReportResponse> CustomersAsync(DateTime from, DateTime to)
    {
        var customers = await db.Customers.AsNoTracking().Where(c => c.CreatedAt >= from && c.CreatedAt < to).OrderByDescending(c => c.CreatedAt).Take(MaxRows).ToListAsync();
        var ids = customers.Select(c => c.Id).ToList();
        var spend = await db.Orders.AsNoTracking().Where(o => o.CustomerId != null && ids.Contains(o.CustomerId)).Select(o => new { o.CustomerId, o.TotalAmount, o.PaymentStatus }).ToListAsync();
        var byCustomer = spend.GroupBy(s => s.CustomerId!).ToDictionary(g => g.Key, g => (Count: g.Count(), Total: g.Where(x => x.PaymentStatus is OrderPaymentStatus.Paid or OrderPaymentStatus.CodCollected).Sum(x => x.TotalAmount)));
        var res = new ReportResponse { Columns = ["Customer ID", "Name", "Email", "Phone", "City", "State", "Joined", "Orders", "Total spent"] };
        foreach (var c in customers)
        {
            byCustomer.TryGetValue(c.Id, out var s);
            res.Rows.Add([c.Id, c.FullName, c.Email, c.Phone, c.ShippingAddress.City, c.ShippingAddress.State, Ist(c.CreatedAt), s.Count.ToString(Inv), Money(s.Total)]);
        }
        res.Summary =
        [
            S("New customers", customers.Count), S("Placed an order", byCustomer.Count, null, "good"),
            S("Total spent", Money(byCustomer.Values.Sum(v => v.Total))), S("Repeat buyers", byCustomer.Values.Count(v => v.Count > 1)),
        ];
        return res;
    }

    // ---- Payments -------------------------------------------------------------------------------

    private async Task<ReportResponse> PaymentsAsync(DateTime from, DateTime to)
    {
        var rows = await db.PaymentAttempts.AsNoTracking().Where(p => p.CreatedAt >= from && p.CreatedAt < to).OrderByDescending(p => p.CreatedAt).Take(MaxRows).ToListAsync();
        var res = new ReportResponse { Columns = ["Time", "Order ID", "Customer", "Phone", "Amount", "Status", "Mode", "Transaction", "Failure reason"] };
        foreach (var p in rows)
            res.Rows.Add([Ist(p.CreatedAt), p.OrderId, p.CustomerName, p.CustomerPhone, Money(p.Amount), p.Status, p.PaymentMode ?? "", p.TxnId ?? "", Clip(p.FailureReason, 120)]);
        var ok = rows.Where(p => p.Status == "SUCCESS").ToList();
        var failed = rows.Count(p => p.Status is "FAILED" or "CANCELLED");
        res.Summary =
        [
            S("Attempts", rows.Count), S("Successful", $"{ok.Count} ({Pct(ok.Count, rows.Count)})", null, "good"), S("Amount received", Money(ok.Sum(p => p.Amount)), null, "good"),
            S("Failed or cancelled", failed, null, failed > 0 ? "warn" : "neutral"), S("Lost amount", Money(rows.Where(p => p.Status is "FAILED" or "CANCELLED").Sum(p => p.Amount)), "Value of unpaid attempts", failed > 0 ? "warn" : "neutral"),
        ];
        return res;
    }

    // ---- Employee Performance (Leads won, Follow-ups done, No-answer count, Date-wise) --------

    public async Task<EmployeePerformanceReportResponse> EmployeePerformanceAsync(DateTime fromUtc, DateTime toUtc, string periodLabel, long? employeeId = null)
    {
        var empsQuery = db.Employees.AsNoTracking().Where(e => e.IsActive);
        if (employeeId is { } eid)
        {
            empsQuery = empsQuery.Where(e => e.Id == eid);
        }
        var employees = await empsQuery.OrderBy(e => e.Name).ToListAsync();
        var empIds = employees.Select(e => e.Id).ToList();

        // 1. Leads assigned to each employee
        var totalLeadsMap = await db.Leads.AsNoTracking()
            .Where(l => l.AssignedToEmployeeId != null && empIds.Contains(l.AssignedToEmployeeId.Value))
            .GroupBy(l => l.AssignedToEmployeeId!.Value)
            .Select(g => new { EmployeeId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.EmployeeId, x => x.Count);

        // 2. Leads converted within period (Leads ethana mudichirukanga)
        var convertedLeads = await db.Leads.AsNoTracking()
            .Where(l => l.ConvertedAt >= fromUtc && l.ConvertedAt < toUtc && l.AssignedToEmployeeId != null && empIds.Contains(l.AssignedToEmployeeId.Value))
            .Select(l => new { l.Id, EmployeeId = l.AssignedToEmployeeId!.Value, l.ConvertedAt })
            .ToListAsync();

        var convertedByEmp = convertedLeads
            .GroupBy(l => l.EmployeeId)
            .ToDictionary(g => g.Key, g => g.Count());

        // 3. Follow-ups completed within period (Connected / Responded only - No answer excluded)
        var doneFollowUps = await db.FollowUps.AsNoTracking()
            .Where(f => f.Status == FollowUpStatus.Done && f.CompletedAt >= fromUtc && f.CompletedAt < toUtc && f.AssignedToEmployeeId != null && empIds.Contains(f.AssignedToEmployeeId.Value))
            .Select(f => new { f.Id, EmployeeId = f.AssignedToEmployeeId!.Value, f.Outcome, f.CompletedAt })
            .ToListAsync();

        // Follow-ups Done: No answer varakoodathu; call panni reply pannavanga (connected calls) mattum thaan varanum
        var doneByEmp = doneFollowUps
            .Where(f => !IsNoAnswer(f.Outcome))
            .GroupBy(f => f.EmployeeId)
            .ToDictionary(g => g.Key, g => g.Count());

        // 4. Outcomes breakdown within period
        var noAnswerByEmp = doneFollowUps
            .Where(f => IsNoAnswer(f.Outcome))
            .GroupBy(f => f.EmployeeId)
            .ToDictionary(g => g.Key, g => g.Count());

        var interestedByEmp = doneFollowUps
            .Where(f => IsInterested(f.Outcome))
            .GroupBy(f => f.EmployeeId)
            .ToDictionary(g => g.Key, g => g.Count());

        var notInterestedByEmp = doneFollowUps
            .Where(f => IsNotInterested(f.Outcome))
            .GroupBy(f => f.EmployeeId)
            .ToDictionary(g => g.Key, g => g.Count());

        // 5. Pending and Overdue follow-ups
        var now = FollowUpService.Clock();
        var openFollowUps = await db.FollowUps.AsNoTracking()
            .Where(f => f.Status == FollowUpStatus.Pending && f.AssignedToEmployeeId != null && empIds.Contains(f.AssignedToEmployeeId.Value))
            .Select(f => new { EmployeeId = f.AssignedToEmployeeId!.Value, IsOverdue = f.DueAt < now })
            .ToListAsync();

        var pendingByEmp = openFollowUps
            .GroupBy(f => f.EmployeeId)
            .ToDictionary(g => g.Key, g => g.Count(x => !x.IsOverdue));

        var overdueByEmp = openFollowUps
            .GroupBy(f => f.EmployeeId)
            .ToDictionary(g => g.Key, g => g.Count(x => x.IsOverdue));

        var byEmployee = new List<EmployeePerformanceDto>();
        foreach (var emp in employees)
        {
            var assigned = totalLeadsMap.GetValueOrDefault(emp.Id, 0);
            var won = convertedByEmp.GetValueOrDefault(emp.Id, 0);
            var fuDone = doneByEmp.GetValueOrDefault(emp.Id, 0);
            var naCount = noAnswerByEmp.GetValueOrDefault(emp.Id, 0);
            var intCount = interestedByEmp.GetValueOrDefault(emp.Id, 0);
            var notIntCount = notInterestedByEmp.GetValueOrDefault(emp.Id, 0);
            var pend = pendingByEmp.GetValueOrDefault(emp.Id, 0);
            var over = overdueByEmp.GetValueOrDefault(emp.Id, 0);
            var winRate = assigned > 0 ? Math.Round((decimal)won * 100m / assigned, 1) : 0m;

            byEmployee.Add(new EmployeePerformanceDto
            {
                EmployeeId = emp.Id,
                Name = emp.Name,
                Role = emp.Role.ToString(),
                Designation = emp.Role == EmployeeRole.Sales ? "Sales Executive" : emp.Role.ToString(),
                TotalLeads = assigned,
                LeadsConverted = won,
                FollowUpsDone = fuDone,
                NoAnswerCount = naCount,
                InterestedCount = intCount,
                NotInterestedCount = notIntCount,
                PendingFollowUps = pend,
                OverdueFollowUps = over,
                WinRatePercent = winRate,
            });
        }

        // 6. Daily Breakdown (Date-wise)
        var empNames = employees.ToDictionary(e => e.Id, e => e.Name);
        var dailyList = new List<EmployeeDailyPerformanceDto>();

        var convByDateEmp = convertedLeads
            .GroupBy(l => (Date: FollowUpService.ToIst(l.ConvertedAt!.Value).ToString("yyyy-MM-dd"), l.EmployeeId))
            .ToDictionary(g => g.Key, g => g.Count());

        var doneByDateEmp = doneFollowUps
            .Where(f => f.CompletedAt != null && !IsNoAnswer(f.Outcome))
            .GroupBy(f => (Date: FollowUpService.ToIst(f.CompletedAt!.Value).ToString("yyyy-MM-dd"), f.EmployeeId))
            .ToDictionary(g => g.Key, g => g.Count());

        var naByDateEmp = doneFollowUps
            .Where(f => f.CompletedAt != null && IsNoAnswer(f.Outcome))
            .GroupBy(f => (Date: FollowUpService.ToIst(f.CompletedAt!.Value).ToString("yyyy-MM-dd"), f.EmployeeId))
            .ToDictionary(g => g.Key, g => g.Count());

        var intByDateEmp = doneFollowUps
            .Where(f => f.CompletedAt != null && IsInterested(f.Outcome))
            .GroupBy(f => (Date: FollowUpService.ToIst(f.CompletedAt!.Value).ToString("yyyy-MM-dd"), f.EmployeeId))
            .ToDictionary(g => g.Key, g => g.Count());

        var notIntByDateEmp = doneFollowUps
            .Where(f => f.CompletedAt != null && IsNotInterested(f.Outcome))
            .GroupBy(f => (Date: FollowUpService.ToIst(f.CompletedAt!.Value).ToString("yyyy-MM-dd"), f.EmployeeId))
            .ToDictionary(g => g.Key, g => g.Count());

        var allDateEmpKeys = convByDateEmp.Keys
            .Union(doneByDateEmp.Keys)
            .Union(naByDateEmp.Keys)
            .Union(intByDateEmp.Keys)
            .Union(notIntByDateEmp.Keys)
            .OrderByDescending(k => k.Date)
            .ThenBy(k => empNames.GetValueOrDefault(k.EmployeeId, ""))
            .ToList();

        foreach (var k in allDateEmpKeys)
        {
            dailyList.Add(new EmployeeDailyPerformanceDto
            {
                Date = k.Date,
                EmployeeId = k.EmployeeId,
                EmployeeName = empNames.GetValueOrDefault(k.EmployeeId, $"Employee #{k.EmployeeId}"),
                LeadsConverted = convByDateEmp.GetValueOrDefault(k, 0),
                FollowUpsDone = doneByDateEmp.GetValueOrDefault(k, 0),
                NoAnswerCount = naByDateEmp.GetValueOrDefault(k, 0),
                InterestedCount = intByDateEmp.GetValueOrDefault(k, 0),
                NotInterestedCount = notIntByDateEmp.GetValueOrDefault(k, 0),
            });
        }

        return new EmployeePerformanceReportResponse
        {
            Success = true,
            Period = periodLabel,
            FromUtc = fromUtc,
            ToUtc = toUtc,
            TotalLeadsConverted = convertedLeads.Count,
            TotalFollowUpsDone = doneFollowUps.Count(f => !IsNoAnswer(f.Outcome)),
            TotalNoAnswerCount = doneFollowUps.Count(f => IsNoAnswer(f.Outcome)),
            TotalInterestedCount = doneFollowUps.Count(f => IsInterested(f.Outcome)),
            TotalNotInterestedCount = doneFollowUps.Count(f => IsNotInterested(f.Outcome)),
            ByEmployee = byEmployee.OrderByDescending(e => e.LeadsConverted).ThenByDescending(e => e.FollowUpsDone).ToList(),
            DailyBreakdown = dailyList
        };
    }

    private static bool IsNoAnswer(string? outcome)
    {
        if (string.IsNullOrWhiteSpace(outcome)) return false;
        var o = outcome.Trim();
        return o.StartsWith(FollowUpOutcomes.NoAnswer, StringComparison.OrdinalIgnoreCase)
            || o.StartsWith(FollowUpOutcomes.Callback, StringComparison.OrdinalIgnoreCase)
            || o.StartsWith("No answer", StringComparison.OrdinalIgnoreCase)
            || o.StartsWith("Call back", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInterested(string? outcome)
    {
        if (string.IsNullOrWhiteSpace(outcome)) return false;
        var o = outcome.Trim();
        return o.StartsWith(FollowUpOutcomes.Interested, StringComparison.OrdinalIgnoreCase)
            || o.StartsWith("Interested", StringComparison.OrdinalIgnoreCase)
            || o.StartsWith("Hot", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNotInterested(string? outcome)
    {
        if (string.IsNullOrWhiteSpace(outcome)) return false;
        var o = outcome.Trim();
        return o.StartsWith(FollowUpOutcomes.NotInterested, StringComparison.OrdinalIgnoreCase)
            || o.StartsWith("Not interested", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<EmployeePerformanceDetailResponse> EmployeePerformanceDetailsAsync(
        string type,
        DateTime fromUtc,
        DateTime toUtc,
        string periodLabel,
        long? employeeId = null,
        string? date = null)
    {
        var normType = (type ?? "").Trim().ToLowerInvariant();
        var emps = await db.Employees.AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.Name);
        var activeEmpIds = await db.Employees.AsNoTracking().Where(e => e.IsActive).Select(e => e.Id).ToListAsync();
        var empName = employeeId is { } eid && emps.TryGetValue(eid, out var n) ? n : null;

        // If a specific date (yyyy-MM-dd in IST) is requested
        if (!string.IsNullOrWhiteSpace(date) && DateTime.TryParseExact(date.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var specificDate))
        {
            fromUtc = FollowUpService.ToUtc(specificDate.Date);
            toUtc = FollowUpService.ToUtc(specificDate.Date.AddDays(1));
            periodLabel = specificDate.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        }

        var items = new List<EmployeePerformanceDetailItemDto>();
        string title = normType switch
        {
            "leads_won" or "leads_converted" or "won" or "orders_booked" or "orders" => "Orders Booked (புக் செய்த ஆர்டர்கள்)",
            "followups_done" or "done" => "Follow-ups Done (முடித்தவை)",
            "no_answer" or "noanswer" => "No Answer Follow-up (அழைப்பு எடுக்காதவை)",
            "interested" or "hot" or "hot_leads" => "Hot Leads / Interested (ஆர்வம் காட்டியவை)",
            "not_interested" or "notinterested" => "Not Interested (விருப்பமில்லை)",
            _ => "Performance Details"
        };

        if (normType is "leads_won" or "leads_converted" or "won" or "orders_booked" or "orders")
        {
            var query = db.Leads.AsNoTracking()
                .Where(l => l.ConvertedAt >= fromUtc && l.ConvertedAt < toUtc);

            if (employeeId.HasValue)
                query = query.Where(l => l.AssignedToEmployeeId == employeeId.Value);
            else
                query = query.Where(l => l.AssignedToEmployeeId != null && activeEmpIds.Contains(l.AssignedToEmployeeId.Value));

            var leads = await query.OrderByDescending(l => l.ConvertedAt).Take(2000).ToListAsync();
            var phones = leads.Select(l => l.Phone).Where(p => !string.IsNullOrEmpty(p)).Distinct().ToList();
            var orders = await db.Orders.AsNoTracking()
                .Where(o => phones.Contains(o.CustomerPhone))
                .Select(o => new { OrderId = o.Id, o.CustomerPhone, Total = o.TotalAmount, o.CreatedAt })
                .OrderByDescending(o => o.CreatedAt)
                .ToListAsync();
            var orderMap = orders.GroupBy(o => o.CustomerPhone).ToDictionary(g => g.Key, g => g.First());

            foreach (var l in leads)
            {
                var dt = l.ConvertedAt ?? l.UpdatedAt;
                var ist = FollowUpService.ToIst(dt);
                orderMap.TryGetValue(l.Phone, out var ord);

                items.Add(new EmployeePerformanceDetailItemDto
                {
                    Id = l.Id,
                    LeadId = l.Id,
                    LeadName = string.IsNullOrWhiteSpace(l.Name) ? "Enquiry #" + l.Id : l.Name,
                    LeadPhone = l.Phone,
                    EmployeeName = l.AssignedToEmployeeId is { } eId && emps.TryGetValue(eId, out var ename) ? ename : "Unassigned",
                    Date = ist.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    Time = ist.ToString("hh:mm tt", CultureInfo.InvariantCulture),
                    Outcome = "Order Converted",
                    Notes = l.Notes ?? "",
                    Source = l.Source.ToString(),
                    OrderId = ord?.OrderId,
                    OrderTotal = ord?.Total
                });
            }
        }
        else if (normType is "followups_done" or "done" or "no_answer" or "noanswer" or "interested" or "hot" or "hot_leads" or "not_interested" or "notinterested")
        {
            var query = db.FollowUps.AsNoTracking()
                .Where(f => f.Status == FollowUpStatus.Done && f.CompletedAt >= fromUtc && f.CompletedAt < toUtc);

            if (employeeId.HasValue)
                query = query.Where(f => f.AssignedToEmployeeId == employeeId.Value);
            else
                query = query.Where(f => f.AssignedToEmployeeId != null && activeEmpIds.Contains(f.AssignedToEmployeeId.Value));

            var list = await query.OrderByDescending(f => f.CompletedAt).Take(2000).ToListAsync();
            if (normType is "followups_done" or "done")
            {
                list = list.Where(f => !IsNoAnswer(f.Outcome)).ToList();
            }
            else if (normType is "no_answer" or "noanswer")
            {
                list = list.Where(f => IsNoAnswer(f.Outcome)).ToList();
            }
            else if (normType is "interested" or "hot" or "hot_leads")
            {
                list = list.Where(f => IsInterested(f.Outcome)).ToList();
            }
            else if (normType is "not_interested" or "notinterested")
            {
                list = list.Where(f => IsNotInterested(f.Outcome)).ToList();
            }

            var leadIds = list.Select(f => f.LeadId).Distinct().ToList();
            var leadMap = await db.Leads.AsNoTracking()
                .Where(l => leadIds.Contains(l.Id))
                .Select(l => new { l.Id, l.Name, l.Phone, l.Source })
                .ToDictionaryAsync(l => l.Id, l => l);

            foreach (var f in list)
            {
                leadMap.TryGetValue(f.LeadId, out var lead);
                var dt = f.CompletedAt ?? f.DueAt;
                var ist = FollowUpService.ToIst(dt);

                items.Add(new EmployeePerformanceDetailItemDto
                {
                    Id = f.Id,
                    LeadId = f.LeadId,
                    LeadName = lead is not null && !string.IsNullOrWhiteSpace(lead.Name) ? lead.Name : "Lead #" + f.LeadId,
                    LeadPhone = lead?.Phone ?? "",
                    EmployeeName = f.AssignedToEmployeeId is { } eId && emps.TryGetValue(eId, out var ename) ? ename : "Staff",
                    Date = ist.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    Time = ist.ToString("hh:mm tt", CultureInfo.InvariantCulture),
                    Outcome = f.Outcome ?? "Done",
                    Notes = f.Notes ?? "",
                    Source = f.Type
                });
            }
        }

        return new EmployeePerformanceDetailResponse
        {
            Success = true,
            Type = normType,
            Title = title,
            Period = periodLabel,
            EmployeeName = empName,
            TotalCount = items.Count,
            Items = items
        };
    }
}
