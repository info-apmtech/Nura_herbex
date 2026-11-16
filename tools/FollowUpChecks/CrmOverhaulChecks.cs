using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nuraherbex.Api.Controllers;
using Nuraherbex.Api.Data;
using Nuraherbex.Api.Options;
using Nuraherbex.Api.Services;
using Nuraherbex.Shared.Models;

/// <summary>
/// Offline verification for all 5 modules of the CRM overhaul:
///   1) Notes lock on New leads until Clinical Assessment is saved + Edit/Update details
///   2) Assessment validation (Gender, MaritalStatus, WorkType, Smoking/Drinking, HealthConcerns + Others text)
///   3) Status workflow (New cannot jump to Follow-up; Interested -> FollowUp on scheduling; Hot leads High priority; 7-attempt No Answer -> NotInterested)
///   4) Offline shipping mode (skips courier booking/alerts, AWB-less MarkDelivered, manual COD update, PDF invoice download)
///   5) 4-category Bulk Messaging preview & RBAC permissions for bulk_messages + drive_files
/// </summary>
public static class CrmOverhaulChecks
{
    private sealed class FixedMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class OkWaHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"messages\":[{\"id\":\"wamid.OVERHAUL\"}]}", Encoding.UTF8, "application/json"),
            });
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        var now = new DateTime(2026, 11, 12, 6, 0, 0, DateTimeKind.Utc);
        FollowUpService.Clock = () => now;

        var dbName = "crm-overhaul-" + Guid.NewGuid().ToString("N");
        using var waHandler = new OkWaHandler();
        var waOptions = Options.Create(new WhatsAppOptions { AccessToken = "tok", PhoneNumberId = "1", DefaultCountryCode = "91", AutoReplyEnabled = false });
        var storeOptions = Options.Create(new StoreOptions
        {
            FrontendUrl = "https://shop.test",
            SellerLegalName = "Nura Herbex Wellness",
            SellerGstin = "33AAACN0000A1Z5",
            SellerState = "Tamil Nadu",
            GstRatePercent = 18m,
            FreeShippingThreshold = 999m,
            FlatShippingFee = 99m,
        });
        var jwtOptions = Options.Create(new JwtOptions { Secret = new string('s', 48), Issuer = "nura", Audience = "nura" });

        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddLogging();
        services.AddDbContext<NuraDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton(waOptions);
        services.AddSingleton(storeOptions);
        services.AddSingleton<IOptionsMonitor<StoreOptions>>(new FixedMonitor<StoreOptions>(storeOptions.Value));
        services.AddSingleton(jwtOptions);
        services.AddSingleton(Options.Create(new FollowUpOptions()));
        services.AddSingleton(Options.Create(new EmailOptions()));
        services.AddSingleton(Options.Create(new ShippingOptions()));
        services.AddSingleton(Options.Create(new ShadowfaxOptions()));
        services.AddSingleton<ShippingSettingsStore>();
        services.AddScoped<StaffAvailability>();
        services.AddScoped<MessageTemplateService>();
        services.AddScoped<LeadService>();
        services.AddScoped<ProductService>();
        services.AddScoped<OrderOperationLock>();
        services.AddScoped<RightsService>();
        services.AddScoped<InvoiceService>();
        services.AddScoped(_ => new EmailService(new HttpClient(waHandler, false), Options.Create(new EmailOptions()), storeOptions, NullLogger<EmailService>.Instance));
        services.AddScoped(p => new WhatsAppService(p.GetRequiredService<NuraDbContext>(), new HttpClient(waHandler, false), p.GetRequiredService<LeadService>(), waOptions, NullLogger<WhatsAppService>.Instance));
        services.AddScoped(_ => new ShiprocketService(new HttpClient(waHandler, false), Options.Create(new ShiprocketOptions()), new ShippingSettingsStore(), NullLogger<ShiprocketService>.Instance));
        services.AddScoped(_ => new ShadowfaxService(new HttpClient(waHandler, false), Options.Create(new ShadowfaxOptions()), new ShippingSettingsStore(), NullLogger<ShadowfaxService>.Instance));
        services.AddScoped<ShippingGateway>();
        services.AddScoped<OrderService>();
        services.AddScoped<FollowUpService>();
        services.AddScoped<BulkMessageService>();

        await using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NuraDbContext>();
        var leads = scope.ServiceProvider.GetRequiredService<LeadService>();
        var followUps = scope.ServiceProvider.GetRequiredService<FollowUpService>();
        var orders = scope.ServiceProvider.GetRequiredService<OrderService>();
        var invoices = scope.ServiceProvider.GetRequiredService<InvoiceService>();
        var bulk = scope.ServiceProvider.GetRequiredService<BulkMessageService>();
        var rights = scope.ServiceProvider.GetRequiredService<RightsService>();

        // Seed designations & staff for RBAC + assignment
        var mgrDesig = new Designation { Name = "Sales manager", Level = 2, RightsJson = PageCatalog.ToJson(PageCatalog.Presets.First(p => p.Name == "Sales manager").Rights) };
        var opsDesig = new Designation { Name = "Operations", Level = 2, RightsJson = PageCatalog.ToJson(PageCatalog.Presets.First(p => p.Name == "Operations").Rights) };
        var execDesig = new Designation { Name = "Sales executive", Level = 2, RightsJson = PageCatalog.ToJson(PageCatalog.Presets.First(p => p.Name == "Sales executive").Rights) };
        db.Designations.AddRange(mgrDesig, opsDesig, execDesig);
        await db.SaveChangesAsync();

        var mgr = new Employee { Name = "Karthik Mgr", Email = "mgr@nura.test", Role = EmployeeRole.Sales, IsActive = true, DesignationId = mgrDesig.Id };
        var ops = new Employee { Name = "Priya Ops", Email = "ops@nura.test", Role = EmployeeRole.Sales, IsActive = true, DesignationId = opsDesig.Id };
        var exec = new Employee { Name = "Ravi Exec", Email = "exec@nura.test", Role = EmployeeRole.Sales, IsActive = true, DesignationId = execDesig.Id };
        db.Employees.AddRange(mgr, ops, exec);
        db.Products.Add(new Product { Id = "stamix-overhaul", Sku = "STX-O", Name = "Stamix Pack", Price = 1500m, StockQuantity = 50, WeightKg = 0.5m, LengthCm = 10, BreadthCm = 10, HeightCm = 10, Status = ProductStatus.Active });
        db.SiteSettings.Add(new SiteSetting { Key = "followups.startedAt", Value = now.AddDays(-5).ToString("O") });
        await db.SaveChangesAsync();

        // ---- 1 & 2. Notes lock on New lead until Assessment Form saved + Others validation + Edit/Update details ----
        var mohan = (await leads.UpsertFromWhatsAppAsync("919944110001", "919944110001", "Need details", null, null)).Lead;
        await leads.AssignAsync(mohan, exec.Id, "test", "admin");
        await db.SaveChangesAsync();

        var (lockedNoteOk, lockedNoteMsg) = await leads.SaveNotesAsync(mohan.Id, "Interested in 3 months");
        var (lockedDetailOk, _) = await leads.UpdateDetailsAsync(mohan.Id, new LeadDetailsUpdateRequest { Name = "Mohan K", Notes = "Note before assessment" });
        check(!lockedNoteOk && lockedNoteMsg.Contains("Assessment Form") && !lockedDetailOk,
            "Overhaul M1: Notes & Details notes write are blocked on a New lead before Assessment Form is saved");

        var (badOtherOk, badOtherMsg) = await leads.SaveAssessmentAsync(mohan.Id, new LeadAssessmentRequest
        {
            Name = "Mohan K", Age = 38, Gender = "Male", MaritalStatus = "Married", WorkType = "Driving",
            IsSmoking = true, IsDrinking = false, HealthConcerns = ["Diabetic", "Others"], OtherHealthConcern = "   ",
        }, exec.Id.ToString());
        check(!badOtherOk && badOtherMsg.Contains("Others"),
            "Overhaul M2: Selecting 'Others' in Health Concerns requires manual text explanation");

        var (assessOk, _) = await leads.SaveAssessmentAsync(mohan.Id, new LeadAssessmentRequest
        {
            Name = "Mohan K", Age = 38, Gender = "Male", MaritalStatus = "Married", WorkType = "Driving",
            ProblemDuration = "6 months", PreviousSupplements = "Yes", AssessmentNotes = "Called, wants evening callback",
            IsSmoking = true, IsDrinking = false, PrimaryConcerns = ["Blood flow", "Others"], HealthConditions = ["Diabetic"], OtherHealthConcern = "Night shift fatigue",
        }, exec.Id.ToString());
        var mohanDb = await db.Leads.AsNoTracking().SingleAsync(l => l.Id == mohan.Id);
        check(assessOk && mohanDb.HasAssessment && mohanDb.Name == "Mohan K" && mohanDb.Age == 38 && mohanDb.WorkType == "Driving" && mohanDb.ProblemDuration == "6 months" && mohanDb.PreviousSupplements == "Yes" && mohanDb.AssessmentNotes == "Called, wants evening callback" && mohanDb.IsSmoking && !mohanDb.IsDrinking && mohanDb.PrimaryConcerns.Contains("Blood flow") && mohanDb.HealthConditions.Contains("Diabetic"),
            "Overhaul M2: Clinical Assessment persists Name, Age, WorkType, habits, duration, supplements, notes and split HealthConcerns + unlocks Notes");

        var (unlockedDetailOk, _) = await leads.UpdateDetailsAsync(mohan.Id, new LeadDetailsUpdateRequest { Name = "Mohan Kumar", Email = "mohan@nura.test", Notes = "Called, wants evening callback" });
        mohanDb = await db.Leads.AsNoTracking().SingleAsync(l => l.Id == mohan.Id);
        check(unlockedDetailOk && mohanDb.Name == "Mohan Kumar" && mohanDb.Email == "mohan@nura.test" && mohanDb.Notes == "Called, wants evening callback",
            "Overhaul M1: UpdateDetailsAsync updates Name, Email and Notes once Assessment is saved");

        // ---- 3. New cannot move to Follow-up directly; Interested + Schedule moves to FollowUp; High priority; 7x No Answer -> NotInterested ----
        var directFuOk = await leads.ChangeStatusAsync(mohan.Id, LeadStatuses.FollowUp, "skip", exec.Id.ToString());
        var (_, schedNewErr) = await followUps.CreateManualAsync(new FollowUpCreateRequest { LeadId = mohan.Id, Type = FollowUpTypes.Call, DueAt = now.AddDays(1) }, exec.Name, false, exec.Id);
        check(!directFuOk && schedNewErr is not null && schedNewErr.Contains("Interested"),
            "Overhaul M3: New lead cannot move directly to Follow-up via ChangeStatusAsync or CreateManualAsync");

        var toInterestedOk = await leads.ChangeStatusAsync(mohan.Id, LeadStatuses.Interested, "Interested in pack", exec.Id.ToString());
        var (prioOk, _) = await leads.ChangePriorityAsync(mohan.Id, LeadPriority.High, "Urgent buyer", exec.Id.ToString());
        check(toInterestedOk && prioOk && (await db.Leads.AsNoTracking().SingleAsync(l => l.Id == mohan.Id)).Priority == LeadPriority.High,
            "Overhaul M3: Moving to Interested & marking Hot Lead priority High succeeds");

        var (schedOk, schedErr) = await followUps.CreateManualAsync(new FollowUpCreateRequest { LeadId = mohan.Id, Type = FollowUpTypes.Call, DueAt = now.AddDays(1) }, exec.Name, false, exec.Id);
        mohanDb = await db.Leads.AsNoTracking().SingleAsync(l => l.Id == mohan.Id);
        check(schedOk is not null && schedErr is null && mohanDb.Status == LeadStatus.FollowUp,
            "Overhaul M3: Scheduling follow-up on Interested lead automatically transitions lead status to FollowUp");

        // 7-attempt No Answer escalation -> NotInterested
        var naLead = (await leads.UpsertFromWhatsAppAsync("919944110002", "Ashok NoAns", "hi", null, null)).Lead;
        await leads.AssignAsync(naLead, exec.Id, "test", "admin");
        await db.SaveChangesAsync();
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            var (naOk, _) = await followUps.LogNoAnswerAsync(naLead, null, $"Try {attempt}", exec.Name, exec.Id);
            naLead = await db.Leads.SingleAsync(l => l.Id == naLead.Id);
            check(naOk && naLead.Status == LeadStatus.NoAnswer && naLead.NoAnswerAttempts == attempt,
                $"Overhaul M3: No Answer attempt {attempt}/7 keeps lead in NoAnswer status");
        }
        var (ok7, msg7) = await followUps.LogNoAnswerAsync(naLead, null, "Try 7", exec.Name, exec.Id);
        naLead = await db.Leads.SingleAsync(l => l.Id == naLead.Id);
        var anyOpenManualAfter7 = await db.FollowUps.AnyAsync(f => f.LeadId == naLead.Id && f.Source == FollowUpSource.Manual && f.Status == FollowUpStatus.Pending);
        check(ok7 && naLead.Status == LeadStatus.NotInterested && naLead.NoAnswerAttempts == 7 && !anyOpenManualAfter7 && msg7.Contains("Not Interested"),
            "Overhaul M3: 7th unanswered attempt auto-moves lead to NotInterested and closes pending retries");

        // Second active No Answer lead for Bulk preview testing later
        var naActive = (await leads.UpsertFromWhatsAppAsync("919944110003", "Kiran Retry", "hello", null, null)).Lead;
        await followUps.LogNoAnswerAsync(naActive, null, "Try 1", exec.Name, null);

        // ---- 4. Offline shipping order: skip courier booking & alerts, AWB-less MarkDelivered, manual COD update, PDF download ----
        var offOrder = await orders.CreateAsync(new CreateOrderRequest
        {
            Customer = new CheckoutCustomerDto { FullName = "Chennai Local Buyer", Phone = "9840199999", Email = "" },
            PaymentMethod = OrderPaymentMethods.Cod,
            ShippingMode = ShippingMode.Offline,
            OfflineDispatchNote = "Chennai Anna Nagar rider",
            Shipping = new AddressDto { AddressLine1 = "10 Second Ave", City = "Chennai", State = "Tamil Nadu", Pincode = "600040" },
            Items = [new CartLineDto { Id = "stamix-overhaul", Quantity = 1 }],
        }, customerId: null, holdUntilPaid: false, emailOptional: true);
        check(offOrder.ShippingMode == ShippingMode.Offline && offOrder.Courier == "Offline" && offOrder.PaymentStatus == OrderPaymentStatus.CodPending,
            "Overhaul M4: CreateCodAsync with Offline mode saves ShippingMode=Offline and Courier='Offline'");

        var retryBlocked = false;
        try { await orders.RetryShipmentAsync(offOrder.Id); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Offline")) { retryBlocked = true; }
        check(retryBlocked, "Overhaul M4: RetryShipmentAsync refuses courier booking for Offline orders");

        offOrder.CreatedAt = now.AddHours(-5);
        await db.SaveChangesAsync();
        await followUps.RunCycleAsync();
        check(!await db.FollowUps.AnyAsync(f => f.OrderId == offOrder.Id && f.StepKey == "order-unbooked"),
            "Overhaul M4: FollowUpService.SyncOrdersAsync does NOT raise order-unbooked alerts for Offline orders");

        var paidOffline = await orders.UpdatePaymentStatusAsync(offOrder.Id, new UpdateOrderPaymentRequest
        {
            PaymentStatus = OrderPaymentStatuses.CodCollected,
            PaymentRef = "CASH-77",
            Note = "Collected at doorstep",
        }, "Admin Owner");
        check(paidOffline.PaymentStatus == OrderPaymentStatus.CodCollected && paidOffline.PaymentRef == "CASH-77",
            "Overhaul M4: UpdatePaymentStatusAsync updates COD collection status manually with audit ref");

        var deliveredOffline = await orders.MarkDeliveredAsync(offOrder.Id);
        check(deliveredOffline.FulfillmentStatus == "DELIVERED" && string.IsNullOrEmpty(deliveredOffline.ShiprocketAwb),
            "Overhaul M4: Offline order can be marked Delivered without an AWB");

        var pdfBytes = invoices.RenderPdf(deliveredOffline);
        var htmlInv = invoices.RenderHtml(deliveredOffline);
        check(pdfBytes.Length > 200 && Encoding.ASCII.GetString(pdfBytes, 0, 5) == "%PDF-" && htmlInv.Contains("Offline") && htmlInv.Contains("COD COLLECTED"),
            "Overhaul M4: InvoiceService renders valid PDF (%PDF-) and HTML showing Offline dispatch and COD COLLECTED");

        // ---- 5. 4-Category Bulk Messaging Preview + RBAC matrix for bulk_messages & drive_files ----
        var hotLead = (await leads.UpsertFromWhatsAppAsync("919944110004", "Hot Buyer", "price?", null, null)).Lead;
        hotLead.Status = LeadStatus.Interested;
        var hotConvo = await db.Conversations.FirstOrDefaultAsync(c => c.LeadId == hotLead.Id);
        if (hotConvo is null) db.Conversations.Add(new Conversation { LeadId = hotLead.Id, LastInboundAt = now, ServiceWindowExpiresAt = now.AddHours(10) });
        else { hotConvo.LastInboundAt = now; hotConvo.ServiceWindowExpiresAt = now.AddHours(10); }
        var optedOutHot = (await leads.UpsertFromWhatsAppAsync("919944110005", "OptOut Hot", "stop", null, null)).Lead;
        optedOutHot.Status = LeadStatus.Interested;
        optedOutHot.ConsentStatus = "opted_out";
        var custLead = (await leads.UpsertFromWhatsAppAsync("919840199999", "Chennai Local Buyer", "ordered", null, null)).Lead;
        custLead.CustomerSince = now;
        custLead.Status = LeadStatus.Converted;
        await db.SaveChangesAsync();

        var prevHot = await bulk.PreviewAsync(BulkCategories.HotLeads);
        var prevNa = await bulk.PreviewAsync(BulkCategories.NoAnswer);
        var prevCust = await bulk.PreviewAsync(BulkCategories.Customers);
        var prevAll = await bulk.PreviewAsync(BulkCategories.AllLeads);
        check(prevHot.TotalMatched >= 2 && prevHot.OptedOutExcluded >= 1 && prevHot.EligibleCount == prevHot.TotalMatched - prevHot.OptedOutExcluded && prevHot.InsideWindowCount >= 1,
            "Overhaul M5: Bulk preview for Hot Leads filters Interested leads, excludes opted-out, and splits 24h window");
        check(prevNa.EligibleCount >= 1 && prevCust.EligibleCount >= 1 && prevAll.EligibleCount >= prevHot.EligibleCount,
            "Overhaul M5: Bulk previews return matches across Customers, No Answer, Hot Leads, and All Leads");

        var bulkRule = PageRules.Match("/api/admin/bulk-messages/send", "POST");
        var driveFilesRule = PageRules.Match("/api/admin/drive/files", "GET");
        var driveSettingsRule = PageRules.Match("/api/admin/drive/settings", "PUT");
        var mgrRights = await rights.ResolveEmployeeAsync(mgr.Id);
        var opsRights = await rights.ResolveEmployeeAsync(ops.Id);
        var execRights = await rights.ResolveEmployeeAsync(exec.Id);
        check(bulkRule.Pages.Contains("bulk_messages") && driveFilesRule.Pages.Contains("drive_files") && driveSettingsRule.MinLevel == PageCatalog.Full
              && mgrRights.Level("bulk_messages") >= PageCatalog.Full && mgrRights.Level("drive_files") >= PageCatalog.Full
              && opsRights.Level("bulk_messages") >= PageCatalog.Limited && opsRights.Level("drive_files") >= PageCatalog.Limited
              && execRights.Level("bulk_messages") == 0 && execRights.Level("drive_files") == 0,
            "Overhaul M5: Sales manager & Operations have bulk_messages + drive_files permissions while Sales executive is denied");

        // ---- 6. Pending Transfers Queue (Transfer / Accept / Reject / 24h Auto-Return) ----
        var waService = scope.ServiceProvider.GetRequiredService<WhatsAppService>();
        var signer = new MediaSigner(Options.Create(new JwtOptions { Secret = "crm-overhaul-jwt-secret-key-32chars-minimum!" }));
        var staffCtrl = new StaffController(db, leads, waService, orders, signer, rights, NullLogger<StaffController>.Instance);

        var execUser = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, exec.Id.ToString()), new Claim("emp_id", exec.Id.ToString()), new Claim(ClaimTypes.Role, "sales"), new Claim(ClaimTypes.Name, exec.Name)], "Test"));
        var mgrUser = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, mgr.Id.ToString()), new Claim("emp_id", mgr.Id.ToString()), new Claim(ClaimTypes.Role, "sales"), new Claim(ClaimTypes.Name, mgr.Name)], "Test"));

        async Task PrepareCtrl(StaffController ctrl, ClaimsPrincipal u, string path)
        {
            var httpContext = new DefaultHttpContext { User = u, RequestServices = scope.ServiceProvider, Request = { Path = "/api/admin/presence/offline" } };
            var actCtx = new ActionContext(httpContext, new RouteData(), new ControllerActionDescriptor());
            ctrl.ControllerContext = new ControllerContext(actCtx);
            var execCtx = new ActionExecutingContext(actCtx, [], new Dictionary<string, object?>(), ctrl);
            await ctrl.OnActionExecutionAsync(execCtx, () =>
            {
                httpContext.Request.Path = path;
                return Task.FromResult(new ActionExecutedContext(actCtx, [], ctrl));
            });
        }

        // 6.1 Transfer Lead
        var leadT = (await leads.UpsertFromWhatsAppAsync("919944110099", "Transfer Target", "hi", null, null)).Lead;
        await leads.AssignAsync(leadT, exec.Id, "Initial owner", "auto");
        await db.SaveChangesAsync();

        await PrepareCtrl(staffCtrl, execUser, $"/api/admin/leads/{leadT.Id}/transfer");
        var transferRes = await staffCtrl.TransferLead(leadT.Id, new LeadTransferRequest { EmployeeId = mgr.Id, Reason = "Speaks English" });
        check(transferRes is OkObjectResult, "Pending Transfer: Transfer request succeeds");

        var leadTFresh = await db.Leads.FirstAsync(l => l.Id == leadT.Id);
        check(leadTFresh.PendingTransferToEmployeeId == mgr.Id && leadTFresh.PendingTransferFromEmployeeId == exec.Id,
            "Pending Transfer: Lead records pending transfer receiver and sender");

        // Verify receiver's list: NOT in receiver's main list, IS in receiver's transfers list
        await PrepareCtrl(staffCtrl, mgrUser, "/api/admin/leads?view=leads");
        var mgrMainLeads = await staffCtrl.Leads(null, null, null, null, 100, LeadViews.Leads);
        check(!mgrMainLeads.Leads.Any(l => l.Id == leadT.Id),
            "Pending Transfer: Transferred lead does NOT appear in receiver's main leads list before acceptance");

        await PrepareCtrl(staffCtrl, mgrUser, "/api/admin/leads?view=transfers");
        var mgrTransfers = await staffCtrl.Leads(null, null, null, null, 100, LeadViews.Transfers);
        check(mgrTransfers.Leads.Any(l => l.Id == leadT.Id) && mgrTransfers.PendingTransfersCount >= 1,
            "Pending Transfer: Transferred lead appears in receiver's Pending Transfers queue with count");

        // 6.2 Accept Transfer
        await PrepareCtrl(staffCtrl, mgrUser, $"/api/admin/leads/{leadT.Id}/transfer/accept");
        var acceptRes = await staffCtrl.AcceptTransfer(leadT.Id);
        check(acceptRes is OkObjectResult, "Pending Transfer: Accept transfer succeeds");

        leadTFresh = await db.Leads.FirstAsync(l => l.Id == leadT.Id);
        check(leadTFresh.AssignedToEmployeeId == mgr.Id && leadTFresh.PendingTransferToEmployeeId == null,
            "Pending Transfer: Accepted lead is assigned to receiver and cleared from pending");

        await PrepareCtrl(staffCtrl, mgrUser, "/api/admin/leads?view=leads");
        mgrMainLeads = await staffCtrl.Leads(null, null, null, null, 100, LeadViews.Leads);
        check(mgrMainLeads.Leads.Any(l => l.Id == leadT.Id),
            "Pending Transfer: Accepted lead is now added to receiver's active leads list");

        // 6.3 Reject Transfer
        var leadR = (await leads.UpsertFromWhatsAppAsync("919944110098", "Reject Target", "hi", null, null)).Lead;
        await leads.AssignAsync(leadR, exec.Id, "Initial owner", "auto");
        await db.SaveChangesAsync();

        await PrepareCtrl(staffCtrl, execUser, $"/api/admin/leads/{leadR.Id}/transfer");
        await staffCtrl.TransferLead(leadR.Id, new LeadTransferRequest { EmployeeId = mgr.Id, Reason = "Over capacity" });

        await PrepareCtrl(staffCtrl, mgrUser, $"/api/admin/leads/{leadR.Id}/transfer/reject");
        var rejectRes = await staffCtrl.RejectTransfer(leadR.Id, new LeadTransferRejectRequest { Reason = "Busy today" });
        check(rejectRes is OkObjectResult, "Pending Transfer: Reject transfer succeeds");

        var leadRFresh = await db.Leads.FirstAsync(l => l.Id == leadR.Id);
        check(leadRFresh.AssignedToEmployeeId == exec.Id && leadRFresh.PendingTransferToEmployeeId == null,
            "Pending Transfer: Rejected lead is returned back to original sender");

        // 6.4 24-Hour Auto-Expiration
        var leadExp = (await leads.UpsertFromWhatsAppAsync("919944110097", "Expire Target", "hi", null, null)).Lead;
        await leads.AssignAsync(leadExp, exec.Id, "Initial owner", "auto");
        leadExp.PendingTransferToEmployeeId = mgr.Id;
        leadExp.PendingTransferFromEmployeeId = exec.Id;
        leadExp.PendingTransferRequestedAt = DateTime.UtcNow.AddHours(-25); // expired > 24h
        await db.SaveChangesAsync();

        var expiredCount = await leads.ExpirePendingTransfersAsync();
        check(expiredCount >= 1, "Pending Transfer: ExpirePendingTransfersAsync detects expired transfer");

        var leadExpFresh = await db.Leads.FirstAsync(l => l.Id == leadExp.Id);
        check(leadExpFresh.AssignedToEmployeeId == exec.Id && leadExpFresh.PendingTransferToEmployeeId == null,
            "Pending Transfer: Auto-expired transfer (>24h) returns lead automatically to original sender");
    }
}

