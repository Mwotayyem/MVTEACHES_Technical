using global::Hangfire.Dashboard;

namespace MVTeaches.Infrastructure.Hangfire;

/// <summary>
/// §22 (security review): the Hangfire dashboard exposes job history, retry
/// controls, and payloads — it must never be reachable by an unauthenticated
/// or non-admin request. Hangfire's own default filter allows local
/// requests only, which is unsafe once deployed behind a normal reverse
/// proxy (all requests look "local" to the app).
/// </summary>
public class AdminOnlyDashboardAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();
        return httpContext.User.Identity?.IsAuthenticated == true
            && httpContext.User.IsInRole("Admin");
    }
}
