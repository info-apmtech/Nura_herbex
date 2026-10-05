using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Nuraherbex.Api.Services;

/// <summary>Marks only [Authorize] endpoints as requiring the Bearer token in Swagger; [AllowAnonymous] ones (public store, webhooks) stay open.</summary>
public class AuthorizeOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var meta = context.ApiDescription.ActionDescriptor.EndpointMetadata;
        var needsAuth = meta.OfType<IAuthorizeData>().Any() && !meta.OfType<IAllowAnonymous>().Any();
        if (!needsAuth) return;

        var roles = string.Join(", ", meta.OfType<IAuthorizeData>().Select(a => a.Roles).Where(r => !string.IsNullOrEmpty(r)).Distinct());
        operation.Summary = string.IsNullOrEmpty(operation.Summary) ? null : operation.Summary;
        operation.Description = $"{operation.Description}\n\n🔒 Requires login{(roles.Length > 0 ? $" (role: {roles})" : "")} — use Authorize at the top of the page.".Trim();
        operation.Security ??= [];
        operation.Security.Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", context.Document)] = [] });
    }
}
