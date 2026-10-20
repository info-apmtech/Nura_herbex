using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Nuraherbex.Api.Services;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.Api.Controllers;

/// <summary>
/// Shared identity and rights helpers for staff controllers. Page rights themselves are enforced for every route by
/// <see cref="PageRightsMiddleware"/>; here controllers only ask "may this person see everyone's records on this page?".
/// </summary>
public abstract class StaffControllerBase(RightsService rightsService) : ControllerBase, IAsyncActionFilter
{
    protected ResolvedRights Rights { get; private set; } = ResolvedRights.Nobody;

    /// <summary>The owner login or an employee with role admin: every page, every record.</summary>
    protected bool IsAdmin => Rights.Super;
    protected bool CanAll(string page) => Rights.All(page);
    protected int Level(string page) => Rights.Level(page);
    protected long? MyId => long.TryParse(User.FindFirstValue("emp_id"), out var id) ? id : null;
    protected string MyName => User.FindFirstValue(ClaimTypes.Name) ?? User.FindFirstValue(ClaimTypes.Email) ?? "admin";

    /// <summary>A token stays valid for hours; rights are read fresh (briefly cached) so a deactivated person, or a changed designation, takes effect at once.</summary>
    [NonAction]
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        Rights = await rightsService.ResolveAsync(User);
        if (!Rights.Active)
        {
            context.Result = Unauthorized(new ApiResult { Success = false, Message = "This account has been deactivated." });
            return;
        }
        await next();
    }
}
