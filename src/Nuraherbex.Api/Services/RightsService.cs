using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;
using Nuraherbex.Api.Data;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Services;

/// <summary>What one signed-in person may open. Administrators (the configured owner login and employees with role admin) can open everything.</summary>
public sealed record ResolvedRights(bool Super, bool Active, Dictionary<string, int> Levels, string? Department, string? Designation)
{
    public int Level(string page) => Super ? PageCatalog.Full : Levels.GetValueOrDefault(page);
    public bool Can(string page, int level = PageCatalog.Limited) => Level(page) >= level;
    /// <summary>Sees and manages everyone's records on this page, not just their own.</summary>
    public bool All(string page) => Level(page) >= PageCatalog.Full;

    public static readonly ResolvedRights Owner = new(true, true, PageCatalog.AllFull(), null, "Administrator");
    public static readonly ResolvedRights Nobody = new(false, false, new(), null, null);
}

/// <summary>Works out page rights from the employee's designation. Cached briefly and cleared whenever rights, designations or people change.</summary>
public class RightsService(NuraDbContext db, IMemoryCache cache)
{
    private static CancellationTokenSource _reset = new();

    /// <summary>Call after any change to employees, designations or departments so the new rights apply at once.</summary>
    public static void Invalidate()
    {
        var old = Interlocked.Exchange(ref _reset, new CancellationTokenSource());
        old.Cancel();
        old.Dispose();
    }

    public async Task<ResolvedRights> ResolveAsync(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true) return ResolvedRights.Nobody;
        if (!long.TryParse(user.FindFirstValue("emp_id"), out var id))
            return user.IsInRole(EmployeeRoles.Admin) ? ResolvedRights.Owner : ResolvedRights.Nobody; // the configured owner login has no employee record
        return await ResolveEmployeeAsync(id);
    }

    public async Task<ResolvedRights> ResolveEmployeeAsync(long employeeId)
    {
        var key = $"rights:{employeeId}";
        if (cache.TryGetValue(key, out ResolvedRights? hit) && hit is not null) return hit;

        var row = await db.Employees.AsNoTracking().Where(e => e.Id == employeeId)
            .Select(e => new { e.Role, e.IsActive, e.DepartmentId, e.DesignationId }).FirstOrDefaultAsync();
        ResolvedRights result;
        if (row is null || !row.IsActive) result = ResolvedRights.Nobody;
        else if (row.Role == EmployeeRoles.Admin)
        {
            var dName = row.DesignationId is { } did ? await db.Designations.AsNoTracking().Where(d => d.Id == did).Select(d => d.Name).FirstOrDefaultAsync() : null;
            var dep = row.DepartmentId is { } pid ? await db.Departments.AsNoTracking().Where(d => d.Id == pid).Select(d => d.Name).FirstOrDefaultAsync() : null;
            result = new ResolvedRights(true, true, PageCatalog.AllFull(), dep, dName ?? "Administrator");
        }
        else
        {
            var des = row.DesignationId is { } did ? await db.Designations.AsNoTracking().Where(d => d.Id == did).FirstOrDefaultAsync() : null;
            var dep = row.DepartmentId is { } pid ? await db.Departments.AsNoTracking().Where(d => d.Id == pid).Select(d => d.Name).FirstOrDefaultAsync() : null;
            dep ??= des?.DepartmentId is { } ddid ? await db.Departments.AsNoTracking().Where(d => d.Id == ddid).Select(d => d.Name).FirstOrDefaultAsync() : null;
            // A person with no (active) designation keeps the original sales-staff access so nobody is locked out by this feature.
            var levels = des is null ? new Dictionary<string, int>(PageCatalog.DefaultStaff) : PageCatalog.FromJson(des.RightsJson);
            result = new ResolvedRights(false, true, levels, dep, des?.Name);
        }
        cache.Set(key, result, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60) }.AddExpirationToken(new CancellationChangeToken(_reset.Token)));
        return result;
    }

    /// <summary>Ids of active employees who may open the page at the given level (administrators included when <paramref name="includeAdmins"/>).</summary>
    public async Task<List<long>> EmployeesWithPageAsync(string page, int minLevel = PageCatalog.Limited, bool includeAdmins = false)
    {
        var rows = await db.Employees.AsNoTracking().Where(e => e.IsActive).Select(e => new { e.Id, e.Role, e.DesignationId }).ToListAsync();
        var json = await db.Designations.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.RightsJson);
        return rows.Where(e =>
            e.Role == EmployeeRoles.Admin ? includeAdmins
            : (e.DesignationId is { } id && json.TryGetValue(id, out var j) ? PageCatalog.LevelFrom(j, page) : PageCatalog.DefaultStaff.GetValueOrDefault(page)) >= minLevel)
            .Select(e => e.Id).ToList();
    }
}

/// <summary>Which page (and level) each admin API route needs. Anything under /api/admin not listed here is closed to everyone except administrators.</summary>
public static class PageRules
{
    public enum Kind { Open, Pages, Deny }
    public sealed record Rule(Kind Kind, string[] Pages, int MinLevel);

    private static readonly Rule Open = new(Kind.Open, [], 0);
    private static readonly Rule Deny = new(Kind.Deny, [], 0);

    private static bool Read(string method) => method is "GET" or "HEAD" or "OPTIONS";

    /// <summary>Own-record CRM pages need the first level for everything (what they may touch is scoped in the controller); store and team pages need "view" to read and "change" to write.</summary>
    private static Rule For(string method, params string[] pages)
    {
        var crm = pages.All(p => PageCatalog.Find(p)?.Kind == PageCatalog.Crm);
        return new Rule(Kind.Pages, pages, crm || Read(method) ? PageCatalog.Limited : PageCatalog.Full);
    }

    public static Rule Match(string path, string method)
    {
        path = path.TrimEnd('/').ToLowerInvariant();
        bool Is(string prefix) => path == prefix || path.StartsWith(prefix + "/");
        var m = method.ToUpperInvariant();

        if (path.StartsWith("/api/shipping/"))
        {
            var seg = path.Split('/', StringSplitOptions.RemoveEmptyEntries); // api, shipping, {id}, action
            if (seg.Length >= 3 && seg[2] == "serviceability") return Open;
            return seg.Length >= 4 && seg[3] == "track" ? For("GET", "orders") : For(m, "orders");
        }

        if (path is "/api/admin/login" or "/api/admin/verify" or "/api/admin/team" or "/api/admin/team/departments") return Open;
        if (Is("/api/admin/notifications")) return Open;

        // team and organisation
        if (Is("/api/admin/employees") || Is("/api/admin/departments") || Is("/api/admin/designations") || Is("/api/admin/rights")) return For(m, "team");
        if (path == "/api/admin/ai/reply-settings") return Read(m) ? For(m, "leads", "followups") : For(m, "team");
        if (path == "/api/admin/followups/settings") return Read(m) ? For(m, "followups") : For(m, "team");
        if (path == "/api/admin/leads/settings") return For(m, "team");

        // sales and work
        if (Is("/api/admin/crm")) return For(m, "crm");
        if (Is("/api/admin/leads")) return For(m, "leads");
        if (Is("/api/admin/followups")) return For(m, "followups");
        if (Is("/api/admin/tasks")) return For(m, "tasks");
        // Staff without the Reports page may still open the reports for pages they work on (leads, follow-ups, tasks); the controller scopes them to their own records.
        if (Is("/api/admin/reports")) return For(m, "reports", "leads", "followups", "tasks");

        // store
        if (Is("/api/admin/orders")) return For(m, "orders");
        if (Is("/api/admin/payments")) return For(m, "payments");
        if (Is("/api/admin/customers")) return For(m, "customers");
        if (Is("/api/admin/reviews")) return For(m, "reviews");
        if (Is("/api/admin/batches")) return For(m, "batches");
        if (Is("/api/admin/content")) return For(m, "content");
        if (Is("/api/admin/formulation")) return For(m, "formulation");
        if (Is("/api/admin/products") || Is("/api/admin/catalog/products")) return For(m, "products");
        if (Is("/api/admin/catalog/coupons")) return For(m, "coupons");
        if (path == "/api/admin/whatsapp/send") return For(m, "whatsapp", "orders");
        if (Is("/api/admin/whatsapp")) return For(m, "whatsapp");

        return Deny;
    }
}

/// <summary>
/// Enforces page rights on every staff API call, before the controller runs. Default is closed: a route that is not in
/// <see cref="PageRules"/> can only be used by administrators.
/// </summary>
public class PageRightsMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx, RightsService rights)
    {
        var path = ctx.Request.Path.Value ?? "";
        var user = ctx.User;
        var staffArea = path.StartsWith("/api/admin", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/api/shipping", StringComparison.OrdinalIgnoreCase);
        if (!staffArea || user.Identity?.IsAuthenticated != true || !(user.IsInRole(EmployeeRoles.Admin) || user.IsInRole(EmployeeRoles.Sales)))
        {
            await next(ctx); // customers, visitors and public routes are handled by the controllers
            return;
        }

        var r = await rights.ResolveAsync(user);
        if (!r.Active) { await Reject(ctx, 401, "This account has been deactivated."); return; }
        if (r.Super) { await next(ctx); return; }

        var rule = PageRules.Match(path, ctx.Request.Method);
        switch (rule.Kind)
        {
            case PageRules.Kind.Open: await next(ctx); return;
            case PageRules.Kind.Pages when rule.Pages.Any(p => r.Can(p, rule.MinLevel)): await next(ctx); return;
            case PageRules.Kind.Pages when rule.MinLevel > PageCatalog.Limited && rule.Pages.Any(p => r.Can(p)):
                await Reject(ctx, 403, "Your designation lets you view this page but not change anything on it."); return;
            default: await Reject(ctx, 403, "Your designation does not give you access to this page."); return;
        }
    }

    private static async Task Reject(HttpContext ctx, int status, string message)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new { success = false, message }));
    }
}
