using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MVTeaches.Domain.Catalog;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using Npgsql;
using Xunit;

namespace MVTeaches.Tests.Persistence;

/// <summary>
/// Local-development bootstrap requested for easy `F5` execution — see
/// docs/LOCAL-DEVELOPMENT.md. These tests run against a REAL ASP.NET Core
/// host (WebApplicationFactory) so every safety gate
/// (Development-only, Enabled flag, exact-database-name guard) is exercised
/// the same way it actually runs in production, not mocked.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class LocalDevelopmentSeederTests : IClassFixture<LocalDevelopmentSeederTests.EnabledFactory>, IClassFixture<LocalDevelopmentSeederTests.DisabledFactory>, IAsyncLifetime
{
    private readonly TestDatabaseFixture _fixture;
    private readonly EnabledFactory _enabledFactory;
    private readonly DisabledFactory _disabledFactory;

    public LocalDevelopmentSeederTests(TestDatabaseFixture fixture, EnabledFactory enabledFactory, DisabledFactory disabledFactory)
    {
        // Both factories point at the SAME real test database as every other
        // test in this suite — LocalDevelopmentSeeder's own database-name
        // guard is configured to match it exactly, so the "enabled" factory
        // below is a genuine, non-destructive exercise of the real
        // migrate+seed path, not a special database of its own.
        Environment.SetEnvironmentVariable("ConnectionStrings__MvTeaches", fixture.ConnectionString);
        _fixture = fixture;
        _enabledFactory = enabledFactory;
        _disabledFactory = disabledFactory;
    }

    private static string RealDatabaseName(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString).Database!;

    /// <summary>DataSeeder.SeedLevelsAsync/SeedCountriesAsync each skip their
    /// WHOLE batch if ANY row of that type already exists — correct and
    /// sufficient for a genuinely fresh production database, but this shared
    /// test database already has other tests' own arbitrary Level/Country
    /// rows in it by the time this class's own WebApplicationFactory host
    /// happens to start, so those two specific ids LocalDevelopmentSeeder
    /// depends on (A1 = 1, Jordan = 1) are not reliably present otherwise.
    /// Ensures exactly those two specific rows exist, by id, without
    /// touching DataSeeder itself (whose behaviour is correct for its real,
    /// production use case).</summary>
    public async Task InitializeAsync()
    {
        await using var db = _fixture.CreateContext();
        if (!await db.Levels.AnyAsync(l => l.Id == 1))
        {
            db.Levels.Add(new Level(1, "A1", "مبتدئ", "Beginner", 1));
        }
        if (!await db.Countries.AnyAsync(c => c.Id == 1))
        {
            db.Countries.Add(new Country(1, "JO", "الأردن", "Jordan", "JOD", "+962", "Asia/Amman"));
        }
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // All four tests in this class share ONE persistent, real Postgres
    // database (the same one every other test in this suite uses) — there
    // is no per-test rollback. Since LocalDevelopmentSeeder always seeds the
    // SAME fixed, documented email addresses by design, a "refuses to seed"
    // test can never assert flat non-existence (a sibling test's own,
    // legitimate happy-path run may have already created that exact row
    // first, in whichever order xUnit happens to pick — it does not
    // guarantee declaration order). The correct, order-independent check is
    // a before/after DELTA: does calling SeedAsync change whether the row
    // exists, not whether it happens to exist at all right now.
    private static async Task<bool> LocalTeacherExistsAsync(IServiceProvider services) =>
        await services.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync("local-teacher@mvteaches.local") is not null;

    [Fact]
    public async Task Disabled_by_default_seeds_nothing()
    {
        // DisabledFactory applies no configuration override at all — this is
        // the shipped appsettings.Development.json default
        // (LocalDevelopmentSeed:Enabled = false), the state every fresh
        // clone actually starts in.
        using var scope = _disabledFactory.Services.CreateScope();
        var existedBefore = await LocalTeacherExistsAsync(scope.ServiceProvider);

        await LocalDevelopmentSeeder.SeedAsync(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<IHostEnvironment>());

        Assert.Equal(existedBefore, await LocalTeacherExistsAsync(scope.ServiceProvider));
    }

    /// <summary>Defence in depth: even if every other gate were somehow
    /// misconfigured, a non-Development environment name alone must refuse.</summary>
    [Fact]
    public async Task Refuses_outside_development_regardless_of_configuration()
    {
        using var scope = _enabledFactory.Services.CreateScope();
        var existedBefore = await LocalTeacherExistsAsync(scope.ServiceProvider);
        var nonDevelopmentEnv = new FakeHostEnvironment("Production");

        await LocalDevelopmentSeeder.SeedAsync(scope.ServiceProvider, nonDevelopmentEnv);

        Assert.Equal(existedBefore, await LocalTeacherExistsAsync(scope.ServiceProvider));
    }

    /// <summary>The database-name guard: even with Enabled=true and a real
    /// password configured, a mismatched RequiredDatabaseName must refuse
    /// outright — this is what stops a misconfigured connection string from
    /// ever seeding a shared/staging/production database.</summary>
    [Fact]
    public async Task Refuses_when_the_connected_database_name_does_not_match()
    {
        var factory = _enabledFactory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LocalDevelopmentSeed:Enabled"] = "true",
                ["LocalDevelopmentSeed:RequiredDatabaseName"] = "definitely_not_the_real_database",
                ["LocalDevelopmentSeed:SeedPassword"] = "Local-Dev-Password-123!",
            })));

        using var scope = factory.Services.CreateScope();
        var existedBefore = await LocalTeacherExistsAsync(scope.ServiceProvider);
        var env = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();

        await LocalDevelopmentSeeder.SeedAsync(scope.ServiceProvider, env);

        Assert.Equal(existedBefore, await LocalTeacherExistsAsync(scope.ServiceProvider));
    }

    /// <summary>The real happy path, run twice: the first call seeds every
    /// account, the second call (a second `F5`) must create no duplicates —
    /// exactly the guarantee the local-development bootstrap requires.</summary>
    [Fact]
    public async Task Seeding_twice_creates_every_account_exactly_once()
    {
        using var scope = _enabledFactory.Services.CreateScope();
        var env = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();

        await LocalDevelopmentSeeder.SeedAsync(scope.ServiceProvider, env);
        await LocalDevelopmentSeeder.SeedAsync(scope.ServiceProvider, env); // simulates a second F5

        var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var teacherUser = await userManager.FindByEmailAsync("local-teacher@mvteaches.local");
        var guardianUser = await userManager.FindByEmailAsync("local-guardian@mvteaches.local");
        var studentUser = await userManager.FindByEmailAsync("local-student@mvteaches.local");
        Assert.NotNull(teacherUser);
        Assert.NotNull(guardianUser);
        Assert.NotNull(studentUser);

        Assert.Equal(1, await db.Teachers.CountAsync(t => t.UserId == teacherUser!.Id));
        Assert.Equal(1, await db.Guardians.CountAsync(g => g.UserId == guardianUser!.Id));
        var guardian = await db.Guardians.FirstAsync(g => g.UserId == guardianUser!.Id);
        Assert.Equal(2, await db.Guardianships.CountAsync(g => g.GuardianId == guardian.Id)); // exactly two children, not four

        // The direct-login student is deliberately never assigned a level —
        // the whole point of this account is demonstrating the placement-test CTA.
        Assert.False(await db.StudentLevels.AnyAsync(l => l.StudentId == db.Students.Where(s => s.UserId == studentUser!.Id).Select(s => s.Id).First()));

        Assert.Equal(1, await db.PricingPlans.CountAsync(p => p.LevelId == 1 && p.SessionType == Domain.Catalog.SessionType.Group));
        Assert.Equal(1, await db.PricingPlans.CountAsync(p => p.LevelId == 1 && p.SessionType == Domain.Catalog.SessionType.Private));
        Assert.Equal(1, await db.PlacementTestVersions.CountAsync(v => v.IsActive));
        Assert.Equal(2, await db.ClassSessions.CountAsync(s => s.TeacherId == db.Teachers.Where(t => t.UserId == teacherUser!.Id).Select(t => t.Id).First()));
    }

    /// <summary>A fake <see cref="IHostEnvironment"/> with an arbitrary
    /// environment name — used only to prove the Development check itself,
    /// without needing a second real host.</summary>
    private class FakeHostEnvironment : IHostEnvironment
    {
        public FakeHostEnvironment(string environmentName) => EnvironmentName = environmentName;
        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "MVTeaches.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    /// <summary>Hosts the real app with LocalDevelopmentSeed enabled and
    /// pointed at this run's real test database — see the constructor's own
    /// remarks on why this is safe (it's the same database every other test
    /// already uses, not a separate one).</summary>
    public class EnabledFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__MvTeaches")!;
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["LocalDevelopmentSeed:Enabled"] = "true",
                    ["LocalDevelopmentSeed:RequiredDatabaseName"] = RealDatabaseName(connectionString),
                    ["LocalDevelopmentSeed:SeedPassword"] = "Local-Dev-Password-123!",
                    // Exercises the admin-dependent seeds too (pricing plans,
                    // dummy placement test, sample expense) — without this,
                    // BootstrapAdminOptions.IsConfigured is false and
                    // LocalDevelopmentSeeder correctly, silently skips them.
                    ["Bootstrap:AdminEmail"] = "local-admin-test@mvteaches.local",
                    ["Bootstrap:AdminPassword"] = "Local-Dev-Password-123!",
                });
            });
        }
    }

    /// <summary>Hosts the real app with no override at all — exactly the
    /// shipped appsettings.Development.json default a fresh clone starts with.</summary>
    public class DisabledFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Development");
    }
}
