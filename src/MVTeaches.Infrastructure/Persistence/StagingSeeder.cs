using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MVTeaches.Application.Payments;
using MVTeaches.Application.Placement;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Common;
using MVTeaches.Domain.Payments;
using MVTeaches.Domain.People;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Application.People;
using MVTeaches.Infrastructure.Identity;
using NodaTime;

namespace MVTeaches.Infrastructure.Persistence;

/// <summary>
/// Local Staging bootstrap: applies pending migrations and inserts a small,
/// clearly-labelled set of test accounts/content so a real acceptance pass
/// can exercise every role against real services and a real (but isolated)
/// database — no repository fork, no simulated business logic.
///
/// Deliberately a SEPARATE class from <see cref="LocalDevelopmentSeeder"/>,
/// not an extension of it — see <see cref="StagingSeedOptions"/>'s own
/// remarks on why. The safety pattern is the same shape (defence in depth,
/// never trust just one gate) but every gate here is independently
/// evaluated against Staging's own environment/options/database name:
/// 1. The caller (Program.cs) only invokes this inside an
///    `app.Environment.IsStaging()` block.
/// 2. This method independently re-checks IsStaging() itself.
/// 3. <see cref="StagingSeedOptions.Enabled"/> must be explicitly true.
/// 4. The actually-connected database's name must exactly equal
///    <see cref="StagingSeedOptions.RequiredDatabaseName"/> — refuses
///    outright (migrating AND seeding) on any mismatch, so a
///    misconfigured connection string can never point this at
///    Development's or a future Production database.
/// Every insert is an idempotent "if it doesn't already exist" check;
/// passwords are reconciled (reset) only when they no longer match the
/// configured seed password, never unconditionally on every run.
/// </summary>
public static class StagingSeeder
{
    // Levels seeded by DataSeeder.SeedLevelsAsync — see its own remarks for
    // why these are fixed ids, not looked up by code, in that one place.
    private const int A1LevelId = 1;
    private const string StagingDomain = "@staging.mvteaches.local";
    private const string TestDataMarker = "[STAGING TEST DATA]";

    private static async Task<(MvTeachesDbContext Db, ILogger Logger, StagingSeedOptions Options)?> CheckGatesAsync(
        IServiceProvider services, IHostEnvironment env, CancellationToken cancellationToken)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("MVTeaches.StagingSeed");

        if (!env.IsStaging())
        {
            // Never reachable via Program.cs's own outer guard, but this
            // method must never trust a single caller to be the only thing
            // standing between it and a non-Staging environment.
            return null;
        }

        var options = services.GetRequiredService<IOptions<StagingSeedOptions>>().Value;
        if (!options.Enabled)
        {
            return null; // the ordinary, safe, do-nothing default
        }

        if (!options.IsConfigured)
        {
            logger.LogWarning(
                "StagingSeed:Enabled is true, but RequiredDatabaseName or SeedPassword is missing (SeedPassword " +
                "must come from a real environment variable, never appsettings.Staging.json). See " +
                "docs/LOCAL-STAGING.md — skipping the staging bootstrap entirely.");
            return null;
        }

        var db = services.GetRequiredService<MvTeachesDbContext>();
        var actualDatabaseName = db.Database.GetDbConnection().Database;
        if (!string.Equals(actualDatabaseName, options.RequiredDatabaseName, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogCritical(
                "StagingSeed is enabled, but the connected database is '{Actual}', not the configured " +
                "'{Required}'. Refusing to migrate or seed anything against it. Fix ConnectionStrings__MvTeaches " +
                "or StagingSeed__RequiredDatabaseName.", actualDatabaseName, options.RequiredDatabaseName);
            return null;
        }

        bool canConnect;
        try
        {
            canConnect = await db.Database.CanConnectAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            canConnect = false;
            logger.LogCritical(ex,
                "StagingSeed: could not reach PostgreSQL for database '{Database}'. See docs/LOCAL-STAGING.md.",
                actualDatabaseName);
        }
        if (!canConnect)
        {
            logger.LogCritical(
                "StagingSeed: could not connect to database '{Database}'. Does it exist yet? See " +
                "docs/LOCAL-STAGING.md's setup steps. Skipping migration and seeding for this run.", actualDatabaseName);
            return null;
        }

        return (db, logger, options);
    }

    /// <summary>Must run BEFORE <see cref="DataSeeder.SeedAsync"/> and this
    /// class's own <see cref="SeedAsync"/> — both query tables that only
    /// exist once migrations have actually been applied. See Program.cs's
    /// ordering (mirrors LocalDevelopmentSeeder's own).</summary>
    public static async Task MigrateIfEnabledAsync(IServiceProvider services, IHostEnvironment env, CancellationToken cancellationToken = default)
    {
        var gates = await CheckGatesAsync(services, env, cancellationToken);
        if (gates is null)
        {
            return;
        }

        var (db, logger, _) = gates.Value;
        logger.LogInformation("StagingSeed: applying pending migrations against '{Database}'.", db.Database.GetDbConnection().Database);
        await db.Database.MigrateAsync(cancellationToken);
    }

    public static async Task SeedAsync(IServiceProvider services, IHostEnvironment env, CancellationToken cancellationToken = default)
    {
        var gates = await CheckGatesAsync(services, env, cancellationToken);
        if (gates is null)
        {
            return;
        }

        var (db, logger, options) = gates.Value;

        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var levelAuthorization = services.GetRequiredService<ITeacherLevelAuthorizationService>();
        var placementAdmin = services.GetRequiredService<IPlacementTestAdminService>();
        var paymentMethods = services.GetRequiredService<IPaymentMethodConfigService>();
        var clock = services.GetRequiredService<IClock>();
        var now = clock.GetCurrentInstant();

        // The bootstrap admin (Bootstrap:AdminEmail/AdminPassword, same
        // production-safe mechanism DataSeeder.SeedAsync already ran for
        // every environment) must already exist for the "created by" audit
        // fields below to have someone to point at. Not configuring it just
        // means those specific steps skip — this bootstrap never invents
        // its own separate admin account.
        var adminUser = await FindAnyAdminAsync(userManager, cancellationToken);
        long? adminUserId = adminUser?.Id;
        if (adminUserId is null)
        {
            logger.LogWarning(
                "StagingSeed: no Admin account exists yet — set Bootstrap__AdminEmail/Bootstrap__AdminPassword " +
                "(real environment variables) and restart before this bootstrap can seed a teacher/plans/placement " +
                "test that need an acting admin id. Guardian/student accounts below are unaffected.");
        }

        var courseId = await db.Courses.Where(c => c.Code == "GENERAL-ENGLISH").Select(c => (long?)c.Id).FirstOrDefaultAsync(cancellationToken)
            ?? await db.Courses.Select(c => (long?)c.Id).FirstOrDefaultAsync(cancellationToken);
        const int countryId = 1; // JO — seeded by DataSeeder.SeedCountriesAsync
        const int adultsAgeGroupId = 3; // seeded by DataSeeder.SeedAgeGroupsAsync

        var teacherId = await SeedTeacherAsync(db, userManager, levelAuthorization, options.SeedPassword!, adminUserId, now, logger, cancellationToken);
        await SeedGuardianAndChildrenAsync(db, userManager, countryId, options.SeedPassword!, logger, cancellationToken);
        await SeedDirectLoginStudentAsync(db, userManager, countryId, options.SeedPassword!, logger, cancellationToken);

        if (adminUserId is not null)
        {
            await SeedPaymentMethodAsync(paymentMethods, adminUserId.Value, cancellationToken, logger);
        }

        if (courseId is null)
        {
            logger.LogWarning("StagingSeed: no course exists yet (DataSeeder should have created one) — skipping pricing plans and sample sessions this run.");
        }
        else
        {
            if (adminUserId is not null)
            {
                await SeedPricingPlansAsync(db, countryId, courseId.Value, adminUserId.Value, now, logger, cancellationToken);
            }

            if (teacherId is not null)
            {
                await SeedFutureSessionsAsync(db, countryId, courseId.Value, adultsAgeGroupId, teacherId.Value, now, logger, cancellationToken);
            }
        }

        if (adminUserId is not null)
        {
            await SeedTestPlacementAsync(placementAdmin, adminUserId.Value, cancellationToken, logger);
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("StagingSeed: Local Staging test data is ready — see docs/LOCAL-STAGING.md for the seeded account list.");
    }

    private static async Task<ApplicationUser?> FindAnyAdminAsync(UserManager<ApplicationUser> userManager, CancellationToken ct)
    {
        var admins = await userManager.GetUsersInRoleAsync(RoleNames.Admin);
        return admins.FirstOrDefault();
    }

    private static async Task<ApplicationUser?> FindKnownStagingUserAsync(UserManager<ApplicationUser> userManager, string email, CancellationToken ct)
    {
        var existing = await userManager.FindByEmailAsync(email);
        if (existing is not null)
        {
            return existing;
        }

        var normalizedEmail = userManager.NormalizeEmail(email);
        return await userManager.Users.SingleOrDefaultAsync(
            user => user.NormalizedEmail == normalizedEmail || user.Email == email, ct);
    }

    private static async Task<ApplicationUser> CreateOrReconcileUserAsync(
        UserManager<ApplicationUser> userManager, string email, string password, IReadOnlyCollection<string> roles,
        ILogger logger, CancellationToken ct, int? countryId = null)
    {
        var existing = await FindKnownStagingUserAsync(userManager, email, ct);
        if (existing is not null)
        {
            existing.UserName = email;
            existing.Email = email;
            existing.EmailConfirmed = true;
            if (countryId is not null)
            {
                existing.CountryId = countryId;
            }
            await userManager.UpdateAsync(existing);

            if (await userManager.IsLockedOutAsync(existing))
            {
                await userManager.SetLockoutEndDateAsync(existing, null);
            }
            if (existing.AccessFailedCount > 0)
            {
                await userManager.ResetAccessFailedCountAsync(existing);
            }
            foreach (var role in roles)
            {
                if (!await userManager.IsInRoleAsync(existing, role))
                {
                    ThrowIfFailed(await userManager.AddToRoleAsync(existing, role), $"StagingSeed could not add '{email}' to '{role}'", logger, email);
                }
            }

            // Idempotent on purpose: only reset the password when it no
            // longer matches the configured seed password, never
            // unconditionally on every run.
            if (!await userManager.CheckPasswordAsync(existing, password))
            {
                IdentityResult passwordResult = await userManager.HasPasswordAsync(existing)
                    ? await userManager.ResetPasswordAsync(existing, await userManager.GeneratePasswordResetTokenAsync(existing), password)
                    : await userManager.AddPasswordAsync(existing, password);
                ThrowIfFailed(passwordResult, $"StagingSeed could not reconcile the password for '{email}'", logger, email);
                logger.LogInformation("StagingSeed: reconciled the configured seed password for {Email}.", email);
            }

            return existing;
        }

        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, CountryId = countryId };
        ThrowIfFailed(await userManager.CreateAsync(user, password), $"StagingSeed could not create '{email}'", logger, email);
        foreach (var role in roles)
        {
            ThrowIfFailed(await userManager.AddToRoleAsync(user, role), $"StagingSeed could not add '{email}' to '{role}'", logger, email);
        }
        return user;
    }

    private static void ThrowIfFailed(IdentityResult result, string message, ILogger logger, string email)
    {
        if (result.Succeeded)
        {
            return;
        }
        logger.LogError("StagingSeed: could not reconcile {Email}: {Errors}", email, string.Join("; ", result.Errors.Select(e => e.Description)));
        throw new InvalidOperationException(message + " — see the log above.");
    }

    private static async Task<long?> SeedTeacherAsync(MvTeachesDbContext db, UserManager<ApplicationUser> userManager,
        ITeacherLevelAuthorizationService levelAuthorization, string password, long? adminUserId, Instant now, ILogger logger, CancellationToken ct)
    {
        var email = "staging-teacher" + StagingDomain;
        var user = await CreateOrReconcileUserAsync(userManager, email, password, new[] { RoleNames.Teacher }, logger, ct);

        var teacher = await db.Teachers.FirstOrDefaultAsync(t => t.UserId == user.Id, ct);
        if (teacher is null)
        {
            teacher = new Teacher(user.Id, $"{TestDataMarker} Teacher", "Asia/Amman");
            db.Teachers.Add(teacher);
            await db.SaveChangesAsync(ct);
        }

        if (!await levelAuthorization.IsAuthorizedForLevelAsync(teacher.Id, A1LevelId, ct))
        {
            await levelAuthorization.GrantAsync(teacher.Id, A1LevelId, adminUserId ?? user.Id, ct);
        }

        // Deliberately NOT given a TeacherMeetingConnection — faking Zoom/
        // Google OAuth tokens would weaken the real "not ready for online
        // sessions" rule. See docs/LOCAL-STAGING.md for exactly which
        // actions that blocks (Start a session, publish new slots).
        return teacher.Id;
    }

    private static async Task SeedGuardianAndChildrenAsync(MvTeachesDbContext db, UserManager<ApplicationUser> userManager,
        int countryId, string password, ILogger logger, CancellationToken ct)
    {
        var email = "staging-guardian" + StagingDomain;
        var user = await CreateOrReconcileUserAsync(userManager, email, password, new[] { RoleNames.Guardian }, logger, ct);

        var guardian = await db.Guardians.FirstOrDefaultAsync(g => g.UserId == user.Id, ct);
        if (guardian is null)
        {
            guardian = new Guardian(user.Id, $"{TestDataMarker} Guardian");
            db.Guardians.Add(guardian);
            await db.SaveChangesAsync(ct);
        }

        var existingChildren = await db.Guardianships.Where(g => g.GuardianId == guardian.Id).Select(g => g.StudentId).ToListAsync(ct);
        if (existingChildren.Count > 0)
        {
            return;
        }

        // Two SEPARATE, independent children — no shared login, no shared
        // placement/level/balance — exactly what proves guardian-side
        // isolation in acceptance step 9.
        var child1 = new Student(countryId, $"{TestDataMarker} Child One", new LocalDate(2014, 3, 1));
        var child2 = new Student(countryId, $"{TestDataMarker} Child Two", new LocalDate(2016, 7, 15));
        child1.MarkVerified();
        child2.MarkVerified();
        db.Students.AddRange(child1, child2);
        await db.SaveChangesAsync(ct);

        db.Guardianships.AddRange(
            new Guardianship(guardian.Id, child1.Id, GuardianRelationship.Parent, isPrimary: true, user.Id),
            new Guardianship(guardian.Id, child2.Id, GuardianRelationship.Parent, isPrimary: true, user.Id));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>No StudentLevel row yet (verified but PendingLevel) — this
    /// account demonstrates "no placement result yet ⟹ the purchase CTA,
    /// not a package list" (acceptance step 3) before taking the test.</summary>
    private static async Task SeedDirectLoginStudentAsync(MvTeachesDbContext db, UserManager<ApplicationUser> userManager,
        int countryId, string password, ILogger logger, CancellationToken ct)
    {
        var email = "staging-student" + StagingDomain;
        var user = await CreateOrReconcileUserAsync(userManager, email, password, new[] { RoleNames.Student }, logger, ct, countryId);

        if (await db.Students.AnyAsync(s => s.UserId == user.Id, ct))
        {
            return;
        }

        var student = new Student(countryId, $"{TestDataMarker} Direct Student", new LocalDate(2010, 5, 20), user.Id);
        student.MarkVerified();
        db.Students.Add(student);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Section 4/5's manual transfer flow needs at least one active
    /// payment method to choose from — a CliQ alias is the simplest real
    /// shape and exercises the exact same admin-authoring path a real
    /// deployment would use, never a raw database insert.</summary>
    private static async Task SeedPaymentMethodAsync(IPaymentMethodConfigService paymentMethods, long adminUserId,
        CancellationToken ct, ILogger logger)
    {
        var existing = await paymentMethods.ListAllAsync(ct);
        if (existing.Any())
        {
            return;
        }

        await paymentMethods.CreateAsync(PaymentMethod.CliQ, $"{TestDataMarker} Beneficiary", "staging-cliq-alias",
            iban: null, bankName: null, swiftBic: null, countryName: "Jordan",
            instructions: $"{TestDataMarker} — do not send real money to this alias.",
            acceptedCurrencies: new[] { "JOD" }, adminUserId, ct);
    }

    private static async Task SeedPricingPlansAsync(MvTeachesDbContext db, int countryId, long courseId, long adminUserId,
        Instant now, ILogger logger, CancellationToken ct)
    {
        var today = now.InUtc().Date;
        var hasA1Group = await db.PricingPlans.AnyAsync(p => p.CourseId == courseId && p.LevelId == A1LevelId && p.SessionType == SessionType.Group, ct);
        var hasA1Private = await db.PricingPlans.AnyAsync(p => p.CourseId == courseId && p.LevelId == A1LevelId && p.SessionType == SessionType.Private, ct);

        if (!hasA1Group)
        {
            db.PricingPlans.Add(new PricingPlan(countryId, courseId, A1LevelId, null, SessionType.Group,
                sessionsCount: 10, minutesTotal: 600, new Money(50m, "JOD"), validityDays: 90, today, adminUserId));
        }
        if (!hasA1Private)
        {
            db.PricingPlans.Add(new PricingPlan(countryId, courseId, A1LevelId, null, SessionType.Private,
                sessionsCount: 5, minutesTotal: 300, new Money(120m, "JOD"), validityDays: 90, today, adminUserId));
        }
    }

    /// <summary>Same deterministic-outcome shape as LocalDevelopmentSeeder's
    /// own dummy test: trivial, clearly-marked placeholder content, one
    /// score range spanning the whole possible score so the acceptance
    /// walkthrough's result is predictable regardless of which answer is
    /// picked. Never real academic content.</summary>
    private static async Task SeedTestPlacementAsync(IPlacementTestAdminService placementAdmin, long adminUserId, CancellationToken ct, ILogger logger)
    {
        var existingActive = await placementAdmin.ListVersionsAsync(ct);
        if (existingActive.Any(v => v.IsActive))
        {
            return;
        }

        var draft = await placementAdmin.CreateDraftVersionAsync($"{TestDataMarker} Placement Test — for staging acceptance testing only", adminUserId, ct);

        await placementAdmin.AddQuestionAsync(draft.TestVersionId, "[Test] 1 + 1 = ?", points: 3,
            new[] { new AddQuestionChoice("2 (correct)", true), new AddQuestionChoice("3", false) }, sortOrder: 1, ct);
        await placementAdmin.AddQuestionAsync(draft.TestVersionId, "[Test] The sky is what colour?", points: 3,
            new[] { new AddQuestionChoice("Blue (correct)", true), new AddQuestionChoice("Green", false) }, sortOrder: 2, ct);
        await placementAdmin.AddScoreRangeAsync(draft.TestVersionId, minScore: 0, maxScore: 6, levelId: A1LevelId, ct);

        var publish = await placementAdmin.PublishAsync(draft.TestVersionId, adminUserId, ct);
        if (publish.Outcome != PublishOutcome.Published)
        {
            logger.LogError("StagingSeed: test placement test failed to publish: {Errors}", string.Join("; ", publish.ValidationErrors));
            return;
        }

        await placementAdmin.ActivateAsync(draft.TestVersionId, ct);
    }

    private static async Task SeedFutureSessionsAsync(MvTeachesDbContext db, int countryId, long courseId, int ageGroupId,
        long teacherId, Instant now, ILogger logger, CancellationToken ct)
    {
        if (await db.ClassSessions.AnyAsync(s => s.TeacherId == teacherId, ct))
        {
            return;
        }

        // Seeded directly (not through ITeacherSlotPublishingService, which
        // would correctly refuse this teacher for having no connected video
        // account) — every DB-level invariant (capacity-matches-type,
        // no-overlap) still applies regardless of how the row is created.
        // This lets acceptance step 7 (booking) run without a real Zoom/
        // Google connection; a real Start/Join still correctly needs one.
        var groupStart = now.Plus(Duration.FromDays(1));
        var privateStart = now.Plus(Duration.FromDays(2));

        db.ClassSessions.Add(new ClassSession(countryId, null, courseId, A1LevelId, ageGroupId, teacherId,
            groupStart, groupStart.Plus(Duration.FromMinutes(60)), "Asia/Amman", "10:00", SessionType.Group, now));
        db.ClassSessions.Add(new ClassSession(countryId, null, courseId, A1LevelId, ageGroupId, teacherId,
            privateStart, privateStart.Plus(Duration.FromMinutes(60)), "Asia/Amman", "11:00", SessionType.Private, now));
    }
}
