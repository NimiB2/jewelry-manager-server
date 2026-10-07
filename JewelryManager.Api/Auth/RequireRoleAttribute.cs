using JewelryManager.Api.Common.Exceptions;
using JewelryManager.Api.Data.Entities;
using Microsoft.AspNetCore.Mvc.Filters;

namespace JewelryManager.Api.Auth;

/// <summary>
/// Only checks the caller's role; identity is resolved earlier by FirebaseAuthMiddleware.
/// Implemented as an action filter (not an authorization filter) so the thrown
/// ForbiddenException is still mapped to 403 by GlobalExceptionFilter.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class RequireRoleAttribute(params Role[] roles) : Attribute, IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        var user = context.HttpContext.RequestServices
            .GetRequiredService<CurrentUserAccessor>().GetUser();

        if (!roles.Contains(user.Role))
            throw new ForbiddenException("Insufficient role");
    }

    public void OnActionExecuted(ActionExecutedContext context) { }
}
