using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
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
/// Offline verification that <c>ChangedBy</c> in <c>lead_assignment_history</c> and <c>lead_status_history</c>
/// persists employee IDs instead of employee display names, resolves IDs back to current employee names in
/// <see cref="StaffController.LeadDetail"/> (including renames and deleted-employee fallback), and preserves
/// <c>auto</c>, <c>system</c>, <c>journey</c>, and configured super-admin email actors.
/// </summary>
public static class ChangedByChecks
{
    private sealed class DummyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }

    private static ClaimsPrincipal EmployeePrincipal(long id, string name, string email, string role) =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Email, email),
            new Claim(ClaimTypes.Name, name),
            new Claim(ClaimTypes.Role, role),
            new Claim("emp_id", id.ToString()),
        ], "Test"));

    private static ClaimsPrincipal SuperAdminPrincipal(string email) =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Email, email),
            new Claim(ClaimTypes.Role, "admin"),
        ], "Test"));

    private static async Task PrepareControllerAsync(StaffControllerBase controller, ClaimsPrincipal user, IServiceProvider sp, string path = "/api/admin/leads")
    {
        var httpContext = new DefaultHttpContext { User = user, RequestServices = sp };
        // OnActionExecutionAsync calls StaffAvailability.TouchAsync (ExecuteUpdateAsync) unless path ends with /presence/offline;
        // EF Core's InMemory provider does not support ExecuteUpdateAsync, so skip TouchAsync while still resolving Rights.
        httpContext.Request.Path = "/api/admin/presence/offline";
        var actionContext = new ActionContext(httpContext, new RouteData(), new ControllerActionDescriptor());
        controller.ControllerContext = new ControllerContext(actionContext);
        var execContext = new ActionExecutingContext(actionContext, [], new Dictionary<string, object?>(), controller);
        await controller.OnActionExecutionAsync(execContext, () =>
        {
            httpContext.Request.Path = path;
            return Task.FromResult(new ActionExecutedContext(actionContext, [], controller));
        });
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        var dbName = "changedby-checks-" + Guid.NewGuid().ToString("N");
        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddLogging();
        services.AddDbContext<NuraDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddScoped<StaffAvailability>();
        services.AddScoped<MessageTemplateService>();
        services.AddScoped<LeadService>();
        services.AddScoped<RightsService>();
        await using var sp = services.BuildServiceProvider();

        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NuraDbContext>();
        var availability = scope.ServiceProvider.GetRequiredService<StaffAvailability>();
        var templates = scope.ServiceProvider.GetRequiredService<MessageTemplateService>();
        var leads = scope.ServiceProvider.GetRequiredService<LeadService>();
        var rights = scope.ServiceProvider.GetRequiredService<RightsService>();
        var waOptions = Options.Create(new WhatsAppOptions { AccessToken = "x", PhoneNumberId = "1", DefaultCountryCode = "91" });
        var storeOptions = Options.Create(new StoreOptions { FrontendUrl = "https://shop.test" });
        var whatsapp = new WhatsAppService(db, new HttpClient(new DummyHandler()), leads, waOptions, NullLogger<WhatsAppService>.Instance);
        var shippingStore = new ShippingSettingsStore();
        var shippingGateway = new ShippingGateway(
            new ShiprocketService(new HttpClient(new DummyHandler()), Options.Create(new ShiprocketOptions()), shippingStore, NullLogger<ShiprocketService>.Instance),
            new ShadowfaxService(new HttpClient(new DummyHandler()), Options.Create(new ShadowfaxOptions()), shippingStore, NullLogger<ShadowfaxService>.Instance),
            shippingStore,
            Options.Create(new ShippingOptions()),
            Options.Create(new ShadowfaxOptions()));
        var orderSvc = new OrderService(db, new ProductService(db, storeOptions), shippingGateway, new OrderOperationLock(db), sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<OrderService>.Instance);
        var signer = new MediaSigner(Options.Create(new JwtOptions { Secret = new string('k', 40) }));
        var fuSvc = new FollowUpService(db, leads, whatsapp, Options.Create(new FollowUpOptions()), storeOptions, NullLogger<FollowUpService>.Instance, waOptions, templates);

        var mansoor = new Employee { Name = "mansoor", Email = "mansoor@nuraherbex.com", Role = EmployeeRole.Sales, IsActive = true, WhatsAppActive = true, LastSeenAt = DateTime.UtcNow };
        var adminEmp = new Employee { Name = "Priya Admin", Email = "priya.admin@nuraherbex.com", Role = EmployeeRole.Admin, IsActive = true, WhatsAppActive = false, LastSeenAt = DateTime.UtcNow };
        var karthik = new Employee { Name = "Karthik", Email = "karthik@nuraherbex.com", Role = EmployeeRole.Sales, IsActive = true, WhatsAppActive = true, LastSeenAt = DateTime.UtcNow };
        db.Employees.AddRange(mansoor, adminEmp, karthik);
        await db.SaveChangesAsync();

        var lead1 = new Lead { Name = "Asha", Phone = "919900000001", Status = LeadStatus.New, AssignedToEmployeeId = mansoor.Id };
        var lead2 = new Lead { Name = "Bala", Phone = "919900000002", Status = LeadStatus.New, AssignedToEmployeeId = null };
        var lead3 = new Lead { Name = "Chitra", Phone = "919900000003", Status = LeadStatus.New, AssignedToEmployeeId = mansoor.Id };
        db.Leads.AddRange(lead1, lead2, lead3);
        await db.SaveChangesAsync();

        var staffCtrl = new StaffController(db, leads, whatsapp, orderSvc, signer, rights, NullLogger<StaffController>.Instance);
        var fuCtrl = new FollowUpController(db, fuSvc, whatsapp, rights);

        var mansoorUser = EmployeePrincipal(mansoor.Id, mansoor.Name, mansoor.Email, mansoor.Role.ToCode());
        var adminEmpUser = EmployeePrincipal(adminEmp.Id, adminEmp.Name, adminEmp.Email, adminEmp.Role.ToCode());
        var karthikUser = EmployeePrincipal(karthik.Id, karthik.Name, karthik.Email, karthik.Role.ToCode());
        var superAdminUser = SuperAdminPrincipal("admin@nuraherbex.com");

        // 1) Sales employee changes lead status & transfers lead
        await PrepareControllerAsync(staffCtrl, mansoorUser, scope.ServiceProvider);
        check(staffCtrl.LeadStatus(lead1.Id, new LeadStatusRequest { Status = LeadStatuses.Interested, Note = "Spoke on phone" }).Result is OkObjectResult, "ChangedBy: sales updates own lead status");
        var status1 = await db.LeadStatusHistory.OrderByDescending(h => h.Id).FirstAsync(h => h.LeadId == lead1.Id);
        check(status1.ChangedBy == mansoor.Id.ToString(), "ChangedBy: status history stores employee ID instead of name");

        check(staffCtrl.LeadAssign(lead1.Id, new LeadAssignRequest { EmployeeId = karthik.Id, Reason = "Speaks Tamil" }).Result is OkObjectResult, "ChangedBy: sales transfers lead to colleague");
        var assign1 = await db.LeadAssignmentHistory.OrderByDescending(h => h.Id).FirstAsync(h => h.LeadId == lead1.Id);
        check(assign1.ChangedBy == mansoor.Id.ToString(), "ChangedBy: assignment history stores employee ID on transfer");

        // 2) Staff user switches WhatsApp off via FollowUpController.SetWhatsAppActive (the exact row-17 scenario)
        await PrepareControllerAsync(fuCtrl, mansoorUser, scope.ServiceProvider, "/api/admin/presence/whatsapp");
        var waRes = await fuCtrl.SetWhatsAppActive(new WhatsAppActiveRequest { Active = false }, availability, leads);
        check(waRes.Success && !waRes.Active, "ChangedBy: sales switches own WhatsApp off");
        var waAssign = await db.LeadAssignmentHistory.OrderByDescending(h => h.Id).FirstAsync(h => h.LeadId == lead3.Id);
        check(waAssign.ChangedBy == mansoor.Id.ToString(), "ChangedBy: SetWhatsAppActive off stores employee ID instead of name");

        // 3) Admin employee (with emp_id) assigns, toggles WhatsApp off, and deactivates/removes employee
        await PrepareControllerAsync(staffCtrl, adminEmpUser, scope.ServiceProvider);
        check(staffCtrl.LeadAssign(lead2.Id, new LeadAssignRequest { EmployeeId = karthik.Id, Reason = "Manual queue pickup" }).Result is OkObjectResult, "ChangedBy: admin employee assigns unowned lead");
        var assign2 = await db.LeadAssignmentHistory.OrderByDescending(h => h.Id).FirstAsync(h => h.LeadId == lead2.Id);
        check(assign2.ChangedBy == adminEmp.Id.ToString(), "ChangedBy: admin employee assignment stores admin employee ID");

        check(staffCtrl.SetEmployeeWhatsApp(karthik.Id, new WhatsAppActiveRequest { Active = false }, availability).Result is OkObjectResult, "ChangedBy: admin employee toggles colleague WhatsApp off");
        var karthikWaAssign = await db.LeadAssignmentHistory.OrderByDescending(h => h.Id).FirstAsync(h => h.LeadId == lead2.Id);
        check(karthikWaAssign.ChangedBy == adminEmp.Id.ToString() && (karthikWaAssign.Reason?.Contains("Priya Admin") ?? false),
            "ChangedBy: admin WhatsApp toggle stores employee ID in ChangedBy and keeps readable name in Reason");

        check(staffCtrl.RemoveEmployee(karthik.Id, "unassigned").Result is OkObjectResult, "ChangedBy: admin employee removes colleague");
        var removeAssign = await db.LeadAssignmentHistory.OrderByDescending(h => h.Id).FirstAsync(h => h.LeadId == lead1.Id);
        check(removeAssign.ChangedBy == adminEmp.Id.ToString(), "ChangedBy: employee removal stores admin employee ID in ChangedBy");

        // 4) FollowUpController Create & Complete record employee ID in LeadStatusHistory & LeadAssignmentHistory
        await PrepareControllerAsync(fuCtrl, adminEmpUser, scope.ServiceProvider, "/api/admin/followups");
        check(fuCtrl.Create(new FollowUpCreateRequest { LeadId = lead1.Id, Type = FollowUpTypes.Call, DueAt = DateTime.UtcNow.AddDays(2), AssignedToEmployeeId = mansoor.Id }).Result is ObjectResult { StatusCode: 201 },
            "ChangedBy: admin employee creates follow-up on unassigned lead");
        var fuAssign = await db.LeadAssignmentHistory.OrderByDescending(h => h.Id).FirstAsync(h => h.LeadId == lead1.Id);
        check(fuAssign.ChangedBy == adminEmp.Id.ToString(), "ChangedBy: FollowUp Create assignment stores actor employee ID");

        var createdFu = await db.FollowUps.OrderByDescending(f => f.Id).FirstAsync(f => f.LeadId == lead1.Id);
        check(createdFu.CreatedBy == adminEmp.Id.ToString(),
            "ChangedBy: FollowUp Create stores employee ID in FollowUp.CreatedBy");
        await PrepareControllerAsync(fuCtrl, mansoorUser, scope.ServiceProvider, $"/api/admin/followups/{createdFu.Id}/complete");
        check(fuCtrl.Complete(createdFu.Id, new FollowUpCompleteRequest { Outcome = FollowUpOutcomes.NotInterested, Note = "Not needed" }).Result is OkObjectResult,
            "ChangedBy: employee completes follow-up with outcome not-interested");
        var fuStatus = await db.LeadStatusHistory.OrderByDescending(h => h.Id).FirstAsync(h => h.LeadId == lead1.Id);
        var completedFu = await db.FollowUps.FirstAsync(f => f.Id == createdFu.Id);
        check(fuStatus.ChangedBy == mansoor.Id.ToString() && completedFu.CompletedBy == mansoor.Id.ToString(),
            "ChangedBy: FollowUp Complete stores employee ID in LeadStatusHistory.ChangedBy and FollowUp.CompletedBy");

        // 5) Configured super-admin (no emp_id) still records configured email
        await PrepareControllerAsync(staffCtrl, superAdminUser, scope.ServiceProvider);
        check(staffCtrl.LeadAssign(lead2.Id, new LeadAssignRequest { EmployeeId = mansoor.Id, Reason = "Super admin override" }).Result is OkObjectResult,
            "ChangedBy: configured super-admin assigns unowned lead");
        var superAssign = await db.LeadAssignmentHistory.OrderByDescending(h => h.Id).FirstAsync(h => h.LeadId == lead2.Id);
        check(superAssign.ChangedBy == "admin@nuraherbex.com", "ChangedBy: super-admin without emp_id records configured email");

        // 6) Sign-out ("Owner signed out") still writes "auto" (lead2 is Status == New and currently owned by mansoor).
        // GoOffline calls CheckOutAsync (ExecuteUpdateAsync, unsupported by InMemory) then SwitchOffWhatsAppAsync(me, "Owner signed out", "auto").
        mansoor.LastSeenAt = null;
        mansoor.WhatsAppActive = true;
        await db.SaveChangesAsync();
        await leads.SwitchOffWhatsAppAsync(mansoor.Id, "Owner signed out", "auto");
        var offlineAssign = await db.LeadAssignmentHistory.OrderByDescending(h => h.Id).FirstAsync(h => h.LeadId == lead2.Id);
        check(offlineAssign.ChangedBy == "auto", "ChangedBy: sign-out preserves auto marker");

        // 7) LeadDetail resolves IDs dynamically, handles employee rename, missing ID -> "Former employee", and preserves system/super-admin/legacy strings
        db.LeadStatusHistory.Add(new LeadStatusHistory { LeadId = lead1.Id, FromStatus = LeadStatus.New, ToStatus = LeadStatus.FollowUp, ChangedBy = "999999", Note = "Deleted staff", CreatedAt = DateTime.UtcNow.AddMinutes(-5) });
        db.LeadAssignmentHistory.Add(new LeadAssignmentHistory { LeadId = lead1.Id, FromEmployeeId = null, ToEmployeeId = mansoor.Id, ChangedBy = "LegacyAgent", Reason = "Old row", CreatedAt = DateTime.UtcNow.AddMinutes(-6) });
        await db.SaveChangesAsync();

        mansoor.Name = "Mansoor Ali";
        adminEmp.Name = "Priya Updated";
        await db.SaveChangesAsync();

        await PrepareControllerAsync(staffCtrl, superAdminUser, scope.ServiceProvider, $"/api/admin/leads/{lead1.Id}");
        var detail1 = (staffCtrl.LeadDetail(lead1.Id).Result as OkObjectResult)?.Value as LeadDetailResponse;
        check(detail1 is not null, "ChangedBy: LeadDetail returns response for lead1");
        check(detail1!.History.Any(h => h.Note == "Spoke on phone" && h.ChangedBy == "Mansoor Ali"),
            "ChangedBy: LeadDetail History resolves stored employee ID to current (renamed) employee name");
        check(detail1.History.Any(h => h.Note == "Deleted staff" && h.ChangedBy == "Former employee"),
            "ChangedBy: LeadDetail History maps unknown numeric employee ID to Former employee");
        check(detail1.Assignments.Any(a => a.Reason == "Speaks Tamil" && a.ChangedBy == "Mansoor Ali"),
            "ChangedBy: LeadDetail Assignments resolves stored employee ID to current employee name");
        check(detail1.Assignments.Any(a => a.Reason == "Old row" && a.ChangedBy == "LegacyAgent"),
            "ChangedBy: LeadDetail Assignments preserves unmigrated legacy string");
        check(detail1.Audit.Any(a => a.Detail == "Spoke on phone" && a.Actor == "Mansoor Ali" && !a.Automatic)
            && detail1.Audit.Any(a => a.Detail != null && a.Detail.StartsWith("Employee removed") && a.Actor == "Priya Updated" && !a.Automatic),
            "ChangedBy: LeadDetail Audit on lead1 resolves employee IDs dynamically");
        check(detail1.FollowUps.Any(f => f.Id == createdFu.Id && f.CompletedBy == "Mansoor Ali" && f.Status == FollowUpStatuses.Done && f.Source == FollowUpSources.Manual),
            "ChangedBy: LeadDetail FollowUps resolves CompletedBy employee ID to current employee name and preserves status/source string codes");

        await PrepareControllerAsync(staffCtrl, superAdminUser, scope.ServiceProvider, $"/api/admin/leads/{lead2.Id}");
        var detail2 = (staffCtrl.LeadDetail(lead2.Id).Result as OkObjectResult)?.Value as LeadDetailResponse;
        check(detail2 is not null, "ChangedBy: LeadDetail returns response for lead2");
        check(detail2!.Assignments.Any(a => a.Reason == "Super admin override" && a.ChangedBy == "admin@nuraherbex.com"),
            "ChangedBy: LeadDetail Assignments preserves configured super-admin email");
        check(detail2.Assignments.Any(a => a.ChangedBy == "auto"),
            "ChangedBy: LeadDetail Assignments preserves auto marker");
        check(detail2.Audit.Any(a => a.Detail != null && a.Detail.StartsWith("Owner signed out") && a.Actor == "auto" && a.Automatic)
            && detail2.Audit.Any(a => a.Detail != null && a.Detail.StartsWith("WhatsApp switched off by") && a.Actor == "Priya Updated" && !a.Automatic)
            && detail2.Audit.Any(a => a.Detail == "Super admin override" && a.Actor == "admin@nuraherbex.com" && !a.Automatic),
            "ChangedBy: LeadDetail Audit resolves admin rename, super-admin email, and automatic markers accurately");
    }
}
