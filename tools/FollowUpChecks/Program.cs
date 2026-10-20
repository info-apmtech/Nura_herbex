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

// Offline checks for follow-ups: journey scheduling, reminders, escalation, order-progress tasks, completion rules,
// automatic sending and reports. Uses an in-memory database and a fake WhatsApp endpoint; nothing external is contacted.

int checks = 0;
void Check(bool value, string label) { if (!value) throw new Exception("FAIL " + label); checks++; Console.WriteLine("PASS " + label); }

var sent = new List<string>();
var http = new HttpClient(new FakeWhatsApp(sent));
var dbOptions = new DbContextOptionsBuilder<NuraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
await using var db = new NuraDbContext(dbOptions);

var waOptions = Options.Create(new WhatsAppOptions { AccessToken = "x", PhoneNumberId = "1", DefaultCountryCode = "91" });
var fuOptions = new FollowUpOptions();
fuOptions.Templates["day3-usage"] = new FollowUpTemplate { Name = "usage_check", Params = ["name"] };
var leads = new LeadService(db, waOptions, NullLogger<LeadService>.Instance);
var wa = new WhatsAppService(db, http, leads, waOptions, NullLogger<WhatsAppService>.Instance);
var svc = new FollowUpService(db, leads, wa, Options.Create(fuOptions), Options.Create(new StoreOptions { FrontendUrl = "https://shop.test" }), NullLogger<FollowUpService>.Instance);

// A fixed "now": Monday 12 Oct 2026, 11:00 IST (05:30 UTC).
var now = new DateTime(2026, 10, 12, 5, 30, 0, DateTimeKind.Utc);
FollowUpService.Clock = () => now;
var ist = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "India Standard Time" : "Asia/Kolkata");
DateTime Ist(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, ist);

db.Employees.AddRange(new Employee { Name = "Priya", Email = "p@x.test", Role = "sales" }, new Employee { Name = "Karthik", Email = "k@x.test", Role = "sales" });
await db.SaveChangesAsync();
var priya = db.Employees.First(e => e.Name == "Priya").Id; var karthik = db.Employees.First(e => e.Name == "Karthik").Id;
// Go-live was yesterday, so test orders (created "today") count.
db.SiteSettings.Add(new SiteSetting { Key = "followups.startedAt", Value = now.AddDays(-1).ToString("O") });
await db.SaveChangesAsync();

Order MakeOrder(string id, string phone, string fulfillment = "DELIVERED", DateTime? created = null, DateTime? updated = null, string? awb = "AWB1") => new()
{
    Id = id, CustomerName = "Asha Raman", CustomerEmail = "a@x.test", CustomerPhone = phone, PaymentMethod = "COD", PaymentStatus = "COD_PENDING",
    FulfillmentStatus = fulfillment, DeliveredAt = fulfillment == "DELIVERED" ? now : null, CreatedAt = created ?? now.AddHours(-1), UpdatedAt = updated ?? now,
    ShiprocketAwb = awb, TotalAmount = 1000,
};

// ---- 1. Delivered order becomes a lead + 15-day journey -------------------------------------------------
db.Orders.Add(MakeOrder("NH-1", "9876500001"));
await db.SaveChangesAsync();
await svc.RunCycleAsync();
var lead = await db.Leads.SingleAsync(l => l.Phone == "919876500001");
Check(lead.Status == LeadStatuses.Converted && lead.Source == "website", "Delivered order creates a Converted lead");
Check(lead.AssignedToEmployeeId is not null, "That lead is assigned to a sales employee");
var steps = await db.FollowUps.Where(f => f.LeadId == lead.Id).OrderBy(f => f.DueAt).ToListAsync();
Check(steps.Count == JourneyPlan.Steps.Length && steps.All(s => s.Source == "journey" && s.OrderId == "NH-1"), "Journey creates one task per step");
var day1 = steps.First(s => s.StepKey == "day1-welcome");
Check(Ist(day1.DueAt).Date == new DateTime(2026, 10, 13) && Ist(day1.DueAt).Hour == 10, "Day 1 is due tomorrow at 10:00 IST");
Check(Ist(steps.First(s => s.StepKey == "day10-call").DueAt).Date == new DateTime(2026, 10, 22), "Day 10 call is due 10 days after delivery");
Check(day1.RemindAt is null, "Day 1 has no advance reminder (already inside 2 days)");
var call = steps.First(s => s.StepKey == "day10-call");
Check(call.RemindAt == call.DueAt.AddDays(-2), "Day 10 call is reminded 2 days before");
Check(steps.First(s => s.StepKey == "day8-review").SuggestedMessage!.Contains("https://shop.test/#reviews"), "Review step carries the review link");
Check(!steps.First(s => s.StepKey == "day3-usage").SuggestedMessage!.Contains('{'), "Message placeholders are filled");
Check(call.SuggestedMessage is null && call.Type == FollowUpTypes.Call, "Call step is a task for the employee, not a message");

// ---- 2. Idempotent --------------------------------------------------------------------------------------
await svc.RunCycleAsync();
Check(await db.FollowUps.CountAsync(f => f.LeadId == lead.Id) == JourneyPlan.Steps.Length && await db.JourneyEnrollments.CountAsync() == 1 && await db.Leads.CountAsync() == 1, "Running the cycle again creates nothing new");

// ---- 3. Journey switch ----------------------------------------------------------------------------------
await svc.SaveSettingsAsync(new FollowUpSettingsRequest { JourneyEnabled = false, AutoSend = false, ReminderDays = 2 });
db.Orders.Add(MakeOrder("NH-2", "9876500002"));
await db.SaveChangesAsync();
await svc.RunCycleAsync();
Check(!await db.JourneyEnrollments.AnyAsync(e => e.OrderId == "NH-2"), "Journey off: no enrollment for new deliveries");
await svc.SaveSettingsAsync(new FollowUpSettingsRequest { JourneyEnabled = true, AutoSend = false, ReminderDays = 2 });
await svc.RunCycleAsync();
Check(await db.JourneyEnrollments.AnyAsync(e => e.OrderId == "NH-2"), "Journey back on: the delivery is enrolled");

// ---- 4. Advance reminder (2 days before) -----------------------------------------------------------------
var day3 = steps.First(s => s.StepKey == "day3-usage");
var day3Row = await db.FollowUps.FirstAsync(f => f.Id == day3.Id);
day3Row.DueAt = now.AddDays(2); day3Row.RemindAt = now.AddMinutes(-1); day3Row.ReminderSentAt = null; day3Row.AssignedToEmployeeId = priya;
await db.SaveChangesAsync();
await svc.RunCycleAsync();
var heads = await db.Notifications.Where(n => n.Kind == "reminder" && n.Title.Contains("Coming up in 2 days")).ToListAsync();
Check(heads.Count >= 1 && heads.All(h => h.EmployeeId is not null), "A heads-up notification is sent 2 days before");
Check((await db.FollowUps.FirstAsync(f => f.Id == day3.Id)).ReminderSentAt is not null, "Reminder is marked as sent");
var before = await db.Notifications.CountAsync(n => n.Kind == "reminder");
await svc.RunCycleAsync();
Check(await db.Notifications.CountAsync(n => n.Kind == "reminder") == before, "The heads-up is not repeated");

// ---- 5. Due now + 6. Escalation -----------------------------------------------------------------------------
var d1 = await db.FollowUps.FirstAsync(f => f.Id == day1.Id);
d1.DueAt = now.AddHours(-1); d1.DueNotifiedAt = null; d1.AssignedToEmployeeId = priya;
var d7 = await db.FollowUps.FirstAsync(f => f.StepKey == "day7-experience" && f.LeadId == lead.Id);
d7.DueAt = now.AddHours(-30); d7.DueNotifiedAt = now; d7.AssignedToEmployeeId = karthik;
await db.SaveChangesAsync();
await svc.RunCycleAsync();
Check(await db.Notifications.AnyAsync(n => n.Kind == "due" && n.EmployeeId == priya), "Due-now notice goes to the owner");
Check(await db.Notifications.AnyAsync(n => n.Kind == "overdue" && n.EmployeeId == null), "A task a day late is escalated to administrators");
var overdueCount = await db.Notifications.CountAsync(n => n.Kind == "overdue");
await svc.RunCycleAsync();
Check(await db.Notifications.CountAsync(n => n.Kind == "overdue") == overdueCount, "Escalation happens once");

// ---- 7. Order-progress tasks -------------------------------------------------------------------------------
db.Orders.AddRange(
    MakeOrder("NH-3", "9876500003", "PENDING", now.AddHours(-3), awb: null),
    MakeOrder("NH-4", "9876500004", "IN_TRANSIT", now.AddDays(-5), now.AddDays(-4)),
    MakeOrder("NH-5", "9876500005", "RTO", now.AddHours(-30)));
await db.SaveChangesAsync();
db.SiteSettings.First(s => s.Key == "followups.startedAt").Value = now.AddDays(-10).ToString("O");
await db.SaveChangesAsync();
await svc.RunCycleAsync();
Check(await db.FollowUps.AnyAsync(f => f.OrderId == "NH-3" && f.StepKey == "order-unbooked" && f.Type == FollowUpTypes.Order), "Paid order with no courier booking raises a task");
Check(await db.FollowUps.AnyAsync(f => f.OrderId == "NH-4" && f.StepKey == "order-stuck"), "Shipment not moving for 3 days raises a task");
Check(await db.FollowUps.AnyAsync(f => f.OrderId == "NH-5" && f.StepKey == "order-rto"), "Return-to-origin raises a task");
await svc.RunCycleAsync();
Check(await db.FollowUps.CountAsync(f => f.StepKey == "order-unbooked") == 1, "Order tasks are not duplicated");
Check(!await db.FollowUps.AnyAsync(f => f.OrderId == "NH-1" && f.StepKey!.StartsWith("order-")), "A healthy delivered order raises no order task");

// ---- 8. Manual follow-ups ------------------------------------------------------------------------------------
var owned = await db.Leads.FirstAsync(l => l.Id == lead.Id);
await leads.AssignAsync(owned, priya, "test", "test"); await db.SaveChangesAsync();
var (none, errPast) = await svc.CreateManualAsync(new FollowUpCreateRequest { LeadId = lead.Id, Type = "call", DueAt = now.AddHours(-3) }, "Priya", false, priya);
Check(none is null && errPast is not null, "A follow-up in the past is rejected");
var (noAccess, errOwner) = await svc.CreateManualAsync(new FollowUpCreateRequest { LeadId = lead.Id, Type = "call", DueAt = now.AddDays(1) }, "Karthik", false, karthik);
Check(noAccess is null && errOwner == "Lead not found.", "A sales user cannot schedule on a colleague's lead");
var fresh = await db.Leads.FirstAsync(l => l.Phone == "919876500002");
fresh.Status = LeadStatuses.New; fresh.AssignedToEmployeeId = priya; await db.SaveChangesAsync();
var (mine, _) = await svc.CreateManualAsync(new FollowUpCreateRequest { LeadId = fresh.Id, Type = "call", DueAt = now.AddDays(5), Notes = "Wants 3-month pack" }, "Priya", false, priya);
Check(mine is not null && mine.RemindAt == mine.DueAt.AddDays(-2) && mine.AssignedToEmployeeId == priya, "Manual follow-up is owned by the lead owner with a 2-day reminder");
Check((await db.Leads.FirstAsync(l => l.Id == fresh.Id)).Status == LeadStatuses.FollowUp, "Scheduling moves a new lead to Follow-up");

// ---- 9. Completing ---------------------------------------------------------------------------------------------
var (ok1, _) = await svc.CompleteAsync(await db.FollowUps.FirstAsync(f => f.Id == mine!.Id), new FollowUpCompleteRequest { Outcome = FollowUpOutcomes.Callback }, "Priya");
Check(!ok1, "Callback without a time is rejected");
var (ok2, msg2) = await svc.CompleteAsync(await db.FollowUps.FirstAsync(f => f.Id == mine!.Id), new FollowUpCompleteRequest { Outcome = FollowUpOutcomes.NoAnswer }, "Priya");
var retry = await db.FollowUps.FirstOrDefaultAsync(f => f.LeadId == fresh.Id && f.Status == "pending" && f.Title.StartsWith("Retry: "));
Check(ok2 && retry is not null && retry.Title.StartsWith("Retry: ") && Ist(retry.DueAt).Date == new DateTime(2026, 10, 13) && Ist(retry.DueAt).Hour == 10, "No answer books a retry for tomorrow 10:00");
var (ok3, _) = await svc.CompleteAsync(retry!, new FollowUpCompleteRequest { Outcome = FollowUpOutcomes.Interested, Note = "Will order Friday" }, "Priya");
Check(ok3 && (await db.Leads.FirstAsync(l => l.Id == fresh.Id)).Status == LeadStatuses.Interested, "Outcome 'interested' updates the lead");
var (again, _) = await svc.CompleteAsync(retry!, new FollowUpCompleteRequest { Outcome = FollowUpOutcomes.Done }, "Priya");
Check(!again, "A closed follow-up cannot be completed twice");

// finish a whole journey -> enrollment completes
foreach (var s in await db.FollowUps.Where(f => f.EnrollmentId != null && f.OrderId == "NH-2" && f.Status == "pending").ToListAsync())
    await svc.CompleteAsync(s, new FollowUpCompleteRequest { Outcome = FollowUpOutcomes.Done }, "Karthik");
Check((await db.JourneyEnrollments.FirstAsync(e => e.OrderId == "NH-2")).Status == "completed", "Finishing every step completes the journey");

// ---- 10. Automatic sending -------------------------------------------------------------------------------------
lead = await db.Leads.FirstAsync(l => l.Id == lead.Id);
var autoStep = await db.FollowUps.FirstAsync(f => f.Id == day3.Id);
autoStep.Status = "pending"; autoStep.CompletedAt = null; autoStep.DueAt = now.AddHours(-1); autoStep.CompletedBy = null;
await db.SaveChangesAsync();
await svc.RunCycleAsync();
Check(sent.Count == 0, "Auto-send is off by default: nothing is sent");
await svc.SaveSettingsAsync(new FollowUpSettingsRequest { JourneyEnabled = true, AutoSend = true, ReminderDays = 2 });
await svc.RunCycleAsync();
Check(sent.Count == 0, "Auto-send on but customer has not opted in: nothing is sent");
lead.ConsentStatus = "opted_in"; await db.SaveChangesAsync();
now = new DateTime(2026, 10, 12, 17, 30, 0, DateTimeKind.Utc); // 23:00 IST
await svc.RunCycleAsync();
Check(sent.Count == 0, "Outside 9am-8pm IST nothing is sent");
now = new DateTime(2026, 10, 12, 5, 30, 0, DateTimeKind.Utc);
await svc.RunCycleAsync();
Check(sent.Count == 1 && sent[0].Contains("usage_check") && sent[0].Contains("919876500001"), "Opted-in customer receives the approved template");
var done = await db.FollowUps.FirstAsync(f => f.Id == day3.Id);
Check(done.Status == "done" && done.CompletedBy == "auto", "The task is closed as sent automatically");
Check(!sent.Any(s => s.Contains("day1") || s.Contains("review")), "Steps without a template are never auto-sent");

// ---- 11. Send now (employee presses Send) ----------------------------------------------------------------------
var day7 = await db.FollowUps.FirstAsync(f => f.LeadId == lead.Id && f.StepKey == "day7-experience");
var (closedWin, closedMsg) = await svc.SendNowAsync(day7, "Priya");
Check(!closedWin && closedMsg.Contains("24-hour"), "Send now refuses when the window is closed and no template exists");
var convo = await db.Conversations.FirstOrDefaultAsync(c => c.LeadId == lead.Id); if (convo is null) { convo = new Conversation { LeadId = lead.Id }; db.Conversations.Add(convo); } convo.ServiceWindowExpiresAt = now.AddHours(5); convo.LastInboundAt = now;
await db.SaveChangesAsync();
var (okSend, sendMsg) = await svc.SendNowAsync(day7, "Priya");
Check(okSend && sent.Count == 2 && (await db.FollowUps.FirstAsync(f => f.Id == day7.Id)).Outcome == "Sent on WhatsApp", "Send now works inside the 24-hour window");
lead.ConsentStatus = "opted_out"; await db.SaveChangesAsync();
var day12 = await db.FollowUps.FirstAsync(f => f.LeadId == lead.Id && f.StepKey == "day12-reorder");
var (optedOut, _) = await svc.SendNowAsync(day12, "Priya");
Check(!optedOut, "Send now refuses an opted-out customer");

// ---- 12. Reports -------------------------------------------------------------------------------------------------
var report = await svc.ReportAsync(30, null);
Check(report.Due > 0 && report.Done > 0 && report.CompletionRate is > 0 and <= 100, "Report totals and completion rate");
Check(report.Journey.Count == JourneyPlan.Steps.Length && report.Journey.Any(j => j.Done > 0), "Report has the 15-day funnel");
Check(report.ByEmployee.Any(r => r.Label == "Priya") && report.ByType.Count > 0 && report.Trend.Count > 0, "Report splits by employee, type and day");
Check(report.AutoSent == 1 && report.ConvertedLeads >= 1 && report.JourneysStarted == 2, "Report counts automatic sends, conversions and journeys");
var mineOnly = await svc.ReportAsync(30, karthik);
Check(mineOnly.ByEmployee.All(r => r.EmployeeId == karthik) && mineOnly.Due <= report.Due, "A sales report only contains their own work");

// ---- 13. Who sent a message, and first-reply speed on the dashboard -----------------------------------------------
var sentMsg = await db.WhatsAppMessages.Where(m => m.Direction == "outbound" && m.Body == day7.SuggestedMessage).OrderByDescending(m => m.Id).FirstAsync();
Check(sentMsg.SentBy == "Priya", "A message sent by a person records who sent it");
Check((await db.WhatsAppMessages.Where(m => m.Direction == "outbound" && m.Type == "template").ToListAsync()).All(m => m.SentBy is "system" or "Priya" or "auto"), "Automatic messages are marked as system");
lead = await db.Leads.FirstAsync(l => l.Phone == "919876500002");
var t0 = FollowUpService.Clock().AddHours(-3);
db.WhatsAppMessages.AddRange(
    new WhatsAppMessage { LeadId = lead.Id, Direction = "inbound", WaMessageId = "in-a", Phone = lead.Phone, CreatedAt = t0 },
    new WhatsAppMessage { LeadId = lead.Id, Direction = "outbound", WaMessageId = "out-bot", Phone = lead.Phone, CreatedAt = t0.AddMinutes(1), SentBy = "system" },
    new WhatsAppMessage { LeadId = lead.Id, Direction = "outbound", WaMessageId = "out-human", Phone = lead.Phone, CreatedAt = t0.AddMinutes(12), SentBy = "Priya" });
var lead3 = await db.Leads.FirstAsync(l => l.Phone == "919876500003");
db.WhatsAppMessages.AddRange(
    new WhatsAppMessage { LeadId = lead3.Id, Direction = "inbound", WaMessageId = "in-b", Phone = lead3.Phone, CreatedAt = t0 },
    new WhatsAppMessage { LeadId = lead3.Id, Direction = "outbound", WaMessageId = "out-bot2", Phone = lead3.Phone, CreatedAt = t0.AddMinutes(1), SentBy = "system" });
await db.SaveChangesAsync();
(await db.FollowUps.FirstAsync(f => f.Status == "pending")).DueAt = FollowUpService.Clock().AddDays(-3);
await db.SaveChangesAsync();
var dash = await new CrmDashboardService(db).BuildAsync(30, null);
Check(dash.MedianFirstReplyMinutes == 12, "First reply time counts the first person's reply, not the auto-reply");
Check(dash.AwaitingFirstReply >= 1, "A message answered only by the auto-reply counts as waiting");
Check(dash.NewLeads > 0 && dash.Funnel.Count == 5 && dash.ByEmployee.Count == 2, "Dashboard has leads, funnel and a row per employee");
Check(dash.NeedsAttention.Any(a => a.Reason.StartsWith("Overdue")) , "Overdue follow-ups show up under needs attention");

// ---- 14. Reply assistant: drafted replies (built-in guide when no AI key) ------------------------------------------
db.Products.AddRange(new Product { Id = "p1", Sku = "S1", Name = "Stamix Canister", Price = 1899, Status = "active" }, new Product { Id = "p2", Sku = "S2", Name = "Stamix Duo", Price = 3499, Status = "active" });
await db.SaveChangesAsync();
var assistant = new ReplyAssistantService(db, Options.Create(new AiOptions()), Options.Create(new StoreOptions { FreeShippingThreshold = 999, FlatShippingFee = 99 }), new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()), NullLogger<ReplyAssistantService>.Instance);
var rl = new Lead { Id = 900, Name = "Asha Raman", Phone = "919876509999" };
var prods = await db.Products.ToListAsync();
ReplySuggestionResponse G(string q, List<Order>? o = null) => assistant.GuideFor(rl, q, prods, o ?? []);
Check(G("Hi").Intent == "Greeting" && G("Hi").Reply.Contains("Asha"), "Guide: greets the customer by first name");
Check(G("What is the price of stamix?").Reply.Contains("Stamix Canister") && G("What is the price of stamix?").Reply.Contains("1,899"), "Guide: price question lists real products and prices");
Check(G("how many days for delivery to madurai").Reply.Contains("pincode") && G("how many days for delivery to madurai").Reply.Contains("999"), "Guide: delivery question asks for the pincode, no invented dates");
Check(G("is cod available").Intent == "Payment question" && G("is cod available").Reply.ToLower().Contains("cash on delivery"), "Guide: payment question");
Check(G("I want to buy 2 packs").Reply.Contains("address"), "Guide: buying intent asks for name, address and pack");
var health = G("Can I take this with my diabetes tablets?");
Check(health.NeedsReview && health.Reply.ToLower().Contains("doctor") && !health.Reply.ToLower().Contains("cure"), "Guide: health question never gives advice and is flagged for review");
Check(G("the package arrived damaged").Intent == "Complaint" && G("the package arrived damaged").NeedsReview, "Guide: a damaged parcel is a complaint, not a health question");
var withOrder = new List<Order> { new() { Id = "NH-77", DeliveryStatus = "Shipment Created with Shadowfax", Courier = "Shadowfax", ShiprocketAwb = "SF123" } };
Check(G("where is my order", withOrder).Reply.Contains("NH-77") && G("where is my order", withOrder).Reply.Contains("SF123"), "Guide: order status uses the customer's real order");
Check(G("I want to order again", withOrder).Intent == "Wants to order", "Guide: a repeat order request is not treated as a status check");
Check(G("asdf qwerty").NeedsReview && G("asdf qwerty").Intent == "General question", "Guide: an unknown question is flagged so a person writes it");
Check(G(null).Intent == "Introduction", "Guide: a lead with no message gets a polite opening");

var ai1 = ReplyAssistantService.ParseModelOutput("```json\n{\"reply\":\"Hello Asha, the Duo pack is Rs 3,499.\",\"intent\":\"Price question\",\"needs_review\":false,\"review_note\":\"\"}\n```");
Check(ai1 is { Reply: "Hello Asha, the Duo pack is Rs 3,499.", Intent: "Price question", NeedsReview: false, ReviewNote: null }, "AI output: JSON inside a code fence is read correctly");
var ai2 = ReplyAssistantService.ParseModelOutput("{\"reply\":\"Please consult your doctor.\",\"intent\":\"Health question\",\"needs_review\":true,\"review_note\":\"Medical question\"}");
Check(ai2 is { NeedsReview: true, ReviewNote: "Medical question" }, "AI output: review flag and note are kept");
var ai3 = ReplyAssistantService.ParseModelOutput("Sure! Hello Asha, here is the price.");
Check(ai3 is { NeedsReview: true } && ai3.Reply.StartsWith("Sure!"), "AI output: if the model ignores the format its text is used but flagged");
Check(ReplyAssistantService.ParseModelOutput("  ") is null, "AI output: empty answer is rejected");

var rl2 = await db.Leads.FirstAsync(l => l.Phone == "919876500001");
db.WhatsAppMessages.Add(new WhatsAppMessage { LeadId = rl2.Id, Direction = "inbound", WaMessageId = "q-1", Phone = rl2.Phone, Body = "Price of stamix please?", CreatedAt = FollowUpService.Clock() });
await db.SaveChangesAsync();
var s1 = await assistant.SuggestAsync(rl2, new ReplySuggestRequest());
Check(s1.Success && s1.Source == "guide" && s1.Intent == "Price question" && s1.RespondingTo!.StartsWith("Price"), "Suggest: answers the customer's latest message from the guide when there is no AI key");
var s2 = await assistant.SuggestAsync(rl2, new ReplySuggestRequest());
Check(ReferenceEquals(s1, s2), "Suggest: the same question is answered from memory, not regenerated");
var s3 = await assistant.SuggestAsync(rl2, new ReplySuggestRequest { Instruction = "shorter", CurrentDraft = "My own edited text" });
Check(s3.Reply == "My own edited text" && s3.ReviewNote is not null, "Suggest: a change that cannot be made leaves the persons text and says why");
await assistant.SaveSettingsAsync(new ReplyAssistantSettingsRequest { Enabled = false, Knowledge = "x" });
Check(!(await assistant.SuggestAsync(rl2, new ReplySuggestRequest { Fresh = true })).Success, "Suggest: switched off by an administrator");
await assistant.SaveSettingsAsync(new ReplyAssistantSettingsRequest { Enabled = true, Knowledge = "" });
Check((await assistant.GetSettingsAsync()).Knowledge.Contains("Never promise medical results"), "Settings: an empty knowledge box falls back to the safe default text");

// ---- 15. Customer photos, stickers, voice notes, documents and reactions ------------------------------------------------
var mediaDir = Path.Combine(Path.GetTempPath(), "nura-wa-media-" + Guid.NewGuid().ToString("N"));
var mediaFake = new MediaFake();
var waMedia = new WhatsAppService(db, new HttpClient(mediaFake), leads, Options.Create(new WhatsAppOptions { AccessToken = "tok", PhoneNumberId = "1", DefaultCountryCode = "91", MediaPath = mediaDir, AutoReplyEnabled = false }), NullLogger<WhatsAppService>.Instance);
string Hook(string phone, object message) => JsonSerializer.Serialize(new { @object = "whatsapp_business_account", entry = new[] { new { id = "1", changes = new[] { new { field = "messages", value = new { messaging_product = "whatsapp", contacts = new[] { new { wa_id = phone, profile = new { name = "Media Tester" } } }, messages = new[] { message } } } } } } });
const string P = "919000011111";

await waMedia.ProcessWebhookAsync(Hook(P, new { from = P, id = "wamid.st1", timestamp = "1", type = "sticker", sticker = new { id = "MED-ST", mime_type = "image/webp", animated = false } }));
var st = await db.WhatsAppMessages.SingleAsync(m => m.WaMessageId == "wamid.st1");
Check(st.Type == "sticker" && st.MediaId == "MED-ST" && st.Body is null, "Sticker is stored as a sticker with its media id and no fake text");
Check(st.MediaFile is { } sf && sf.EndsWith(".webp") && File.ReadAllBytes(Path.Combine(mediaDir, sf)).SequenceEqual(mediaFake.Bytes), "The sticker file is downloaded and kept");
Check(mediaFake.Auths.All(a => a == "Bearer tok") && mediaFake.Auths.Count >= 2, "Media is fetched with the WhatsApp access token");
Check((await db.Leads.SingleAsync(l => l.Phone == P)).FirstMessage == "Sticker", "The first message of the lead reads Sticker, not a code");

await waMedia.ProcessWebhookAsync(Hook(P, new { from = P, id = "wamid.im1", timestamp = "2", type = "image", image = new { id = "MED-IM", mime_type = "image/jpeg", caption = "This is the damaged pack" } }));
var im = await db.WhatsAppMessages.SingleAsync(m => m.WaMessageId == "wamid.im1");
Check(im.Body == "This is the damaged pack" && im.MediaFile!.EndsWith(".jpg"), "A photo keeps its caption and is downloaded");

await waMedia.ProcessWebhookAsync(Hook(P, new { from = P, id = "wamid.doc1", timestamp = "3", type = "document", document = new { id = "MED-DOC", mime_type = "application/pdf", filename = "invoice.pdf" } }));
var dc = await db.WhatsAppMessages.SingleAsync(m => m.WaMessageId == "wamid.doc1");
Check(dc.MediaName == "invoice.pdf" && dc.MediaFile!.EndsWith(".pdf"), "A document keeps its file name");

await waMedia.ProcessWebhookAsync(Hook(P, new { from = P, id = "wamid.bad1", timestamp = "4", type = "audio", audio = new { id = "BAD", mime_type = "audio/ogg", voice = true } }));
var bad = await db.WhatsAppMessages.SingleAsync(m => m.WaMessageId == "wamid.bad1");
Check(bad.Type == "audio" && bad.MediaFile is null, "If Meta will not give the file the message is still saved");
mediaFake.Bad.Clear();
Check(await waMedia.DownloadMediaAsync(bad) && bad.MediaFile!.EndsWith(".ogg"), "Opening it later fetches the file");

await waMedia.ProcessWebhookAsync(Hook(P, new { from = P, id = "wamid.evil", timestamp = "5", type = "image", image = new { id = "EVIL", mime_type = "image/png" } }));
Check((await db.WhatsAppMessages.SingleAsync(m => m.WaMessageId == "wamid.evil")).MediaFile is null, "A download address that is not Meta is refused");

await waMedia.ProcessWebhookAsync(Hook(P, new { from = P, id = "wamid.loc", timestamp = "6", type = "location", location = new { latitude = 13.0827, longitude = 80.2707, name = "Anna Nagar", address = "Chennai" } }));
Check((await db.WhatsAppMessages.SingleAsync(m => m.WaMessageId == "wamid.loc")).Body!.Contains("https://maps.google.com/?q=13.0827,80.2707"), "A shared location becomes a map link");

// reactions
var mediaLead = await db.Leads.SingleAsync(l => l.Phone == P);
db.WhatsAppMessages.Add(new WhatsAppMessage { LeadId = mediaLead.Id, Direction = "outbound", WaMessageId = "wamid.OUT1", Phone = P, Body = "Hello", Status = "delivered", SentBy = "Priya" });
await db.SaveChangesAsync();
var unreadBefore = (await db.Conversations.SingleAsync(c => c.LeadId == mediaLead.Id)).UnreadCount;
var countBefore = await db.WhatsAppMessages.CountAsync();
await waMedia.ProcessWebhookAsync(Hook(P, new { from = P, id = "wamid.rx1", timestamp = "7", type = "reaction", reaction = new { message_id = "wamid.OUT1", emoji = "👍" } }));
Check((await db.WhatsAppMessages.SingleAsync(m => m.WaMessageId == "wamid.OUT1")).Reaction == "👍", "A reaction is shown on the message it was left on");
Check(await db.WhatsAppMessages.CountAsync() == countBefore && (await db.Conversations.SingleAsync(c => c.LeadId == mediaLead.Id)).UnreadCount == unreadBefore, "A reaction is not a new message and does not add unread");
await waMedia.ProcessWebhookAsync(Hook(P, new { from = P, id = "wamid.rx2", timestamp = "8", type = "reaction", reaction = new { message_id = "wamid.OUT1" } }));
Check((await db.WhatsAppMessages.SingleAsync(m => m.WaMessageId == "wamid.OUT1")).Reaction is null, "Removing the reaction clears it");
await waMedia.ProcessWebhookAsync(Hook(P, new { from = P, id = "wamid.rx3", timestamp = "9", type = "reaction", reaction = new { message_id = "wamid.unknown", emoji = "❤️" } }));
Check(true, "A reaction to an unknown message is ignored without error");

// signed links and the message shape the app receives
var signer = new MediaSigner(Options.Create(new JwtOptions { Secret = new string('k', 40) }));
var url = signer.Url(st.Id);
var q = System.Web.HttpUtility.ParseQueryString(url[(url.IndexOf('?') + 1)..]);
Check(signer.IsValid(st.Id, long.Parse(q["exp"]!), q["sig"]), "A signed media link is accepted");
Check(!signer.IsValid(st.Id + 1, long.Parse(q["exp"]!), q["sig"]) && !signer.IsValid(st.Id, long.Parse(q["exp"]!), q["sig"]![..^2] + "00"), "A link for another file or a tampered signature is refused");
Check(!signer.IsValid(st.Id, long.Parse(q["exp"]!), q["sig"], DateTime.UtcNow.AddHours(3)), "A link stops working after two hours");
Check(st.ToDto(signer).MediaUrl is { } mu && mu.StartsWith("api/whatsapp-media/") && st.ToDto(signer).MediaMime == "image/webp", "The app receives a signed link for a sticker");
Check(new WhatsAppMessage { Type = "text", Body = "hi" }.ToDto(signer).MediaUrl is null, "A text message has no media link");
Check(new WhatsAppMessage { Id = 5, Type = "image", MediaId = "x" }.ToDto(null).MediaUrl is null, "Without a signer no link is produced");
Check(G("[sticker]").Intent == "Sticker" && G("[photo]").Intent == "Shared a file" && G("[photo]").NeedsReview, "Reply guide: a sticker gets a friendly hello, a photo asks the team to look first");
try { Directory.Delete(mediaDir, true); } catch { }
// earlier messages stored as "[sticker]" get their media id back from the saved payload
db.WhatsAppMessages.Add(new WhatsAppMessage { Direction = "inbound", WaMessageId = "old-st", Phone = P, Type = "sticker", Body = "[sticker]", Status = "received", RawPayload = "{\"type\":\"sticker\",\"sticker\":{\"id\":\"OLD-ID\",\"mime_type\":\"image/webp\"}}" });
await db.SaveChangesAsync();
await WhatsAppMediaBackfill.RunAsync(db, NullLogger.Instance);
var oldSt = await db.WhatsAppMessages.SingleAsync(m => m.WaMessageId == "old-st");
Check(oldSt.MediaId == "OLD-ID" && oldSt.MediaMime == "image/webp" && oldSt.Body is null, "Earlier sticker messages are re-linked to their media and lose the placeholder text");


// ---- 16. Tweaks work without an AI key: shorter, friendlier, more formal, Tamil, Hindi -----------------------------
var tq = "What is the price of stamix?";
var base0 = assistant.GuideFor(rl, tq, prods, []);
var tShort = assistant.GuideFor(rl, tq, prods, [], base0.Reply, "make it shorter");
Check(tShort.Reply != base0.Reply && tShort.Reply.Length < base0.Reply.Length && tShort.Reply.Contains("1,899") && tShort.Reply.StartsWith("Hi Asha"), "Shorter: drops the pleasantries, keeps the prices and the question");
var tFriendly = assistant.GuideFor(rl, tq, prods, [], base0.Reply, "make it warmer and friendlier");
Check(tFriendly.Reply != base0.Reply && tFriendly.Reply.Contains("😊") && tFriendly.Reply.Contains("1,899") && tFriendly.Reply.StartsWith("Hi Asha!"), "Friendlier: warmer greeting and closing, same facts");
var tFormal = assistant.GuideFor(rl, tq, prods, [], base0.Reply, "make it more formal and professional");
Check(tFormal.Reply != base0.Reply && tFormal.Reply.StartsWith("Dear Asha,") && tFormal.Reply.Contains("Kind regards") && !tFormal.Reply.Contains("🌿"), "More formal: Dear, no emoji, signed off");
var tTa = assistant.GuideFor(rl, tq, prods, [], base0.Reply, "translate it into natural Tamil");
Check(tTa.Reply.StartsWith("வணக்கம் Asha") && tTa.Reply.Contains("1,899") && tTa.Intent == "Price question", "Tamil: the standard price reply in Tamil with the real prices");
var tHi = assistant.GuideFor(rl, tq, prods, [], base0.Reply, "translate it into natural Hindi");
Check(tHi.Reply.StartsWith("नमस्ते Asha") && tHi.Reply.Contains("3,499"), "Hindi: the standard price reply in Hindi with the real prices");
if (Environment.GetEnvironmentVariable("FU_SHOW") == "1") foreach (var (n, r) in new[] { ("STANDARD", base0), ("SHORTER", tShort), ("FRIENDLIER", tFriendly), ("MORE FORMAL", tFormal), ("TAMIL", tTa), ("HINDI", tHi) }) Console.WriteLine("=== " + n + " ===" + Environment.NewLine + r.Reply);
var tTaShort = assistant.GuideFor(rl, tq, prods, [], tTa.Reply, "make it shorter");
Check(tTaShort.Reply == tTa.Reply && tTaShort.ReviewNote!.Contains("English"), "A tone change on a Tamil reply says it only works in English");
var tBack = assistant.GuideFor(rl, tq, prods, [], tTa.Reply, "translate it into natural Hindi");
Check(tBack.Reply.StartsWith("नमस्ते"), "Switching from Tamil to Hindi works");
var chain = assistant.GuideFor(rl, tq, prods, [], tShort.Reply, "make it more formal and professional");
Check(chain.Reply.StartsWith("Dear Asha,") && chain.Reply.Length < tFormal.Reply.Length, "Changes can be stacked: shorter and then formal");
var typed = "Hello Asha. Thank you for your interest. We have a special combo this week for loyal customers. It includes free delivery and a gift. Please tell us which pack you want.";
var typedShort = assistant.GuideFor(rl, tq, prods, [], typed, "shorter");
Check(typedShort.Reply.Length < typed.Length, "Text the person typed can still be shortened");
var typedTamil = assistant.GuideFor(rl, tq, prods, [], typed, "translate it into Tamil");
Check(typedTamil.Reply == typed && typedTamil.ReviewNote!.Contains("edited"), "Typed text is never replaced when it cannot be translated, and the person is told why");
var oddAsk = assistant.GuideFor(rl, tq, prods, [], base0.Reply, "write it as a poem");
Check(oddAsk.Reply == base0.Reply && oddAsk.ReviewNote!.Contains("AI assistant"), "A request only the AI can do leaves the text and explains");
var tShortAgain = assistant.GuideFor(rl, tq, prods, [], tShort.Reply, "make it shorter");
Check(tShortAgain.Reply == tShort.Reply || tShortAgain.Reply.Length < tShort.Reply.Length, "Shorter on an already short reply does not break it");
Check(assistant.GuideFor(rl, "I want to buy 2 packs", prods, [], null, null).Reply.Contains("1. Your full name"), "Order reply keeps its numbered list");
var orderTa = assistant.GuideFor(rl, "I want to buy 2 packs", prods, []);
Check(assistant.GuideFor(rl, "I want to buy 2 packs", prods, [], orderTa.Reply, "tamil").Reply.Contains("1. உங்கள் முழு பெயர்"), "Tamil keeps the numbered list for orders");
var healthTa = assistant.GuideFor(rl, "Is it okay with my diabetes tablets?", prods, []);
var healthTamil = assistant.GuideFor(rl, "Is it okay with my diabetes tablets?", prods, [], healthTa.Reply, "tamil");
Check(healthTamil.NeedsReview && healthTamil.Intent == "Health question" && healthTamil.Reply.Contains("மருத்துவ"), "A health reply stays flagged for review in Tamil");

Console.WriteLine($"\n{checks} follow-up checks passed. No external services contacted.");

sealed class FakeWhatsApp(List<string> sent) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        sent.Add(body);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"messages\":[{\"id\":\"wamid.TEST" + sent.Count + "\"}]}", Encoding.UTF8, "application/json") };
    }
}

/// <summary>Pretends to be the Meta media endpoints: a lookup that returns a download address, and the file itself.</summary>
sealed class MediaFake : HttpMessageHandler
{
    public readonly byte[] Bytes = [0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4, 5, 6];
    public readonly HashSet<string> Bad = ["BAD"];
    public readonly List<string> Auths = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        Auths.Add(request.Headers.Authorization?.ToString() ?? "");
        if (uri.Host == "graph.facebook.com")
        {
            var id = uri.Segments.Last();
            if (Bad.Contains(id)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var mime = id switch { "MED-ST" => "image/webp", "MED-IM" => "image/jpeg", "MED-DOC" => "application/pdf", "EVIL" => "image/png", _ => "audio/ogg" };
            var host = id == "EVIL" ? "evil.example.com" : "lookaside.fbsbx.com";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"url\":\"https://{host}/{id}\",\"mime_type\":\"{mime}\",\"file_size\":{Bytes.Length}}}", Encoding.UTF8, "application/json") });
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes) });
    }
}
