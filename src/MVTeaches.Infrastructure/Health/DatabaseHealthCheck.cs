using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MVTeaches.Infrastructure.Persistence;

namespace MVTeaches.Infrastructure.Health;

/// <summary>
/// Deployment guide §10's own flagged gap. Deliberately minimal — a single
/// "can we reach Postgres and run a query" check, not a dependency graph of
/// every integration (Zoom/WhatsApp/MEPS are all optional-until-configured
/// per their own "not configured" stubs, so their absence must never fail
/// the app's own health check).
/// </summary>
public class DatabaseHealthCheck : IHealthCheck
{
    private readonly MvTeachesDbContext _db;

    public DatabaseHealthCheck(MvTeachesDbContext db) => _db = db;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var canConnect = await _db.Database.CanConnectAsync(cancellationToken);
            return canConnect
                ? HealthCheckResult.Healthy("PostgreSQL reachable.")
                : HealthCheckResult.Unhealthy("PostgreSQL not reachable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL health check threw.", ex);
        }
    }
}
