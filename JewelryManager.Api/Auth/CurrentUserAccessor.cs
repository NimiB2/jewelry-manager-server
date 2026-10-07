using JewelryManager.Api.Common.Exceptions;
using JewelryManager.Api.Data.Entities;

namespace JewelryManager.Api.Auth;

/// <summary>
/// Scoped service that exposes the authenticated user and their businessId
/// to any service in the application.
///
/// The middleware resolves the user from Firebase once per request and stores
/// it in HttpContext.Items. This accessor reads it out cleanly so services
/// don't need to know about HttpContext at all.
///
/// NestJS equivalent: TenantContext (AsyncLocalStorage-based).
/// </summary>
public class CurrentUserAccessor(IHttpContextAccessor httpContextAccessor)
{
    private const string UserKey = "CurrentUser";

    /// <summary>Returns the authenticated user for this request.</summary>
    /// <exception cref="UnauthorizedException">If no user was resolved by middleware.</exception>
    public User GetUser()
    {
        var ctx = httpContextAccessor.HttpContext
            ?? throw new UnauthorizedException();

        return ctx.Items[UserKey] as User
            ?? throw new UnauthorizedException();
    }

    /// <summary>Returns the businessId of the authenticated user.</summary>
    /// <exception cref="UnauthorizedException">If the user has no associated business.</exception>
    public Guid GetBusinessId()
    {
        var businessId = GetUser().BusinessId;

        // An employee whose account was pre-created but who hasn't been assigned to a
        // business yet would hit this. Treat it the same as unauthorized.
        return businessId ?? throw new UnauthorizedException("User is not associated with a business");
    }

    /// <summary>Stores the resolved user in HttpContext.Items. Called only by middleware.</summary>
    public void SetUser(HttpContext context, User user) =>
        context.Items[UserKey] = user;
}
