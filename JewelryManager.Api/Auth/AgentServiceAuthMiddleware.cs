using System.Security.Cryptography;
using System.Text;
using JewelryManager.Api.Data;
using JewelryManager.Api.Features.Agent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace JewelryManager.Api.Auth;

/// <summary>
/// Lets the WhatsApp agent call the API as one of its whitelisted people. The agent proves itself with a shared
/// secret and names the person; the tenant is then resolved exactly as for a signed-in user, so every query is
/// still filtered by that person's business. Only a short list of API calls is open to this identity.
///
/// A request that carries the agent header but fails any check is refused outright: it never falls through
/// to the Firebase login.
/// </summary>
public class AgentServiceAuthMiddleware(RequestDelegate next)
{
    public const string KeyHeader = "X-Agent-Key";
    public const string UserHeader = "X-Agent-User";

    public async Task InvokeAsync(HttpContext ctx, AppDbContext db, CurrentUserAccessor userAccessor, IOptions<AgentOptions> options)
    {
        if (!ctx.Request.Headers.TryGetValue(KeyHeader, out var sentKey))
        {
            await next(ctx);
            return;
        }

        var config = options.Value;
        if (!config.Enabled || string.IsNullOrWhiteSpace(config.ServiceKey) || !SameSecret(sentKey.ToString(), config.ServiceKey))
        {
            await Refuse(ctx, "Invalid agent key");
            return;
        }

        if (!AgentRouteGuard.IsAllowed(ctx.Request.Method, ctx.Request.Path.Value ?? "/"))
        {
            await Refuse(ctx, "This call is not open to the agent", StatusCodes.Status403Forbidden);
            return;
        }

        if (!Guid.TryParse(ctx.Request.Headers[UserHeader].ToString(), out var agentUserId))
        {
            await Refuse(ctx, "Missing agent user");
            return;
        }

        var agentUser = await db.AgentUsers.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentUserId && a.IsActive);
        var user = agentUser is null
            ? null
            : await db.Users.FirstOrDefaultAsync(u => u.Id == agentUser.UserId && u.BusinessId == agentUser.BusinessId);
        if (user is null)
        {
            await Refuse(ctx, "Unknown agent user");
            return;
        }

        userAccessor.SetUser(ctx, user);
        await next(ctx);
    }

    // Compared in constant time so the secret cannot be guessed byte by byte from response times.
    private static bool SameSecret(string sent, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(sent)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

    private static async Task Refuse(HttpContext ctx, string message, int status = StatusCodes.Status401Unauthorized)
    {
        ctx.Response.StatusCode = status;
        await ctx.Response.WriteAsJsonAsync(new { error = message });
    }
}
