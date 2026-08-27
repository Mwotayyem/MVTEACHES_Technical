using global::Hangfire.Dashboard;
using MVTeaches.Infrastructure.Identity;

namespace MVTeaches.Infrastructure.Hangfire;

/// <summary>
/// §22 (security review): the Hangfire dashboard exposes job history, retry
/// controls, and payloads — it must never be reachable by an unauthenticated
/// or non-admin request. Hangfire's own default filter allows local
/// requests only, which is unsafe once deployed behind a normal reverse
/// proxy (all requests look "local" to the app).
///
/// Release-readiness audit finding: this originally checked only the literal
/// "Admin" role, matching none of this codebase's own RoleNames constants and
/// — more importantly — excluding SystemAdmin, the role RoleNames.cs itself
/// documents as "elevated over Admin" for exactly this kind of operational
/// control (job history, manual triggers). A SystemAdmin-only account could
/// not reach the dashboard at all, unlike every other admin surface in the
/// app (which authorizes Admin and SystemAdmin together).
/// </summary>
public class AdminOnlyDashboardAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();
        return httpContext.User.Identity?.IsAuthenticated == true
            && (httpContext.User.IsInRole(RoleNames.Admin) || httpContext.User.IsInRole(RoleNames.SystemAdmin));
    }
}
