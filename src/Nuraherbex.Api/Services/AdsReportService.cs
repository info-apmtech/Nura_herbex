using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nuraherbex.Api.Data;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Services;

/// <summary>What the report is narrowed to. Dates are Indian calendar days, both included.</summary>
public sealed record AdsQuery(DateTime From, DateTime To, string? Campaign = null, string? Ad = null, string? SourceType = null,
    string? Status = null, string? Owner = null, string? Q = null, bool CustomersOnly = false);

/// <summary>The ad details WhatsApp attaches to the first message of a Click-to-WhatsApp lead.</summary>
public sealed record AdReferral(string? AdId, string? SourceType, string? SourceUrl, string? Headline, string? Body, string? MediaType)
{
    public string Platform => (SourceUrl ?? "").ToLowerInvariant() switch
    {
        var u when u.Contains("instagram") => "Instagram",
        var u when u.Contains("facebook") || u.Contains("fb.me") || u.Contains("fb.com") => "Facebook",
        _ => "Meta",
    };
}

/// <summary>
/// The Ad campaigns report. A person counts as an ad lead from the day their first message carried ad details (first touch).
/// Campaign and ad names, spend and reach come from the Meta sync when it is connected; without it the report groups by the ad id and headline.
/// Sales are the confirmed orders (paid or cash on delivery, not cancelled or returned) of the people in view.
/// </summary>
public class AdsReportService(NuraDbContext db)
{
    public const int MaxPeople = 2000;
    private const int WeeklyAfterDays = 62;

    // ---- referral parsing ---------------------------------------------------------------------

    /// <summary>Reads the stored referral JSON. A damaged or empty object still counts as an ad touch with nothing known about the ad.</summary>
    public static AdReferral? ParseReferral(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            string? S(string name)
            {
                if (!doc.RootElement.TryGetProperty(name, out var v)) return null;
                var text = v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null };
                return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            }
            return new AdReferral(S("source_id"), S("source_type")?.ToLowerInvariant(), S("source_url"), S("headline"), S("body"), S("media_type"));
        }
        catch (JsonException) { return new AdReferral(null, null, null, null, null, null); }
    }

    // ---- the report ---------------------------------------------------------------------------

    private sealed class Att
    {
        public required Lead L { get; init; }
        public required AdReferral R { get; init; }
        public DateTime FirstAt { get; init; }
        public string AdKey { get; set; } = "";
        public string AdLabel { get; set; } = "";
        public string CampKey { get; set; } = "";
        public string CampLabel { get; set; } = "";
        public string AdType { get; set; } = "ad";
        public string Status { get; set; } = "new";
        public bool IsCustomer { get; init; }
        public int Orders { get; set; }
        public decimal Revenue { get; set; }
        public string? OwnerName { get; set; }
    }

    private sealed record Names(string AdName, string CampaignId, string CampaignName);

    public async Task<AdsReportResponse> BuildAsync(AdsQuery q, long? ownerOnly)
    {
        var fromUtc = FollowUpService.ToUtc(q.From.Date);
        var toUtc = FollowUpService.ToUtc(q.To.Date.AddDays(1));
        var allowSpend = ownerOnly is null;

        // 1) first ad touch per lead, in the period
        var events = await db.LeadSourceEvents.AsNoTracking().Where(e => e.ReferralJson != null && e.ReferralJson != "")
            .Select(e => new { e.LeadId, e.CreatedAt, e.ReferralJson }).ToListAsync();
        var first = events.GroupBy(e => e.LeadId).Select(g => g.OrderBy(x => x.CreatedAt).First())
            .Where(e => e.CreatedAt >= fromUtc && e.CreatedAt < toUtc).ToList();

        var leads = new Dictionary<long, Lead>();
        foreach (var chunk in first.Select(x => x.LeadId).Distinct().Chunk(800))
        {
            var ids = chunk.ToList();
            foreach (var l in await db.Leads.AsNoTracking().Where(l => ids.Contains(l.Id)).ToListAsync()) leads[l.Id] = l;
        }
        var owners = await db.Employees.AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.Name);

        // 2) campaign and ad names from Meta, when synced
        var names = new Dictionary<string, Names>();
        var spendRows = new List<AdInsightDaily>();
        if (allowSpend)
        {
            var all = await db.AdInsights.AsNoTracking().OrderByDescending(x => x.Date)
                .Select(x => new { x.AdId, x.AdName, x.CampaignId, x.CampaignName, x.Date }).ToListAsync();
            foreach (var r in all) names.TryAdd(r.AdId, new Names(r.AdName, r.CampaignId, r.CampaignName));
            var fd = q.From.Date; var td = q.To.Date;
            spendRows = await db.AdInsights.AsNoTracking().Where(x => x.Date >= fd && x.Date <= td).ToListAsync();
        }
        var hasMeta = allowSpend && names.Count > 0;

        // 3) attribute every person
        var scoped = new List<Att>();
        foreach (var e in first)
        {
            if (!leads.TryGetValue(e.LeadId, out var lead)) continue;
            if (ownerOnly is { } me && lead.AssignedToEmployeeId != me) continue;
            var r = ParseReferral(e.ReferralJson) ?? new AdReferral(null, null, null, null, null, null);
            var headline = r.Headline ?? lead.SourceDetail;
            names.TryGetValue(r.AdId ?? "", out var n);
            var adKey = r.AdId ?? "h:" + (headline ?? "unknown");
            var adLabel = n?.AdName is { Length: > 0 } an ? an : headline ?? (r.AdId is null ? "Unknown ad" : $"Ad {r.AdId}");
            var att = new Att
            {
                L = lead, R = r, FirstAt = e.CreatedAt, IsCustomer = lead.CustomerSince is not null,
                AdKey = adKey, AdLabel = adLabel,
                CampKey = n is { CampaignId.Length: > 0 } ? n.CampaignId : r.AdId is not null ? "ad:" + r.AdId : adKey,
                CampLabel = n is { CampaignName.Length: > 0 } ? n.CampaignName : r.AdId is not null ? $"Unnamed campaign (ad {r.AdId})" : $"Unnamed campaign ({adLabel})",
                AdType = r.SourceType is "post" ? "post" : "ad",
                Status = lead.CustomerSince is not null ? "customer" : lead.Status,
                OwnerName = lead.AssignedToEmployeeId is { } oid ? owners.GetValueOrDefault(oid) : null,
            };
            scoped.Add(att);
        }

        // 4) orders per person (matched by phone)
        var byLast10 = new Dictionary<string, List<Att>>();
        foreach (var a in scoped) { var k = Last10(a.L.Phone); if (!byLast10.TryGetValue(k, out var list)) byLast10[k] = list = new(); list.Add(a); }
        foreach (var chunk in scoped.SelectMany(a => LeadService.PhoneForms(a.L.Phone)).Distinct().Chunk(600))
        {
            var forms = chunk.ToList();
            var orders = await db.Orders.AsNoTracking().Where(o => forms.Contains(o.CustomerPhone))
                .Select(o => new Order { CustomerPhone = o.CustomerPhone, TotalAmount = o.TotalAmount, PaymentStatus = o.PaymentStatus, FulfillmentStatus = o.FulfillmentStatus }).ToListAsync();
            foreach (var o in orders)
                if (LeadService.IsConfirmed(o) && byLast10.TryGetValue(Last10(o.CustomerPhone), out var list))
                    foreach (var a in list) { a.Orders++; a.Revenue += o.TotalAmount; }
        }

        // 5) filters
        var personFilters = !string.IsNullOrEmpty(q.Status) || !string.IsNullOrEmpty(q.Owner) || !string.IsNullOrWhiteSpace(q.Q) || q.CustomersOnly;
        var costHidden = allowSpend && hasMeta && (personFilters || !string.IsNullOrEmpty(q.SourceType));
        var people = scoped.Where(a => Matches(a, q)).ToList();

        var spendIn = costHidden || !hasMeta ? new List<AdInsightDaily>() : spendRows
            .Where(r => (string.IsNullOrEmpty(q.Campaign) || r.CampaignId == q.Campaign) && (string.IsNullOrEmpty(q.Ad) || r.AdId == q.Ad)).ToList();
        var showSpend = hasMeta && !costHidden;

        // 6) numbers
        var res = new AdsReportResponse
        {
            Success = true, From = q.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), To = q.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Period = q.From.Date == q.To.Date ? q.From.ToString("d MMM yyyy", CultureInfo.InvariantCulture)
                : q.From.ToString("d MMM yyyy", CultureInfo.InvariantCulture) + " to " + q.To.ToString("d MMM yyyy", CultureInfo.InvariantCulture),
            MetaConnected = hasMeta, CostHidden = costHidden,
            ScopeNote = ownerOnly is null ? null : "Showing leads assigned to you only. Spend and reach are shown to people who see everyone's records.",
        };
        res.Kpis = Totals(people, spendIn, showSpend);
        res.Funnel = new[] { "new", "follow-up", "interested", "customer", "not-interested" }.Select(s => new AdsFunnelRow { Status = s, Count = people.Count(p => p.Status == s) }).ToList();
        (res.Trend, res.TrendWeekly) = Trend(q, people, spendIn);
        res.Campaigns = CampaignRows(people, spendIn, showSpend, includeEmptyCampaigns: showSpend && !personFilters);
        res.Ads = AdRows(people, spendIn, showSpend, includeEmptyAds: showSpend && !personFilters, names);
        res.PeopleTotal = people.Count;
        res.People = people.OrderByDescending(p => p.FirstAt).Take(MaxPeople).Select(p => new AdsPersonRow
        {
            LeadId = p.L.Id, Name = p.L.Name, Phone = p.L.Phone, Campaign = p.CampLabel, Ad = p.AdLabel, AdType = p.AdType, Platform = p.R.Platform,
            FirstAdContactAt = p.FirstAt, Status = p.Status, IsCustomer = p.IsCustomer, OwnerName = p.OwnerName, Orders = p.Orders, Revenue = p.Revenue, LastContactAt = p.L.LastContactAt,
        }).ToList();
        res.Filters = Options(scoped, spendRows, q, allowSpend);
        return res;
    }

    private static bool Matches(Att a, AdsQuery q)
    {
        if (!string.IsNullOrEmpty(q.Campaign) && a.CampKey != q.Campaign) return false;
        if (!string.IsNullOrEmpty(q.Ad) && a.AdKey != q.Ad) return false;
        if (!string.IsNullOrEmpty(q.SourceType) && a.AdType != q.SourceType) return false;
        if (!string.IsNullOrEmpty(q.Status) && a.Status != q.Status) return false;
        if (q.CustomersOnly && !a.IsCustomer) return false;
        if (!string.IsNullOrEmpty(q.Owner))
        {
            if (q.Owner == "unassigned") { if (a.L.AssignedToEmployeeId is not null) return false; }
            else if (!long.TryParse(q.Owner, out var oid) || a.L.AssignedToEmployeeId != oid) return false;
        }
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var term = q.Q.Trim();
            var digits = new string(term.Where(char.IsDigit).ToArray());
            var hit = a.L.Name.Contains(term, StringComparison.OrdinalIgnoreCase) || a.AdLabel.Contains(term, StringComparison.OrdinalIgnoreCase)
                || a.CampLabel.Contains(term, StringComparison.OrdinalIgnoreCase) || (digits.Length >= 3 && a.L.Phone.Contains(digits));
            if (!hit) return false;
        }
        return true;
    }

    private static decimal? Div(decimal top, decimal bottom) => bottom > 0 && top > 0 ? Math.Round(top / bottom, 2) : null;
    private static int Pct(int part, int whole) => whole == 0 ? 0 : (int)Math.Round(100.0 * part / whole);
    private static string Last10(string phone) { var d = new string((phone ?? "").Where(char.IsDigit).ToArray()); return d.Length > 10 ? d[^10..] : d; }

    private static AdsKpis Totals(List<Att> people, List<AdInsightDaily> spend, bool showSpend)
    {
        var customers = people.Count(p => p.IsCustomer);
        var revenue = people.Sum(p => p.Revenue);
        var orders = people.Sum(p => p.Orders);
        var k = new AdsKpis
        {
            AdLeads = people.Count, HotLeads = people.Count(p => p.Status == "interested"), Customers = customers, NotInterested = people.Count(p => p.Status == "not-interested"),
            ConversionPercent = Pct(customers, people.Count), Revenue = revenue, Orders = orders, AverageOrderValue = orders == 0 ? 0 : Math.Round(revenue / orders, 2),
            HasSpend = showSpend,
        };
        if (!showSpend) return k;
        k.Spend = spend.Sum(r => r.Spend); k.Impressions = spend.Sum(r => r.Impressions); k.Clicks = spend.Sum(r => r.Clicks); k.Conversations = spend.Sum(r => r.Conversations);
        k.CostPerLead = Div(k.Spend, people.Count); k.CostPerCustomer = Div(k.Spend, customers); k.Roas = Div(revenue, k.Spend);
        return k;
    }

    private static (List<AdsTrendPoint>, bool) Trend(AdsQuery q, List<Att> people, List<AdInsightDaily> spend)
    {
        var from = q.From.Date; var days = (q.To.Date - from).Days + 1;
        var step = days > WeeklyAfterDays ? 7 : 1;
        var points = new List<AdsTrendPoint>();
        for (var i = 0; i < days; i += step)
        {
            var start = from.AddDays(i);
            points.Add(new AdsTrendPoint { Day = start.ToString("d MMM", CultureInfo.InvariantCulture), Date = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });
        }
        int Bucket(DateTime day) { var i = (day.Date - from).Days; return i < 0 || i >= days ? -1 : i / step; }
        foreach (var p in people) { var b = Bucket(FollowUpService.ToIst(p.FirstAt)); if (b >= 0) points[b].Leads++; }
        foreach (var p in people.Where(p => p.L.CustomerSince is not null)) { var b = Bucket(FollowUpService.ToIst(p.L.CustomerSince!.Value)); if (b >= 0) points[b].Customers++; }
        foreach (var r in spend) { var b = Bucket(r.Date); if (b >= 0) points[b].Spend += r.Spend; }
        return (points, step > 1);
    }

    private static List<AdsCampaignRow> CampaignRows(List<Att> people, List<AdInsightDaily> spend, bool showSpend, bool includeEmptyCampaigns)
    {
        var rows = new Dictionary<string, AdsCampaignRow>();
        var adSets = new Dictionary<string, HashSet<string>>();
        foreach (var g in people.GroupBy(p => p.CampKey))
        {
            var customers = g.Count(p => p.IsCustomer);
            rows[g.Key] = new AdsCampaignRow
            {
                Key = g.Key, Campaign = g.First().CampLabel, Leads = g.Count(), Hot = g.Count(p => p.Status == "interested"), Customers = customers,
                ConversionPercent = Pct(customers, g.Count()), Revenue = g.Sum(p => p.Revenue),
            };
            adSets[g.Key] = g.Select(p => p.AdKey).ToHashSet();
        }
        if (showSpend)
            foreach (var g in spend.GroupBy(r => r.CampaignId))
            {
                if (!rows.TryGetValue(g.Key, out var row))
                {
                    if (!includeEmptyCampaigns || g.Sum(r => r.Spend) <= 0) continue;
                    rows[g.Key] = row = new AdsCampaignRow { Key = g.Key, Campaign = g.OrderByDescending(r => r.Date).First().CampaignName };
                    adSets[g.Key] = new HashSet<string>();
                }
                row.Spend = g.Sum(r => r.Spend);
                foreach (var ad in g.Select(r => r.AdId)) adSets[g.Key].Add(ad);
            }
        foreach (var r in rows.Values)
        {
            r.Ads = adSets.GetValueOrDefault(r.Key)?.Count ?? 0;
            if (showSpend) { r.CostPerLead = Div(r.Spend, r.Leads); r.CostPerCustomer = Div(r.Spend, r.Customers); r.Roas = Div(r.Revenue, r.Spend); }
        }
        return rows.Values.OrderByDescending(r => r.Leads).ThenByDescending(r => r.Spend).ThenBy(r => r.Campaign).ToList();
    }

    private static List<AdsAdRow> AdRows(List<Att> people, List<AdInsightDaily> spend, bool showSpend, bool includeEmptyAds, Dictionary<string, Names> names)
    {
        var rows = new Dictionary<string, AdsAdRow>();
        foreach (var g in people.GroupBy(p => p.AdKey))
        {
            var customers = g.Count(p => p.IsCustomer);
            var f = g.First();
            rows[g.Key] = new AdsAdRow
            {
                Key = g.Key, Ad = f.AdLabel, Campaign = f.CampLabel, CampaignKey = f.CampKey, AdType = f.AdType, Leads = g.Count(), Hot = g.Count(p => p.Status == "interested"),
                Customers = customers, ConversionPercent = Pct(customers, g.Count()), Revenue = g.Sum(p => p.Revenue),
            };
        }
        if (showSpend)
            foreach (var g in spend.GroupBy(r => r.AdId))
            {
                if (!rows.TryGetValue(g.Key, out var row))
                {
                    if (!includeEmptyAds || g.Sum(r => r.Spend) <= 0) continue;
                    var latest = g.OrderByDescending(r => r.Date).First();
                    rows[g.Key] = row = new AdsAdRow { Key = g.Key, Ad = latest.AdName, Campaign = latest.CampaignName, CampaignKey = latest.CampaignId };
                }
                row.Spend = g.Sum(r => r.Spend);
            }
        foreach (var r in rows.Values) if (showSpend) r.CostPerLead = Div(r.Spend, r.Leads);
        return rows.Values.OrderByDescending(r => r.Leads).ThenByDescending(r => r.Spend).ThenBy(r => r.Ad).ToList();
    }

    /// <summary>The dropdown lists: everything in the period for this person's scope, not narrowed by the other filters, so choices never vanish.</summary>
    private static AdsFilterOptions Options(List<Att> scoped, List<AdInsightDaily> spendRows, AdsQuery q, bool allowSpend)
    {
        var camps = scoped.GroupBy(a => a.CampKey).ToDictionary(g => g.Key, g => g.First().CampLabel);
        var ads = scoped.GroupBy(a => a.AdKey).ToDictionary(g => g.Key, g => (Label: g.First().AdLabel, Camp: g.First().CampKey));
        if (allowSpend)
        {
            foreach (var g in spendRows.GroupBy(r => r.CampaignId)) camps.TryAdd(g.Key, g.OrderByDescending(r => r.Date).First().CampaignName);
            foreach (var g in spendRows.GroupBy(r => r.AdId)) { var l = g.OrderByDescending(r => r.Date).First(); ads.TryAdd(g.Key, (l.AdName, l.CampaignId)); }
        }
        var owners = scoped.Where(a => a.L.AssignedToEmployeeId is not null).GroupBy(a => a.L.AssignedToEmployeeId!.Value)
            .Select(g => new AdsOption { Value = g.Key.ToString(), Label = g.First().OwnerName ?? "Former employee" }).OrderBy(o => o.Label).ToList();
        if (scoped.Any(a => a.L.AssignedToEmployeeId is null)) owners.Insert(0, new AdsOption { Value = "unassigned", Label = "Unassigned" });
        return new AdsFilterOptions
        {
            Campaigns = camps.Select(c => new AdsOption { Value = c.Key, Label = c.Value }).OrderBy(c => c.Label).ToList(),
            Ads = ads.Where(a => string.IsNullOrEmpty(q.Campaign) || a.Value.Camp == q.Campaign).Select(a => new AdsOption { Value = a.Key, Label = a.Value.Label }).OrderBy(a => a.Label).ToList(),
            Owners = owners,
        };
    }
}
