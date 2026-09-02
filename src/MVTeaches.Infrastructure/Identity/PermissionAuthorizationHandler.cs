using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using MVTeaches.Infrastructure.Persistence;

namespace MVTeaches.Infrastructure.Identity;

/// <summary>
/// SystemAdmin is an unconditional Owner: the very first check below succeeds
/// for it without looking at a single claim, by design — see PermissionKeys'
/// own remarks and the owner's explicit instruction that SystemAdmin must
/// never depend on checkboxes/claims, so nothing on /Admin/AdminUsers can
/// ever revoke its own access.
///
/// A plain Admin succeeds only when a matching permission claim actually
/// exists on their AspNetUserClaims row RIGHT NOW — queried fresh against
/// the database on every single check, deliberately never read from the
/// signed-in cookie's cached ClaimsPrincipal. The cookie's claims are a
/// snapshot from login time; if this ran against those instead, revoking a
/// permission from a still-logged-in Admin would not take effect until they
/// signed out and back in (or the cookie's periodic security-stamp
/// revalidation happened to fire) — a real, if narrow, window where a
/// deliberately-revoked Admin keeps acting on a screen they were just cut
/// off from. Querying the database on every check closes that window
/// completely: a revoked permission stops working on the very next request.
///
/// Registered as Scoped (see Program.cs) specifically so this can safely
/// take a scoped DbContext — ASP.NET Core resolves authorization handlers
/// from the current request's own service scope, so this is the supported
/// way for a handler to depend on anything scoped, not a workaround.
/// </summary>
public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    private readonly MvTeachesDbContext _db;

    public PermissionAuthorizationHandler(MvTeachesDbContext db)
    {
        _db = db;
    }

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (context.User.IsInRole(RoleNames.SystemAdmin))
        {
            context.Succeed(requirement);
            return;
        }

        var userIdText = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!long.TryParse(userIdText, out var userId))
        {
            return; // Not succeeding here is a refusal, not an error - an
                     // unauthenticated or malformed principal simply has no
                     // permission, exactly like every other authorization
                     // failure in this app.
        }

        var hasClaim = await _db.UserClaims.AnyAsync(c =>
            c.UserId == userId && c.ClaimType == PermissionKeys.ClaimType && c.ClaimValue == requirement.PermissionKey);

        if (hasClaim)
        {
            context.Succeed(requirement);
        }
    }
}
