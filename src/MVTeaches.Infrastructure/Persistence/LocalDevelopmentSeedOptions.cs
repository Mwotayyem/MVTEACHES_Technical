namespace MVTeaches.Infrastructure.Persistence;

/// <summary>
/// Gates the entire local-development bootstrap (auto-migrate + idempotent
/// dummy data) added for easy `F5` execution against a developer's own
/// local PostgreSQL instance. This is NOT a production feature — it is
/// refused outright unless the hosting environment is Development (checked
/// again, redundantly, inside <see cref="LocalDevelopmentSeeder"/> itself,
/// never trusted from this flag alone) AND the connected database's actual
/// name exactly matches <see cref="RequiredDatabaseName"/>, so a
/// misconfigured connection string can never point this at a shared,
/// staging, or production database by accident. See docs/LOCAL-DEVELOPMENT.md.
/// </summary>
public class LocalDevelopmentSeedOptions
{
    public const string SectionName = "LocalDevelopmentSeed";

    public bool Enabled { get; set; }

    /// <summary>The one and only database name this bootstrap will ever
    /// migrate or seed against — never defaulted to a real value here (see
    /// appsettings.Development.json's own non-secret default of
    /// "mvteaches_local").</summary>
    public string RequiredDatabaseName { get; set; } = string.Empty;

    /// <summary>A single shared password for every seeded local-only
    /// account (Admin/Teacher/Guardian/Student) — never defaulted to a real
    /// value; must come from User Secrets. Never logged.</summary>
    public string? SeedPassword { get; set; }

    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(RequiredDatabaseName) && !string.IsNullOrWhiteSpace(SeedPassword);
}
