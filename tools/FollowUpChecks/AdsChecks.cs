using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nuraherbex.Api.Data;
using Nuraherbex.Api.Options;
using Nuraherbex.Api.Services;
using Nuraherbex.Shared.Models;

/// <summary>
/// Offline checks for the Ad campaigns report and the Meta sync: referral parsing, first-touch attribution, campaign and ad grouping with and
/// without Meta data, filters, revenue and cost maths, owner scoping, paging and the access token never leaking. Nothing external is contacted.
/// Call from the check runner with: <c>await AdsChecks.RunAsync(db, Check);</c>
/// </summary>
public static class AdsChecks
{
    private const string Token = "SECRET-TOKEN-123";

    private sealed class FixedOptions<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class FakeMeta(Func<HttpRequestMessage, (HttpStatusCode, string)> reply) : HttpMessageHandler
    {
        public List<(string Url, string? Auth)> Calls { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((request.RequestUri!.ToString(), request.Headers.Authorization?.ToString()));
            var (status, body) = reply(request);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static string Row(string date, string adId, string adName, string adset, string campId, string campName, string spend, string impressions, string clicks, string? actions = null) =>
        $"{{\"date_start\":\"{date}\",\"ad_id\":\"{adId}\",\"ad_name\":\"{adName}\",\"adset_id\":\"S{adId}\",\"adset_name\":\"{adset}\",\"campaign_id\":\"{campId}\",\"campaign_name\":\"{campName}\",\"spend\":\"{spend}\",\"impressions\":\"{impressions}\",\"clicks\":\"{clicks}\"{(actions is null ? "" : ",\"actions\":" + actions)}}}";

    public static async Task RunAsync(NuraDbContext db, Action<bool, string> check)
    {
        // ---- referral parsing ----------------------------------------------------------------
        var full = AdsReportService.ParseReferral("{\"source_id\":\"111\",\"source_type\":\"AD\",\"source_url\":\"https://www.instagram.com/p/abc\",\"headline\":\" Summer Sale \",\"media_type\":\"video\"}");
        check(full is { AdId: "111", SourceType: "ad", Headline: "Summer Sale", MediaType: "video" } && full.Platform == "Instagram", "Ads: a referral is read into ad id, type, headline and platform");
        check(AdsReportService.ParseReferral("{\"source_id\":222}")?.AdId == "222", "Ads: a numeric source id is accepted");
        check(AdsReportService.ParseReferral("{\"source_url\":\"https://fb.me/x\"}")?.Platform == "Facebook" && AdsReportService.ParseReferral("{}")?.Platform == "Meta", "Ads: the platform comes from the ad link");
        var broken = AdsReportService.ParseReferral("{not json");
        check(broken is { AdId: null, Headline: null } && AdsReportService.ParseReferral("") is null && AdsReportService.ParseReferral(null) is null, "Ads: a damaged referral still counts as an ad touch, an empty one does not");

        // ---- data: five people around March 2026 ------------------------------------------------
        var ownerA = new Employee { Name = "Ads Owner A", Email = "ads.a@x.test", Role = EmployeeRole.Sales };
        var ownerB = new Employee { Name = "Ads Owner B", Email = "ads.b@x.test", Role = EmployeeRole.Sales };
        db.Employees.AddRange(ownerA, ownerB);
        await db.SaveChangesAsync();

        Lead MakeLead(string phone, string name, LeadStatus status, long? owner, DateTime? customerSince = null) => new()
        {
            Phone = phone, Name = name, Status = status, AssignedToEmployeeId = owner, CustomerSince = customerSince,
            CreatedAt = new DateTime(2026, 3, 9, 9, 0, 0, DateTimeKind.Utc), LastContactAt = new DateTime(2026, 3, 22, 9, 0, 0, DateTimeKind.Utc),
        };
        var l1 = MakeLead("919700000001", "Anita Rao", LeadStatus.New, ownerA.Id);
        var l2 = MakeLead("919700000002", "Bala Kumar", LeadStatus.Converted, ownerA.Id, new DateTime(2026, 3, 15, 8, 0, 0, DateTimeKind.Utc));
        var l3 = MakeLead("919700000003", "Chitra Devi", LeadStatus.Interested, ownerB.Id);
        var l4 = MakeLead("919700000004", "Dev Anand", LeadStatus.New, null);
        var l5 = MakeLead("919700000005", "Esha Nair", LeadStatus.New, ownerB.Id);
        var l6 = MakeLead("919700000006", "Farhan Ali", LeadStatus.New, ownerB.Id);
        db.Leads.AddRange(l1, l2, l3, l4, l5, l6);
        await db.SaveChangesAsync();

        string Ref(string adId, string headline, string type = "ad") => $"{{\"source_id\":\"{adId}\",\"source_type\":\"{type}\",\"source_url\":\"https://fb.me/{adId}\",\"headline\":\"{headline}\"}}";
        LeadSourceEvent Ev(Lead l, DateTime at, string? json) => new() { LeadId = l.Id, CreatedAt = at, ReferralJson = json, Source = LeadSource.WhatsApp };
        db.LeadSourceEvents.AddRange(
            Ev(l1, new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc), Ref("111", "Summer Sale")),
            Ev(l1, new DateTime(2026, 3, 20, 12, 0, 0, DateTimeKind.Utc), Ref("222", "Winter Reel")), // later touch: the first one wins
            Ev(l2, new DateTime(2026, 3, 11, 5, 0, 0, DateTimeKind.Utc), Ref("111", "Summer Sale")),
            Ev(l3, new DateTime(2026, 3, 12, 5, 0, 0, DateTimeKind.Utc), Ref("222", "Winter Reel")),
            Ev(l4, new DateTime(2026, 3, 13, 5, 0, 0, DateTimeKind.Utc), Ref("333", "Festival Post", "post")),
            Ev(l5, new DateTime(2026, 2, 28, 5, 0, 0, DateTimeKind.Utc), Ref("111", "Summer Sale")),   // before the period
            Ev(l6, new DateTime(2026, 3, 14, 5, 0, 0, DateTimeKind.Utc), null));                         // no ad details
        Order MakeOrder(string id, string phone, OrderPaymentStatus pay, string fulfil, decimal total) => new()
        {
            Id = id, CustomerName = "x", CustomerEmail = "x@x.test", CustomerPhone = phone, PaymentMethod = pay == OrderPaymentStatus.Paid ? OrderPaymentMethod.Online : OrderPaymentMethod.Cod, PaymentStatus = pay,
            FulfillmentStatus = fulfil, TotalAmount = total, CreatedAt = new DateTime(2026, 3, 16, 5, 0, 0, DateTimeKind.Utc),
        };
        db.Orders.AddRange(
            MakeOrder("AD-O1", "9700000002", OrderPaymentStatus.Paid, "DELIVERED", 1000m),
            MakeOrder("AD-O2", "919700000002", OrderPaymentStatus.CodPending, "IN_TRANSIT", 500m),
            MakeOrder("AD-O3", "9700000002", OrderPaymentStatus.Paid, "CANCELLED", 700m)); // cancelled: not revenue
        await db.SaveChangesAsync();

        var svc = new AdsReportService(db);
        var march = new AdsQuery(new DateTime(2026, 3, 1), new DateTime(2026, 3, 31));

        // ---- without Meta: grouped by ad id and headline -----------------------------------------
        var plain = await svc.BuildAsync(march, null);
        check(plain.People.Count == 4 && plain.People.All(p => p.LeadId != l5.Id && p.LeadId != l6.Id), "Ads: only people whose first ad touch is in the period (and who had ad details) are listed");
        check(plain.People.Single(p => p.LeadId == l1.Id).Ad == "Summer Sale", "Ads: the first ad touch is the attribution, a later ad does not steal the lead");
        check(!plain.MetaConnected && !plain.Kpis.HasSpend && plain.Kpis.CostPerLead is null, "Ads: without Meta there is no spend or cost, and the report says so");
        var c111 = plain.Campaigns.Single(c => c.Key == "ad:111");
        check(c111.Campaign == "Unnamed campaign (ad 111)" && c111.Leads == 2 && c111.Customers == 1 && c111.ConversionPercent == 50 && c111.Revenue == 1500m, "Ads: leads, customers, conversion and sales per campaign (unnamed when Meta is not connected)");
        check(plain.Campaigns.Count == 3 && plain.Ads.Count == 3 && plain.Ads.Single(a => a.Key == "333").AdType == "post", "Ads: campaigns and ads are grouped, a post stays a post");
        check(plain.Kpis is { AdLeads: 4, HotLeads: 1, Customers: 1, ConversionPercent: 25, Revenue: 1500m, Orders: 2 } && plain.Kpis.AverageOrderValue == 750m, "Ads: headline numbers count confirmed orders only (cancelled is not sales)");
        check(plain.Funnel.Single(f => f.Status == "customer").Count == 1 && plain.Funnel.Single(f => f.Status == "interested").Count == 1 && plain.Funnel.Single(f => f.Status == "new").Count == 2, "Ads: the funnel counts people by stage");
        var p2 = plain.People.Single(p => p.LeadId == l2.Id);
        check(p2.IsCustomer && p2.Status == "customer" && p2.Orders == 2 && p2.Revenue == 1500m && p2.OwnerName == "Ads Owner A" && p2.Platform == "Facebook", "Ads: each person carries status, owner, orders and sales");
        check(plain.Trend.Count == 31 && !plain.TrendWeekly && plain.Trend.Sum(t => t.Leads) == 4 && plain.Trend.Sum(t => t.Customers) == 1, "Ads: a month shows one trend point per day");
        var wide = await svc.BuildAsync(new AdsQuery(new DateTime(2026, 1, 1), new DateTime(2026, 6, 30)), null);
        check(wide.TrendWeekly && wide.Trend.Sum(t => t.Leads) == 5, "Ads: long periods are shown by week and still add up (the February lead is in)");
        check((await svc.BuildAsync(new AdsQuery(new DateTime(2026, 4, 1), new DateTime(2026, 4, 30)), null)).People.Count == 0, "Ads: an empty period gives an empty report");

        // ---- the Meta sync: paging, upsert, token handling ----------------------------------------------
        var opts = new MetaAdsOptions { AdAccountId = "12345", AccessToken = Token, SyncDays = 400 };
        var page1 = "{\"data\":[" + Row("2026-03-10", "111", "Summer Sale Video", "Spring Set", "C1", "Spring Campaign", "250.50", "10000", "300",
                "[{\"action_type\":\"onsite_conversion.messaging_conversation_started_7d\",\"value\":\"12\"},{\"action_type\":\"link_click\",\"value\":\"300\"}]")
            + "," + Row("2026-03-11", "111", "Summer Sale Video", "Spring Set", "C1", "Spring Campaign", "100.00", "5000", "120")
            + $"],\"paging\":{{\"next\":\"https://graph.facebook.com/v21.0/act_12345/insights?after=PAGE2&access_token={Token}\"}}}}";
        var page2 = "{\"data\":[" + Row("2026-03-12", "222", "Winter Reel", "Winter Set", "C2", "Winter Push", "69.50", "3000", "90") + ","
            + Row("2026-03-12", "999", "Idle Ad", "Idle Set", "C3", "Idle Campaign", "50.00", "800", "10") + "]}";
        var fake = new FakeMeta(r => (HttpStatusCode.OK, r.RequestUri!.Query.Contains("after=PAGE2") ? page2 : page1));
        var meta = new MetaAdsService(new HttpClient(fake), db, new FixedOptions<MetaAdsOptions>(opts), NullLogger<MetaAdsService>.Instance);
        var sync = await meta.SyncAsync();
        check(sync.Ok && sync.Rows == 4 && await db.AdInsights.CountAsync() == 4, "Meta: a sync follows paging and stores every ad-day row");
        check(fake.Calls.Count == 2 && fake.Calls.All(c => c.Auth == "Bearer " + Token) && !fake.Calls[0].Url.Contains(Token) && fake.Calls[0].Url.Contains("act_12345/insights") && fake.Calls[0].Url.Contains("level=ad"), "Meta: the token travels in a header, never in the first request address");
        check((await db.AdInsights.SingleAsync(x => x.AdId == "111" && x.Date == new DateTime(2026, 3, 10))).Conversations == 12, "Meta: started conversations are read from the actions");
        await meta.SyncAsync();
        check(await db.AdInsights.CountAsync() == 4, "Meta: syncing again updates rows instead of duplicating them");
        var status = await meta.StatusAsync();
        check(status.Configured && status.Rows == 4 && status.LastSyncAt is not null && status.LastError is null && status.FirstDate == "2026-03-10", "Meta: the status shows the last sync and the stored range");

        var failing = new MetaAdsService(new HttpClient(new FakeMeta(_ => (HttpStatusCode.BadRequest, $"{{\"error\":{{\"message\":\"Invalid OAuth access token {Token}\"}}}}"))), db, new FixedOptions<MetaAdsOptions>(opts), NullLogger<MetaAdsService>.Instance);
        var bad = await failing.SyncAsync();
        var badStatus = await failing.StatusAsync();
        check(!bad.Ok && !bad.Message.Contains(Token) && badStatus.LastError is { } le && !le.Contains(Token) && le.Contains("Invalid OAuth"), "Meta: a refused request reports Meta's reason with the token removed");
        var off = await new MetaAdsService(new HttpClient(new FakeMeta(_ => (HttpStatusCode.OK, "{}"))), db, new FixedOptions<MetaAdsOptions>(new MetaAdsOptions()), NullLogger<MetaAdsService>.Instance).SyncAsync();
        check(!off.Ok && off.Message.Contains("not connected"), "Meta: without settings nothing is called and the message says why");

        // ---- with Meta: names, spend, cost, ROAS ------------------------------------------------------
        var withMeta = await svc.BuildAsync(march, null);
        var c1 = withMeta.Campaigns.Single(c => c.Key == "C1");
        check(withMeta.MetaConnected && c1.Campaign == "Spring Campaign" && c1.Leads == 2 && c1.Spend == 350.50m && c1.CostPerLead == 175.25m && c1.CostPerCustomer == 350.50m && c1.Roas == 4.28m, "Ads: with Meta, campaigns carry real names, spend, cost per lead, cost per customer and ROAS");
        check(withMeta.Ads.Single(a => a.Key == "111").Ad == "Summer Sale Video" && withMeta.Ads.Single(a => a.Key == "111").Spend == 350.50m, "Ads: the ad name comes from Meta and spend adds up across days");
        check(withMeta.Campaigns.Any(c => c.Key == "C3" && c.Leads == 0 && c.Spend == 50m && c.CostPerLead is null) && withMeta.Campaigns.Any(c => c.Key == "ad:333"), "Ads: a campaign with spend but no leads shows up, and an ad Meta does not know stays unnamed");
        check(withMeta.Kpis is { HasSpend: true, Spend: 470m, CostPerLead: 117.5m, CostPerCustomer: 470m, Roas: 3.19m, Impressions: 18800, Clicks: 520, Conversations: 12 }, "Ads: total spend, cost per lead, cost per customer, ROAS and reach add up");
        check(withMeta.Trend.Sum(t => t.Spend) == 470m, "Ads: spend per day adds up to the total");

        // ---- filters --------------------------------------------------------------------------------
        var byCamp = await svc.BuildAsync(march with { Campaign = "C1" }, null);
        check(byCamp.People.Count == 2 && byCamp.Kpis.Spend == 350.50m && byCamp.Kpis.CostPerLead == 175.25m && !byCamp.CostHidden, "Ads filter: one campaign narrows people and spend together");
        check(byCamp.Filters.Campaigns.Count == 4 && byCamp.Filters.Ads.Count == 1 && byCamp.Filters.Ads[0].Value == "111", "Ads filter: the campaign list stays complete and the ad list follows the campaign");
        var byAd = await svc.BuildAsync(march with { Ad = "222" }, null);
        check(byAd.People.Count == 1 && byAd.People[0].LeadId == l3.Id && byAd.Kpis.Spend == 69.50m, "Ads filter: one ad");
        var post = await svc.BuildAsync(march with { SourceType = "post" }, null);
        check(post.People.Count == 1 && post.People[0].LeadId == l4.Id && post.CostHidden && !post.Kpis.HasSpend && post.Kpis.CostPerLead is null, "Ads filter: posts only, and cost is hidden because spend cannot be split that way");
        check((await svc.BuildAsync(march with { Status = "customer" }, null)).People.Select(p => p.LeadId).SequenceEqual([l2.Id]), "Ads filter: status customer");
        check((await svc.BuildAsync(march with { Status = "interested" }, null)).People.Select(p => p.LeadId).SequenceEqual([l3.Id]), "Ads filter: status interested");
        check((await svc.BuildAsync(march with { CustomersOnly = true }, null)).People.Count == 1, "Ads filter: customers only");
        check((await svc.BuildAsync(march with { Owner = ownerB.Id.ToString() }, null)).People.Select(p => p.LeadId).SequenceEqual([l3.Id]), "Ads filter: one owner");
        check((await svc.BuildAsync(march with { Owner = "unassigned" }, null)).People.Select(p => p.LeadId).SequenceEqual([l4.Id]), "Ads filter: unassigned");
        var search = await svc.BuildAsync(march with { Q = "summer" }, null);
        check(search.People.Count == 2, "Ads filter: search looks at names, phone, ad and campaign");
        check((await svc.BuildAsync(march with { Q = "Summer Sale Video" }, null)).People.Count == 2 && (await svc.BuildAsync(march with { Q = "9700000003" }, null)).People.Single().LeadId == l3.Id && (await svc.BuildAsync(march with { Q = "chitra" }, null)).People.Count == 1, "Ads filter: search by ad name, phone digits and person name");
        var personFiltered = await svc.BuildAsync(march with { Status = "new" }, null);
        check(personFiltered.CostHidden && personFiltered.Kpis.CostPerLead is null && personFiltered.Campaigns.All(c => c.Spend == 0), "Ads filter: narrowing by status hides cost rather than showing a misleading figure");

        // ---- own records only -------------------------------------------------------------------------
        var mine = await svc.BuildAsync(march, ownerA.Id);
        check(mine.People.Count == 2 && mine.People.All(p => p.OwnerName == "Ads Owner A") && !mine.MetaConnected && !mine.Kpis.HasSpend && mine.ScopeNote is not null && mine.Campaigns.All(c => c.Spend == 0), "Ads scope: own records only shows the person's own leads, without spend or Meta-only campaigns");
        check(mine.Filters.Owners.Count == 1 && mine.Filters.Campaigns.All(c => c.Value != "C3"), "Ads scope: the filter lists do not reveal other people's leads or campaigns");

        // ---- the token never reaches a report -----------------------------------------------------------
        var json = JsonSerializer.Serialize(withMeta) + JsonSerializer.Serialize(await meta.StatusAsync()) + JsonSerializer.Serialize(await failing.StatusAsync());
        check(!json.Contains(Token), "Meta: the access token never appears in the report or the status");

        // ---- the connection entered in the app: saved encrypted, tested, synced on a schedule -------------------------
        const string AppToken = "EAAB-app-token-abcdefghijklmnopqrstuvwxyz1234";
        await using var cdb = new NuraDbContext(new DbContextOptionsBuilder<NuraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var jwt = Microsoft.Extensions.Options.Options.Create(new JwtOptions { Secret = "unit-test-signing-secret-0123456789abcdef" });
        var calls = new FakeMeta(r => r.RequestUri!.AbsolutePath.EndsWith("/insights")
            ? (HttpStatusCode.OK, "{\"data\":[" + Row("2026-03-12", "777", "App Ad", "App Set", "C7", "App Campaign", "10.00", "100", "5") + "]}")
            : (HttpStatusCode.OK, "{\"name\":\"Nura Herbex Ads\",\"currency\":\"INR\",\"account_status\":1}"));
        var app = new MetaAdsService(new HttpClient(calls), cdb, new FixedOptions<MetaAdsOptions>(new MetaAdsOptions()), NullLogger<MetaAdsService>.Instance, jwt);

        check(!(await app.IsConfiguredAsync()) && (await app.ConnectionAsync()).Source == "none", "Connect: nothing is connected before an account and token are entered");
        check(await app.SaveConnectionAsync(new MetaConnectionSaveRequest { AdAccountId = "abc", AccessToken = AppToken }, "Admin") is not null, "Connect: an account ID that is not a number is refused");
        check(await app.SaveConnectionAsync(new MetaConnectionSaveRequest { AdAccountId = "1234567890", AccessToken = "short" }, "Admin") is not null, "Connect: a token that is too short is refused");
        check(await app.SaveConnectionAsync(new MetaConnectionSaveRequest { AdAccountId = "1234567890" }, "Admin") is not null, "Connect: the first save needs a token");
        check(await app.SaveConnectionAsync(new MetaConnectionSaveRequest { AdAccountId = "1234567890", AccessToken = AppToken, ApiVersion = "latest" }, "Admin") is not null, "Connect: a bad API version is refused");
        check(await app.SaveConnectionAsync(new MetaConnectionSaveRequest { AdAccountId = "act_ 1234567890 ", AccessToken = AppToken }, "Admin") is null, "Connect: an account ID with act_ and spaces is accepted");

        var stored = await cdb.SiteSettings.AsNoTracking().FirstAsync(s => s.Key == "metaads.connection");
        check(!stored.Value.Contains(AppToken) && !stored.Value.Contains("abcdefghijklmnop"), "Connect: the token is stored encrypted, not as text");
        var conn = await app.ConnectionAsync();
        check(conn.Source == "app" && conn.AdAccountId == "1234567890" && conn.HasToken && conn.TokenHint == "1234" && !JsonSerializer.Serialize(conn).Contains(AppToken), "Connect: the browser only gets the last four characters of the token");
        check(await app.IsConfiguredAsync() && (await app.EffectiveAsync()).AccessToken == AppToken, "Connect: the saved details are used for syncing");

        var test = await app.TestAsync();
        check(test.Ok && test.AccountName == "Nura Herbex Ads" && test.Message.Contains("INR") && calls.Calls[^1].Url.Contains("act_1234567890?") && calls.Calls[^1].Auth == "Bearer " + AppToken && !calls.Calls[^1].Url.Contains(AppToken), "Connect: the test asks Meta for the account and reports its name");
        check((await app.StatusAsync()).AccountName == "Nura Herbex Ads", "Connect: the account name shows in the status");

        check(await app.SaveConnectionAsync(new MetaConnectionSaveRequest { AdAccountId = "1234567890", ApiVersion = "v22.0" }, "Admin") is null && (await app.EffectiveAsync()).AccessToken == AppToken && (await app.EffectiveAsync()).ApiVersion == "v22.0", "Connect: leaving the token blank keeps the saved one");

        var first = await app.SyncAsync(default, full: null);
        var firstUrl = calls.Calls[^1].Url;
        check(first.Ok && !first.Message.Contains("last 3 days") && await cdb.AdInsights.CountAsync() == 1, "Auto sync: the first run reads the whole window");
        var quick = await app.SyncAsync(default, full: null);
        var quickUrl = Uri.UnescapeDataString(calls.Calls[^1].Url);
        var since = DateTime.Parse(System.Text.RegularExpressions.Regex.Match(quickUrl, "\"since\":\"([0-9-]+)\"").Groups[1].Value);
        check(quick.Ok && quick.Message.Contains("last 3 days") && (FollowUpService.IstNow.Date - since).Days == 3 && firstUrl != calls.Calls[^1].Url, "Auto sync: later runs only re-read the last three days");
        var st = await app.StatusAsync();
        check(st.IntervalMinutes == 10 && st.NextSyncAt is { } next && next > DateTime.UtcNow && next < DateTime.UtcNow.AddMinutes(11) && !st.Syncing && st.Source == "app", "Auto sync: the schedule is every 10 minutes and the status shows when the next one is due");
        check(MetaAdsService.ClampMinutes(0) == 10 && MetaAdsService.ClampMinutes(1) == 5 && MetaAdsService.ClampMinutes(9999) == 360 && MetaAdsService.ClampMinutes(15) == 15, "Auto sync: the interval is kept between 5 minutes and 6 hours");

        var wrong = new MetaAdsService(new HttpClient(new FakeMeta(_ => (HttpStatusCode.BadRequest, $"{{\"error\":{{\"message\":\"Invalid token {AppToken}\"}}}}"))), cdb, new FixedOptions<MetaAdsOptions>(new MetaAdsOptions()), NullLogger<MetaAdsService>.Instance, jwt);
        var badTest = await wrong.TestAsync();
        check(!badTest.Ok && !badTest.Message.Contains(AppToken) && badTest.Message.Contains("Invalid token"), "Connect: a wrong token is reported by Meta's reason with the token removed");

        var otherSecret = new MetaAdsService(new HttpClient(calls), cdb, new FixedOptions<MetaAdsOptions>(new MetaAdsOptions()), NullLogger<MetaAdsService>.Instance, Microsoft.Extensions.Options.Options.Create(new JwtOptions { Secret = "a-different-signing-secret-0123456789abcdef" }));
        check(!await otherSecret.IsConfiguredAsync(), "Connect: if the server's signing secret changes the saved token is not usable and has to be entered again");

        await app.ClearConnectionAsync();
        check(!await app.IsConfiguredAsync() && (await app.ConnectionAsync()).Source == "none" && await cdb.AdInsights.AnyAsync(), "Connect: disconnecting stops syncing but keeps the numbers already stored");
        var fromServer = new MetaAdsService(new HttpClient(calls), cdb, new FixedOptions<MetaAdsOptions>(new MetaAdsOptions { AdAccountId = "555555", AccessToken = AppToken }), NullLogger<MetaAdsService>.Instance, jwt);
        check((await fromServer.ConnectionAsync()).Source == "server" && await fromServer.IsConfiguredAsync(), "Connect: server settings still work when nothing was saved in the app");
    }
}
