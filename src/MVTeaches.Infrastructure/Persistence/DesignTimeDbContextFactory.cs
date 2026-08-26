using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MVTeaches.Infrastructure.Persistence;

/// <summary>
/// Used ONLY by `dotnet ef migrations add/update` at design time. The
/// connection string here points at a disposable local development cluster
/// (see /docs/deployment — this is NOT a production credential, and no
/// production connection string is ever committed to source control: §40 of
/// the master engineering prompt, "never commit production secrets").
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<MvTeachesDbContext>
{
    public MvTeachesDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("MVTEACHES_DESIGN_TIME_CONNECTION")
            ?? "Host=127.0.0.1;Port=5433;Database=mvteaches_dev;Username=mvteaches_dev";

        var optionsBuilder = new DbContextOptionsBuilder<MvTeachesDbContext>();
        optionsBuilder.UseNpgsql(connectionString, o => o.UseNodaTime());

        return new MvTeachesDbContext(optionsBuilder.Options);
    }
}
