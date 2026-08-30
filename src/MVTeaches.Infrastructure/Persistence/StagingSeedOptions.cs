namespace MVTeaches.Infrastructure.Persistence;

/// <summary>
/// Gates the entire Local Staging bootstrap (idempotent test accounts/
/// content for manual acceptance testing) — a deliberately SEPARATE class
/// from <see cref="LocalDevelopmentSeedOptions"/>, not a reuse or extension
/// of it. <see cref="LocalDevelopmentSeeder"/>'s own guard is hardcoded to
/// Development and must stay that way; Staging gets its own independent
/// options type and its own independent guard in <see cref="StagingSeeder"/>,
/// so relaxing one can never accidentally relax the other. See
/// docs/LOCAL-STAGING.md.
/// </summary>
public class StagingSeedOptions
{
    public const string SectionName = "StagingSeed";

    public bool Enabled { get; set; }

    /// <summary>The one and only database name this bootstrap will ever
    /// migrate or seed against — never defaulted to a real value here (see
    /// appsettings.Staging.json's own non-secret default of
    /// "mvteaches_staging").</summary>
    public string RequiredDatabaseName { get; set; } = string.Empty;

    /// <summary>A single shared password for every seeded staging test
    /// account (Teacher/Guardian/Student) — never defaulted to a real
    /// value; must come from a real environment variable, never a committed
    /// file. Never logged.</summary>
    public string? SeedPassword { get; set; }

    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(RequiredDatabaseName) && !string.IsNullOrWhiteSpace(SeedPassword);
}
