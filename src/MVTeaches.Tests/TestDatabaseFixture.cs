using Microsoft.EntityFrameworkCore;
using MVTeaches.Infrastructure.Persistence;

namespace MVTeaches.Tests;

/// <summary>
/// Points at a disposable local PostgreSQL 16 cluster used ONLY for this test
/// run (see /docs/deployment for why: no Docker/Testcontainers were available
/// in the environment these tests were authored in, so a real local `initdb`
/// cluster on a non-default port was used instead — this is a genuine
/// PostgreSQL 16 server, not SQLite/InMemory, so the EXCLUDE constraint, the
/// partial unique indexes, and the append-only trigger are all exercised for
/// real). A fresh, empty database is created and migrated once per test run.
/// </summary>
public class TestDatabaseFixture : IAsyncLifetime
{
    private const string AdminConnectionString = "Host=127.0.0.1;Port=5433;Database=postgres;Username=mvteaches_dev";
    private const string TestDbName = "mvteaches_test";

    public string ConnectionString { get; } = $"Host=127.0.0.1;Port=5433;Database={TestDbName};Username=mvteaches_dev";

    public async Task InitializeAsync()
    {
        await using (var adminConn = new Npgsql.NpgsqlConnection(AdminConnectionString))
        {
            await adminConn.OpenAsync();
            await using (var terminate = adminConn.CreateCommand())
            {
                terminate.CommandText =
                    $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{TestDbName}' AND pid <> pg_backend_pid();";
                await terminate.ExecuteNonQueryAsync();
            }

            await using (var drop = adminConn.CreateCommand())
            {
                drop.CommandText = $"DROP DATABASE IF EXISTS {TestDbName};";
                await drop.ExecuteNonQueryAsync();
            }

            await using (var create = adminConn.CreateCommand())
            {
                create.CommandText = $"CREATE DATABASE {TestDbName};";
                await create.ExecuteNonQueryAsync();
            }
        }

        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public MvTeachesDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MvTeachesDbContext>()
            .UseNpgsql(ConnectionString, o => o.UseNodaTime())
            .Options;
        return new MvTeachesDbContext(options);
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

[CollectionDefinition(nameof(DatabaseCollection))]
public class DatabaseCollection : ICollectionFixture<TestDatabaseFixture>
{
}
