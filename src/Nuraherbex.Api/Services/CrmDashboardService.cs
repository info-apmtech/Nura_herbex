using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Nuraherbex.Api.Data;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Services;

/// <summary>Numbers for the sales dashboard: lead flow, conversion, revenue from leads, reply speed and who needs attention.</summary>
public class CrmDashboardService(NuraDbContext db)
{
    private static readonly TimeZoneInfo Ist = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "India Standard Time" : "Asia/Kolkata");

    private static string Last10(string phone) { var d = new string((phone ?? "").Where(char.IsDigit).ToArray()); return d.Length > 10 ? d[^10..] : d; }

    private static bool Counts(string payment, string fulfillment) =>
        payment is "PAID" or "COD_PENDING" or "COD_COLLECTED" && fulfillment is not ("CANCELLED" or "RTO");

    private static int? Median(List<double> minutes)
    {
        if (minutes.Count == 0) return null;
        minutes.Sort();
        var mid = minutes.Count / 2;
        return (int)Math.Round(minutes.Count % 2 == 1 ? minutes[mid] : (minutes[mid - 1] + minutes[mid]) / 2);
    }

    public async Task<CrmDashboardResponse> BuildAsync(int days, long? onlyEmployee)
    {
        days = Math.Clamp(days, 1, 365);
        var now = DateTime.UtcNow;
        var todayStart = FollowUpService.TodayStartUtc();
        var from = todayStart.AddDays(-(days - 1));

        // ---- leads ----
        var leadQ = db.Leads.AsNoTracking().AsQueryable();
        if (onlyEmployee is { } me) leadQ = leadQ.Where(l => l.AssignedToEmployeeId == me);

        var created = await leadQ.Where(l => l.CreatedAt >= from).Select(l => new { l.Id, l.Source, l.Status, l.CreatedAt, l.Phone, l.AssignedToEmployeeId, l.ConvertedAt }).ToListAsync();
        var convertedInPeriod = await leadQ.CountAsync(l => l.ConvertedAt != null && l.ConvertedAt >= from);
        var report = new CrmDashboardResponse
        {
            Success = true, Days = days, From = from, Personal = onlyEmployee is not null,
            NewLeads = created.Count,
            ConvertedLeads = convertedInPeriod,
            ConversionRate = created.Count == 0 ? 0 : (int)Math.Round(100.0 * created.Count(l => l.Status == LeadStatuses.Converted) / created.Count),
            OpenLeads = await leadQ.CountAsync(l => l.Status != LeadStatuses.Converted && l.Status != LeadStatuses.NotInterested),
            Unassigned = onlyEmployee is null ? await db.Leads.CountAsync(l => l.AssignedToEmployeeId == null && l.Status != LeadStatuses.Converted && l.Status != LeadStatuses.NotInterested) : 0,
        };

        report.Funnel = LeadStatuses.All.Select(s => new CrmFunnelRow { Status = s, Count = created.Count(l => l.Status == s) }).ToList();

        // ---- orders matched to leads by phone ----
        var allLeads = await leadQ.Select(l => new { l.Id, l.Phone, l.Source, l.AssignedToEmployeeId }).ToListAsync();
        var byPhone = new Dictionary<string, (long Id, string Source, long? Owner)>();
        foreach (var l in allLeads) byPhone[Last10(l.Phone)] = (l.Id, l.Source, l.AssignedToEmployeeId);
        var orders = (await db.Orders.AsNoTracking().Where(o => o.CreatedAt >= from)
                .Select(o => new { o.CustomerPhone, o.TotalAmount, o.PaymentStatus, o.FulfillmentStatus, o.CreatedAt }).ToListAsync())
            .Where(o => Counts(o.PaymentStatus, o.FulfillmentStatus) && byPhone.ContainsKey(Last10(o.CustomerPhone))).ToList();
        report.Orders = orders.Count;
        report.Revenue = orders.Sum(o => o.TotalAmount);
        report.AverageOrderValue = orders.Count == 0 ? 0 : Math.Round(report.Revenue / orders.Count, 0);

        // ---- first reply by a person ----
        var firstMsgs = await db.WhatsAppMessages.AsNoTracking()
            .Where(m => m.LeadId != null && m.CreatedAt >= from.AddDays(-1))
            .Select(m => new { LeadId = m.LeadId!.Value, m.Direction, m.CreatedAt, m.SentBy }).ToListAsync();
        var inScope = allLeads.Select(l => l.Id).ToHashSet();
        var replyMinutes = new Dictionary<long, double>();
        var awaiting = 0;
        foreach (var g in firstMsgs.Where(m => inScope.Contains(m.LeadId)).GroupBy(m => m.LeadId))
        {
            var inbound = g.Where(m => m.Direction == "inbound").OrderBy(m => m.CreatedAt).FirstOrDefault();
            if (inbound is null || inbound.CreatedAt < from) continue;
            var reply = g.Where(m => m.Direction == "outbound" && m.CreatedAt >= inbound.CreatedAt && m.SentBy is not (null or "system" or "auto")).OrderBy(m => m.CreatedAt).FirstOrDefault();
            if (reply is null) { if (now - inbound.CreatedAt > TimeSpan.FromHours(1)) awaiting++; }
            else replyMinutes[g.Key] = (reply.CreatedAt - inbound.CreatedAt).TotalMinutes;
        }
        report.MedianFirstReplyMinutes = Median(replyMinutes.Values.ToList());
        report.AwaitingFirstReply = awaiting;
        var measured = replyMinutes.Count + awaiting;
        report.RepliedWithinHourPercent = measured == 0 ? 0 : (int)Math.Round(100.0 * replyMinutes.Values.Count(v => v <= 60) / measured);

        // ---- follow-ups ----
        var fq = db.FollowUps.AsNoTracking().Where(f => f.Status == "pending");
        if (onlyEmployee is { } me2) fq = fq.Where(f => f.AssignedToEmployeeId == me2);
        report.FollowUpsOverdue = await fq.CountAsync(f => f.DueAt < todayStart);
        report.FollowUpsToday = await fq.CountAsync(f => f.DueAt >= todayStart && f.DueAt < todayStart.AddDays(1));

        // ---- by source ----
        foreach (var g in created.GroupBy(l => l.Source).OrderByDescending(g => g.Count()))
        {
            var converted = g.Count(l => l.Status == LeadStatuses.Converted);
            report.BySource.Add(new CrmSourceRow
            {
                Source = g.Key, Leads = g.Count(), Converted = converted, ConversionPercent = (int)Math.Round(100.0 * converted / g.Count()),
                Revenue = orders.Where(o => byPhone[Last10(o.CustomerPhone)].Source == g.Key).Sum(o => o.TotalAmount),
            });
        }
        // Revenue can also come from sources with no new lead in the period (older leads who reordered).
        foreach (var src in orders.GroupBy(o => byPhone[Last10(o.CustomerPhone)].Source).Where(g => report.BySource.All(r => r.Source != g.Key)))
            report.BySource.Add(new CrmSourceRow { Source = src.Key, Revenue = src.Sum(o => o.TotalAmount) });

        // ---- by employee ----
        var names = await db.Employees.AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.Name);
        var open = await db.Leads.AsNoTracking().Where(l => l.AssignedToEmployeeId != null && l.Status != LeadStatuses.Converted && l.Status != LeadStatuses.NotInterested)
            .GroupBy(l => l.AssignedToEmployeeId!.Value).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        var overdue = await db.FollowUps.AsNoTracking().Where(f => f.Status == "pending" && f.DueAt < todayStart && f.AssignedToEmployeeId != null)
            .GroupBy(f => f.AssignedToEmployeeId!.Value).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        var leadOwner = allLeads.ToDictionary(l => l.Id, l => l.AssignedToEmployeeId);
        var wonByOwner = (await leadQ.Where(l => l.ConvertedAt != null && l.ConvertedAt >= from).Select(l => l.AssignedToEmployeeId).ToListAsync()).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        foreach (var id in names.Keys.Where(k => onlyEmployee is null || k == onlyEmployee))
        {
            var mine = replyMinutes.Where(kv => leadOwner.GetValueOrDefault(kv.Key) == id).Select(kv => kv.Value).ToList();
            report.ByEmployee.Add(new CrmEmployeeRow
            {
                EmployeeId = id, Name = names[id],
                Leads = created.Count(l => l.AssignedToEmployeeId == id),
                Converted = wonByOwner.GetValueOrDefault(id),
                Revenue = orders.Where(o => byPhone[Last10(o.CustomerPhone)].Owner == id).Sum(o => o.TotalAmount),
                MedianFirstReplyMinutes = Median(mine), OpenLeads = open.GetValueOrDefault(id), OverdueFollowUps = overdue.GetValueOrDefault(id),
            });
        }
        report.ByEmployee = report.ByEmployee.OrderByDescending(e => e.Revenue).ThenByDescending(e => e.Leads).ToList();

        // ---- trend ----
        var span = Math.Min(days, 31);
        var convertedDates = (await leadQ.Where(l => l.ConvertedAt != null && l.ConvertedAt >= todayStart.AddDays(-(span - 1))).Select(l => l.ConvertedAt!.Value).ToListAsync());
        for (var i = span - 1; i >= 0; i--)
        {
            var s = todayStart.AddDays(-i); var e = s.AddDays(1);
            report.Trend.Add(new CrmTrendPoint
            {
                Day = TimeZoneInfo.ConvertTimeFromUtc(s, Ist).ToString("dd MMM", CultureInfo.InvariantCulture),
                NewLeads = created.Count(l => l.CreatedAt >= s && l.CreatedAt < e),
                Converted = convertedDates.Count(d => d >= s && d < e),
            });
        }

        // ---- needs attention ----
        var attention = new List<CrmAttentionRow>();
        var staleCut = now.AddHours(-1);
        var unanswered = await db.Conversations.AsNoTracking().Where(c => c.UnreadCount > 0 && c.LastInboundAt != null && c.LastInboundAt < staleCut).OrderBy(c => c.LastInboundAt).Take(40).ToListAsync();
        var leadMap = await leadQ.Select(l => new { l.Id, l.Name, l.AssignedToEmployeeId, l.Status }).ToDictionaryAsync(l => l.Id);
        foreach (var c in unanswered.Where(c => leadMap.ContainsKey(c.LeadId)).Take(8))
        {
            var l = leadMap[c.LeadId];
            attention.Add(new CrmAttentionRow { LeadId = l.Id, Name = l.Name, Reason = "Customer message not answered", Since = c.LastInboundAt!.Value, Owner = l.AssignedToEmployeeId is { } o ? names.GetValueOrDefault(o) : null });
        }
        if (onlyEmployee is null)
        {
            foreach (var l in await db.Leads.AsNoTracking().Where(l => l.AssignedToEmployeeId == null && l.Status != LeadStatuses.Converted && l.Status != LeadStatuses.NotInterested).OrderBy(l => l.CreatedAt).Take(5).ToListAsync())
                attention.Add(new CrmAttentionRow { LeadId = l.Id, Name = l.Name, Reason = "Nobody owns this lead", Since = l.CreatedAt });
        }
        var late = await fq.Where(f => f.DueAt < todayStart).OrderBy(f => f.DueAt).Take(6).Select(f => new { f.LeadId, f.Title, f.DueAt, f.AssignedToEmployeeId }).ToListAsync();
        var lateLeadNames = await db.Leads.AsNoTracking().Where(l => late.Select(x => x.LeadId).Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.Name);
        foreach (var f in late)
            attention.Add(new CrmAttentionRow { LeadId = f.LeadId, Name = lateLeadNames.GetValueOrDefault(f.LeadId, "Lead"), Reason = "Overdue: " + f.Title, Since = f.DueAt, Owner = f.AssignedToEmployeeId is { } o ? names.GetValueOrDefault(o) : null });
        report.NeedsAttention = attention.OrderBy(a => a.Since).Take(10).ToList();
        return report;
    }
}
