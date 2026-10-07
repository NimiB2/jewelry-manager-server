using FirebaseAdmin.Auth;
using JewelryManager.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace JewelryManager.Api.Auth;

/// <summary>
/// Runs on every request. Verifies the Firebase JWT, resolves the matching DB user,
/// and stores that user in HttpContext.Items for downstream use via CurrentUserAccessor.
///
/// If verification fails for any reason → 401, request is terminated immediately.
/// Routes decorated with [AllowAnonymous] are skipped entirely.
///
/// NestJS equivalent: FirebaseAuthGuard (implements CanActivate).
/// Key difference: middleware runs earlier in the pipeline than guards,
/// and can fully short-circuit the request before routing happens.
/// </summary>
public class FirebaseAuthMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext ctx,
        AppDbContext db,
        FirebaseAuth firebase,
        CurrentUserAccessor userAccessor)
    {
        // Skip auth for routes explicitly marked [AllowAnonymous].
        var endpoint = ctx.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute>() is not null)
        {
            await next(ctx);
            return;
        }

        // ── Step 1: Extract the bearer token ──────────────────────────────────
        var authHeader = ctx.Request.Headers.Authorization.ToString();
        if (!authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new { error = "Missing bearer token" });
            return;
        }

        var token = authHeader["Bearer ".Length..]; // C# range syntax: same as slice(7)

        // ── Step 2: Verify the token with Firebase ────────────────────────────
        FirebaseToken decoded;
        try
        {
            decoded = await firebase.VerifyIdTokenAsync(token);
        }
        catch
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new { error = "Invalid or expired token" });
            return;
        }

        // ── Step 3: Look up the user in our DB ────────────────────────────────
        var user = await db.Users.FirstOrDefaultAsync(u => u.FirebaseUid == decoded.Uid);

        // ── Step 4: Handle pre-invited employees (first sign-in) ─────────────
        // An owner can pre-create an employee record with just an email.
        // When that employee signs in for the first time, we link their Firebase uid
        // to the existing record instead of rejecting them.
        if (user is null && decoded.Claims.TryGetValue("email", out var emailClaim))
        {
            var email = emailClaim.ToString()!;
            var invited = await db.Users.FirstOrDefaultAsync(
                u => u.FirebaseUid == null && u.Email == email);

            if (invited is not null)
            {
                invited.FirebaseUid = decoded.Uid;
                await db.SaveChangesAsync();
                user = invited;
            }
        }

        // ── Step 5: Reject if still not found ────────────────────────────────
        if (user is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new { error = "User not recognized" });
            return;
        }

        // ── Step 6: Store the user for downstream use ─────────────────────────
        // All services access this via CurrentUserAccessor — they never touch HttpContext.
        userAccessor.SetUser(ctx, user);

        await next(ctx); // pass control to the next middleware / controller
    }
}
