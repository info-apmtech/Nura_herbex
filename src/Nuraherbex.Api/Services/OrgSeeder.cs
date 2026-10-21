using Microsoft.EntityFrameworkCore;
using Nuraherbex.Api.Data;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Services;

/// <summary>
/// First-run starter set: four departments and five designations with sensible page rights, and existing staff are placed in them
/// (so nobody loses the access they had). Runs only while there are no designations; afterwards the company edits everything freely.
/// </summary>
public static class OrgSeeder
{
    public static async Task RunAsync(NuraDbContext db, ILogger log)
    {
        await FirstRunAsync(db, log);
        await AddMarketingAndDesignAsync(db, log);
    }

    private static async Task FirstRunAsync(NuraDbContext db, ILogger log)
    {
        try
        {
            if (await db.Designations.AnyAsync() || await db.Departments.AnyAsync()) return;

            var sales = new Department { Name = "Sales", Description = "Turns WhatsApp and website enquiries into customers." };
            var support = new Department { Name = "Customer Support", Description = "Answers customers and looks after orders and reviews." };
            var ops = new Department { Name = "Operations", Description = "Packing, shipping, stock and payments." };
            var mgmt = new Department { Name = "Management", Description = "Owners and administrators." };
            db.Departments.AddRange(sales, support, ops, mgmt);
            await db.SaveChangesAsync();

            Designation D(string name, string text, Department dep, int level, string preset)
            {
                var p = PageCatalog.Presets.First(x => x.Name == preset);
                return new Designation { Name = name, Description = text, DepartmentId = dep.Id, Level = level, RightsJson = PageCatalog.ToJson(p.Rights) };
            }
            var exec = D("Sales Executive", "Works own leads, follow-ups and tasks.", sales, 3, "Sales executive");
            var manager = D("Sales Manager", "Leads the sales team and sees everyone's work.", sales, 6, "Sales manager");
            var supportExec = D("Support Executive", "Helps customers and checks orders.", support, 3, "Customer support");
            var opsExec = D("Operations Executive", "Ships orders and manages stock.", ops, 3, "Operations");
            var admin = D("Administrator", "Full access to every page.", mgmt, 10, "Everything");
            db.Designations.AddRange(exec, manager, supportExec, opsExec, admin);
            await db.SaveChangesAsync();

            foreach (var e in await db.Employees.Where(e => e.DesignationId == null).ToListAsync())
            {
                if (e.Role == EmployeeRoles.Admin) { e.DesignationId = admin.Id; e.DepartmentId ??= mgmt.Id; }
                else { e.DesignationId = exec.Id; e.DepartmentId ??= sales.Id; }
            }
            await db.SaveChangesAsync();
            log.LogInformation("[Org] Starter departments and designations created");
        }
        catch (Exception ex) { log.LogWarning(ex, "[Org] Starter set skipped"); }
    }

    private const string RoleTeamsFlag = "org.marketingDesignAdded";

    /// <summary>
    /// Adds Marketing and Design departments with a Marketing Executive and a Graphic Designer designation, once, so those roles can be given
    /// their own tasks and rights. Skipped when the company already has departments by those names, and never repeated after an admin edits or deletes them.
    /// </summary>
    private static async Task AddMarketingAndDesignAsync(NuraDbContext db, ILogger log)
    {
        try
        {
            if (await db.SiteSettings.AnyAsync(s => s.Key == RoleTeamsFlag)) return;
            if (!await db.Departments.AnyAsync()) return; // nothing seeded yet; the first-run set decides

            async Task<Department> Ensure(string name, string text)
            {
                var d = await db.Departments.FirstOrDefaultAsync(x => x.Name == name);
                if (d is null) { d = new Department { Name = name, Description = text }; db.Departments.Add(d); await db.SaveChangesAsync(); }
                return d;
            }
            async Task EnsureDesignation(string name, string text, Department dep, int level, string preset)
            {
                if (await db.Designations.AnyAsync(x => x.Name == name)) return;
                var p = PageCatalog.Presets.First(x => x.Name == preset);
                db.Designations.Add(new Designation { Name = name, Description = text, DepartmentId = dep.Id, Level = level, RightsJson = PageCatalog.ToJson(p.Rights) });
                await db.SaveChangesAsync();
            }

            var marketing = await Ensure("Marketing", "Campaigns, offers, reviews, content and brand reach.");
            var design = await Ensure("Design", "Creatives, banners, packaging and video.");
            await EnsureDesignation("Marketing Executive", "Runs campaigns and keeps content and offers fresh.", marketing, 3, "Marketing");
            await EnsureDesignation("Graphic Designer", "Creates the visuals for campaigns, the website and packaging.", design, 3, "Designer");

            db.SiteSettings.Add(new SiteSetting { Key = RoleTeamsFlag, Value = "true" });
            await db.SaveChangesAsync();
            log.LogInformation("[Org] Marketing and Design departments added");
        }
        catch (Exception ex) { log.LogWarning(ex, "[Org] Marketing and Design setup skipped"); }
    }
}
