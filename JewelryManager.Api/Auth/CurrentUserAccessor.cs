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

    // Set only by a trusted system caller (a verified store webhook), which has no signed-in user.
    private Guid? _systemBusinessId;

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
        if (_systemBusinessId is { } systemBusinessId) return systemBusinessId;

        var businessId = GetUser().BusinessId;

        // An employee whose account was pre-created but who hasn't been assigned to a
        // business yet would hit this. Treat it the same as unauthorized.
        return businessId ?? throw new UnauthorizedException("User is not associated with a business");
    }

    /// <summary>
    /// Runs the rest of this request as the given business. Never call it from user-driven code: it exists
    /// for requests that were authenticated another way (a webhook whose signature was verified).
    /// </summary>
    public void UseBusiness(Guid businessId) => _systemBusinessId = businessId;

    /// <summary>Stores the resolved user in HttpContext.Items. Called only by middleware.</summary>
    public void SetUser(HttpContext context, User user) =>
        context.Items[UserKey] = user;
}
