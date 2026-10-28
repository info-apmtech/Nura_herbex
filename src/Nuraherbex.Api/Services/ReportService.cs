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
        var r = key switch
        {
            "orders" => await OrdersAsync(fromUtc, toUtc),
            "leads" => await LeadsAsync(fromUtc, toUtc, ownerOnly),
            "followups" => await FollowUpsAsync(fromUtc, toUtc, ownerOnly),
            "whatsapp" => await WhatsAppAsync(fromUtc, toUtc),
            "tasks" => await TasksAsync(fromUtc, toUtc, ownerOnly),
            "customers" => await CustomersAsync(fromUtc, toUtc),
            "payments" => await PaymentsAsync(fromUtc, toUtc),
            _ => null,
        };
        if (r is null) return null;
        var info = Catalog.First(c => c.Key == key);
        r.Success = true; r.Key = key; r.Title = info.Title; r.Description = info.Description; r.Period = periodLabel;
        r.Truncated = r.Rows.Count >= MaxRows;
        return r;
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
                Money(o.Subtotal), Money(o.DiscountAmount), Money(o.ShippingFee), Money(o.TotalAmount), o.PaymentMethod, o.PaymentStatus,
                o.FulfillmentStatus, o.DeliveryStatus, o.ShiprocketCourier ?? o.Courier ?? "", o.ShiprocketAwb ?? ""]);
        var paid = orders.Where(o => o.PaymentStatus is "PAID" or "COD_COLLECTED").ToList();
        var delivered = orders.Count(o => o.FulfillmentStatus == "DELIVERED");
        var lost = orders.Count(o => o.FulfillmentStatus is "RTO" or "CANCELLED");
        var units = orders.SelectMany(o => o.Items).GroupBy(i => i.Name).Select(g => new { g.Key, Qty = g.Sum(i => i.Quantity) }).OrderByDescending(x => x.Qty).FirstOrDefault();
        res.Summary =
        [
            S("Orders", orders.Count), S("Revenue collected", Money(paid.Sum(o => o.TotalAmount)), "Paid online or COD collected", "good"),
            S("COD awaiting", Money(orders.Where(o => o.PaymentStatus == "COD_PENDING").Sum(o => o.TotalAmount)), "Cash on delivery not yet collected", "warn"),
            S("Average order", Money(orders.Count == 0 ? 0 : orders.Average(o => o.TotalAmount))),
            S("Delivered", $"{delivered} ({Pct(delivered, orders.Count)})", null, "good"),
            S("Returned or cancelled", $"{lost} ({Pct(lost, orders.Count)})", null, lost > 0 ? "bad" : "neutral"),
            S("Prepaid / COD", $"{orders.Count(o => o.PaymentMethod != "COD")} / {orders.Count(o => o.PaymentMethod == "COD")}"),
            S("Best seller", units is null ? "None" : $"{units.Key} ({units.Qty})"),
        ];
        return res;
    }

    // ---- Leads ----------------------------------------------------------------------------------

    private async Task<ReportResponse> LeadsAsync(DateTime from, DateTime to, long? ownerOnly)
    {
        var owners = await db.Employees.AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.Name);
        var leads = await db.Leads.AsNoTracking().Where(l => l.CreatedAt >= from && l.CreatedAt < to && (ownerOnly == null || l.AssignedToEmployeeId == ownerOnly)).OrderByDescending(l => l.CreatedAt).Take(MaxRows).ToListAsync();
        var ids = leads.Select(l => l.Id).ToList();
        // The newest comment staff left when changing the lead's status, plus how many follow-ups are done.
        var notes = (await db.LeadStatusHistory.AsNoTracking().Where(h => ids.Contains(h.LeadId) && h.Note != null && h.Note != "").OrderByDescending(h => h.CreatedAt).ToListAsync())
            .GroupBy(h => h.LeadId).ToDictionary(g => g.Key, g => g.First());
        var fu = await db.FollowUps.AsNoTracking().Where(f => ids.Contains(f.LeadId)).GroupBy(f => f.LeadId).Select(g => new { g.Key, Total = g.Count(), Done = g.Count(f => f.Status == "done") }).ToDictionaryAsync(x => x.Key);
        var res = new ReportResponse { Columns = ["Lead ID", "Created", "Name", "Phone", "Email", "Source", "Campaign / detail", "Status", "Owner", "Notes", "Latest comment", "Follow-ups", "Last contact", "Converted on", "Consent", "First message"] };
        foreach (var l in leads)
        {
            notes.TryGetValue(l.Id, out var h);
            fu.TryGetValue(l.Id, out var f);
            res.Rows.Add([l.Id.ToString(Inv), Ist(l.CreatedAt), l.Name, l.Phone, l.Email ?? "", l.Source, l.SourceDetail ?? "", l.Status,
                l.AssignedToEmployeeId is { } id ? owners.GetValueOrDefault(id) ?? "" : "Unassigned", Clip(l.Notes, 300),
                h is null ? "" : $"{Clip(h.Note, 300)} ({Ist(h.CreatedAt)})", f is null ? "" : $"{f.Done} of {f.Total} done", Ist(l.LastContactAt), Ist(l.ConvertedAt), l.ConsentStatus, Clip(l.FirstMessage, 160)]);
        }
        var won = leads.Count(l => l.Status == LeadStatuses.Converted);
        var unassigned = leads.Count(l => l.AssignedToEmployeeId is null);
        res.Summary =
        [
            S("New leads", leads.Count), S("Converted", $"{won} ({Pct(won, leads.Count)})", "Conversion rate of this period's leads", "good"),
            S("Interested", leads.Count(l => l.Status == LeadStatuses.Interested)), S("In follow-up", leads.Count(l => l.Status == LeadStatuses.FollowUp)),
            S("Not interested", leads.Count(l => l.Status == LeadStatuses.NotInterested)), S("Still new", leads.Count(l => l.Status == LeadStatuses.New)),
            S("Unassigned", unassigned, "Leads with no owner", unassigned > 0 ? "warn" : "neutral"),
            S("Top source", leads.GroupBy(l => l.Source).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} ({g.Count()})").FirstOrDefault() ?? "None"),
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
            var status = f.Status == "pending" && f.DueAt < today ? "overdue" : f.Status;
            res.Rows.Add([Ist(f.DueAt), l?.Name ?? "", l?.Phone ?? "", f.Type, f.Title, Clip(f.Notes, 300), f.AssignedToEmployeeId is { } id ? owners.GetValueOrDefault(id) ?? "" : "Unassigned",
                status, Ist(f.CompletedAt), f.CompletedBy ?? "", Clip(f.Outcome, 300), l?.Status ?? "", f.StepKey ?? "", f.Source, f.OrderId ?? ""]);
        }
        var done = items.Count(f => f.Status == "done");
        var sent = items.Count(f => f.Status == "done" && f.Outcome != null && f.Outcome.StartsWith("Sent"));
        var overdue = items.Count(f => f.Status == "pending" && f.DueAt < today);
        res.Summary =
        [
            S("Follow-ups due", items.Count), S("Done", $"{done} ({Pct(done, items.Count)})", null, "good"),
            S("Overdue", overdue, "Still pending after their day", overdue > 0 ? "bad" : "neutral"), S("Pending", items.Count(f => f.Status == "pending") - overdue),
            S("Skipped", items.Count(f => f.Status == "skipped")), S("WhatsApp messages sent", sent, "Sent from the follow-up list"),
            S("Sent automatically", items.Count(f => f.CompletedBy == "auto")), S("Journey steps", items.Count(f => f.Source == "journey")),
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
            var late = t.Status != "done" && t.DueAt is { } d && d < now;
            res.Rows.Add([t.Id.ToString(Inv), t.Title, t.DepartmentId is { } di ? depts.GetValueOrDefault(di) ?? "" : "", t.AssignedToEmployeeId is { } a ? people.GetValueOrDefault(a) ?? "" : "Unassigned",
                t.CreatedByName, t.Priority, t.Status, checks.TryGetValue(t.Id, out var ck) ? $"{ck.Done} of {ck.Total}" : "",
                lastComment.TryGetValue(t.Id, out var lc) ? $"{lc.Author}: {Clip(lc.Body, 300)}" : "", Ist(t.CreatedAt), Ist(t.DueAt), Ist(t.CompletedAt), late ? "Yes" : "",
                trail.TryGetValue(t.Id, out var tl) ? string.Join("\n", tl.Select(x => $"{x.When} · {x.Actor}: {Clip(x.Text, 400)}")) : ""]);
            if (trail.TryGetValue(t.Id, out var lines)) res.Details[t.Id.ToString(Inv)] = lines;
        }
        var doneCount = tasks.Count(t => t.Status == "done");
        var overdue = tasks.Count(t => t.Status != "done" && t.DueAt is { } d && d < now);
        res.Summary =
        [
            S("Tasks created", tasks.Count), S("Done", $"{doneCount} ({Pct(doneCount, tasks.Count)})", null, "good"),
            S("In progress", tasks.Count(t => t.Status == "in_progress")), S("Blocked", tasks.Count(t => t.Status == "blocked"), null, tasks.Any(t => t.Status == "blocked") ? "warn" : "neutral"),
            S("Overdue", overdue, null, overdue > 0 ? "bad" : "neutral"), S("Urgent or high", tasks.Count(t => t.Priority is "urgent" or "high")),
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
        var byCustomer = spend.GroupBy(s => s.CustomerId!).ToDictionary(g => g.Key, g => (Count: g.Count(), Total: g.Where(x => x.PaymentStatus is "PAID" or "COD_COLLECTED").Sum(x => x.TotalAmount)));
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
}
