using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MVTeaches.Application.Placement;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Common;
using MVTeaches.Domain.People;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Application.People;
using MVTeaches.Infrastructure.Identity;
using NodaTime;

namespace MVTeaches.Infrastructure.Persistence;

/// <summary>
/// Local-only `F5` bootstrap: verifies the connection, applies every pending
/// EF Core migration, and inserts a small set of clearly-labelled dummy
/// accounts/content so a developer can sign in as every role and exercise
/// the real application immediately — nothing here is a substitute for
/// <see cref="DataSeeder"/>'s own always-on reference data (roles, age
/// groups, countries, levels, the course), which must already have run
/// before this is called (see Program.cs's ordering).
///
/// Safety, all defence-in-depth (never trust just one of these):
/// 1. The caller (Program.cs) only invokes this inside an
///    `app.Environment.IsDevelopment()` block.
/// 2. This method independently re-checks IsDevelopment() itself.
/// 3. <see cref="LocalDevelopmentSeedOptions.Enabled"/> must be explicitly
///    true — the shipped Development default is false.
/// 4. The actually-connected database's name must exactly equal
///    <see cref="LocalDevelopmentSeedOptions.RequiredDatabaseName"/> — a
///    connection string accidentally pointed at a shared/staging/production
///    database causes this to refuse outright (migrating AND seeding),
///    never a partial, silent write.
/// Every insert is an idempotent "if it doesn't already exist" check, so
/// repeated `F5` runs never duplicate a row.
/// </summary>
public static class LocalDevelopmentSeeder
{
    // Levels seeded by DataSeeder.SeedLevelsAsync — see its own remarks for
    // why these are fixed ids, not looked up by code, in that one place.
    private const int A1LevelId = 1;

    /// <summary>Every safety gate (Development-only, Enabled, configured,
    /// exact-database-name match, connectivity) — shared by both
    /// <see cref="MigrateIfEnabledAsync"/> and <see cref="SeedAsync"/> so
    /// neither one trusts the other to have already checked. Returns the
    /// open <see cref="MvTeachesDbContext"/>/logger/options on success, or
    /// null (having already logged exactly why) when this run should do
    /// nothing further.</summary>
    private static async Task<(MvTeachesDbContext Db, ILogger Logger, LocalDevelopmentSeedOptions Options)?> CheckGatesAsync(
        IServiceProvider services, IHostEnvironment env, CancellationToken cancellationToken)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("MVTeaches.LocalDevelopmentSeed");

        if (!env.IsDevelopment())
        {
            // Never reachable via Program.cs's own outer guard, but this
            // method must never trust a single caller to be the only thing
            // standing between it and a non-Development environment.
            return null;
        }

        var options = services.GetRequiredService<IOptions<LocalDevelopmentSeedOptions>>().Value;
        if (!options.Enabled)
        {
            return null; // the ordinary, safe, do-nothing case for every other Development run
        }

        if (!options.IsConfigured)
        {
            logger.LogWarning(
                "LocalDevelopmentSeed:Enabled is true, but RequiredDatabaseName or SeedPassword is missing. " +
                "See docs/LOCAL-DEVELOPMENT.md — skipping the local bootstrap entirely.");
            return null;
        }

        var db = services.GetRequiredService<MvTeachesDbContext>();
        var actualDatabaseName = db.Database.GetDbConnection().Database;
        if (!string.Equals(actualDatabaseName, options.RequiredDatabaseName, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogCritical(
                "LocalDevelopmentSeed is enabled, but the connected database is '{Actual}', not the configured " +
                "'{Required}'. Refusing to migrate or seed anything against it. Fix ConnectionStrings:MvTeaches " +
                "or LocalDevelopmentSeed:RequiredDatabaseName in User Secrets.",
                actualDatabaseName, options.RequiredDatabaseName);
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
                "LocalDevelopmentSeed: could not reach PostgreSQL. Is PostgreSQL 16 running locally (default port " +
                "5432)? Is ConnectionStrings:MvTeaches correct in User Secrets? See docs/LOCAL-DEVELOPMENT.md. " +
                "The application will still start, but every database-backed page will fail until this is fixed.");
        }
        if (!canConnect)
        {
            logger.LogCritical(
                "LocalDevelopmentSeed: could not connect to database '{Database}'. Is PostgreSQL 16 running " +
                "locally, and does this database exist yet (see docs/LOCAL-DEVELOPMENT.md's pgAdmin step)? " +
                "Skipping migration and seeding for this run.", actualDatabaseName);
            return null;
        }

        return (db, logger, options);
    }

    /// <summary>Must run BEFORE <see cref="DataSeeder.SeedAsync"/> and
    /// BEFORE this class's own <see cref="SeedAsync"/> — both of those
    /// query tables (AspNetRoles, etc.) that only exist once migrations
    /// have actually been applied. On a genuinely fresh database, calling
    /// them first fails outright with "relation ... does not exist"; this
    /// method is what makes the schema exist at all before anything else
    /// touches it. See Program.cs's ordering.</summary>
    public static async Task MigrateIfEnabledAsync(IServiceProvider services, IHostEnvironment env, CancellationToken cancellationToken = default)
    {
        var gates = await CheckGatesAsync(services, env, cancellationToken);
        if (gates is null)
        {
            return;
        }

        var (db, logger, _) = gates.Value;
        logger.LogInformation("LocalDevelopmentSeed: applying pending migrations against '{Database}'.", db.Database.GetDbConnection().Database);
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
        var bootstrapAdminOptions = services.GetRequiredService<IOptions<BootstrapAdminOptions>>().Value;
        var levelAuthorization = services.GetRequiredService<ITeacherLevelAuthorizationService>();
        var placementAdmin = services.GetRequiredService<IPlacementTestAdminService>();
        var clock = services.GetRequiredService<IClock>();
        var now = clock.GetCurrentInstant();

        // 0. Ensure the configured bootstrap admin exists for this local
        // database and has the current local password. DataSeeder's
        // production-safe one-time bootstrap still owns production behavior;
        // this reconciliation can only run after the local Development gates
        // above have proven this is the explicitly enabled local database.
        long? adminUserId = null;
        if (bootstrapAdminOptions.IsConfigured)
        {
            var adminUser = await CreateOrReconcileUserAsync(
                userManager,
                bootstrapAdminOptions.AdminEmail!,
                bootstrapAdminOptions.AdminPassword!,
                new[] { RoleNames.Admin, RoleNames.SystemAdmin },
                logger,
                cancellationToken);
            adminUserId = adminUser.Id;
        }
        if (adminUserId is null)
        {
            logger.LogWarning(
                "LocalDevelopmentSeed: Bootstrap:AdminEmail/AdminPassword are not configured, so no admin account " +
                "exists yet — only the Teacher/Guardian/Student accounts below will be seeded this run. Configure " +
                "Bootstrap:AdminEmail/AdminPassword (see docs/LOCAL-DEVELOPMENT.md) and restart to add the admin.");
        }

        // Looks for D-41's own named course first, falling back to "whichever
        // course exists" rather than crashing outright — DataSeeder.SeedCoursesAsync's
        // own idempotency check ("skip if ANY course exists") is deliberately
        // simple for the MVP's one-course scope, so a database that already has
        // some other course row (never possible on a genuinely fresh production
        // database, but a real state this bootstrap must still tolerate
        // gracefully rather than assume away) would otherwise never get
        // "GENERAL-ENGLISH" seeded at all.
        var courseId = await db.Courses.Where(c => c.Code == "GENERAL-ENGLISH").Select(c => (long?)c.Id).FirstOrDefaultAsync(cancellationToken)
            ?? await db.Courses.Select(c => (long?)c.Id).FirstOrDefaultAsync(cancellationToken);
        const int countryId = 1; // JO — seeded by DataSeeder.SeedCountriesAsync
        const int adultsAgeGroupId = 3; // seeded by DataSeeder.SeedAgeGroupsAsync

        var teacherId = await SeedTeacherAsync(db, userManager, levelAuthorization, options.SeedPassword!, adminUserId, now, logger, cancellationToken);
        var (guardianId, childIds) = await SeedGuardianAndChildrenAsync(db, userManager, countryId, options.SeedPassword!, logger, cancellationToken);
        await SeedDirectLoginStudentAsync(db, userManager, countryId, options.SeedPassword!, logger, cancellationToken);

        if (courseId is null)
        {
            logger.LogWarning(
                "LocalDevelopmentSeed: no course exists yet (DataSeeder should have created one) — skipping " +
                "pricing plans and sample sessions this run.");
        }
        else
        {
            if (adminUserId is not null)
            {
                await SeedPricingPlansAsync(db, countryId, courseId.Value, adminUserId.Value, now, logger, cancellationToken);
                await SeedOperatingExpenseSampleAsync(db, countryId, adminUserId.Value, now, logger, cancellationToken);
            }

            if (teacherId is not null)
            {
                await SeedFutureSessionsAsync(db, countryId, courseId.Value, adultsAgeGroupId, teacherId.Value, now, logger, cancellationToken);
            }
        }

        if (adminUserId is not null)
        {
            await SeedDummyPlacementTestAsync(db, placementAdmin, adminUserId.Value, cancellationToken, logger);
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("LocalDevelopmentSeed: local development data is ready — see docs/LOCAL-DEVELOPMENT.md for the seeded account list.");
    }

    private const string LocalDomain = "@mvteaches.local";

    private static async Task<ApplicationUser?> FindKnownLocalUserAsync(UserManager<ApplicationUser> userManager, string email, CancellationToken ct)
    {
        var existing = await userManager.FindByEmailAsync(email);
        if (existing is not null)
        {
            return existing;
        }

        var normalizedEmail = userManager.NormalizeEmail(email);
        return await userManager.Users.SingleOrDefaultAsync(
            user => user.NormalizedEmail == normalizedEmail || user.Email == email,
            ct);
    }

    private static async Task<ApplicationUser> CreateOrReconcileUserAsync(
        UserManager<ApplicationUser> userManager,
        string email,
        string password,
        IReadOnlyCollection<string> roles,
        ILogger logger,
        CancellationToken ct,
        int? countryId = null)
    {
        var existing = await FindKnownLocalUserAsync(userManager, email, ct);
        if (existing is not null)
        {
            var needsProfileUpdate =
                existing.UserName != email ||
                existing.Email != email ||
                existing.NormalizedUserName != userManager.NormalizeName(email) ||
                existing.NormalizedEmail != userManager.NormalizeEmail(email) ||
                existing.EmailConfirmed == false ||
                existing.CountryId != (countryId ?? existing.CountryId);

            existing.UserName = email;
            existing.Email = email;
            existing.EmailConfirmed = true;
            if (countryId is not null)
            {
                existing.CountryId = countryId;
            }

            if (needsProfileUpdate)
            {
                var updateResult = await userManager.UpdateAsync(existing);
                ThrowIfFailed(updateResult, $"LocalDevelopmentSeed could not update '{email}'", logger, email);
            }

            if (await userManager.IsLockedOutAsync(existing))
            {
                var unlockResult = await userManager.SetLockoutEndDateAsync(existing, null);
                ThrowIfFailed(unlockResult, $"LocalDevelopmentSeed could not unlock '{email}'", logger, email);
            }

            if (existing.AccessFailedCount > 0)
            {
                var resetFailuresResult = await userManager.ResetAccessFailedCountAsync(existing);
                ThrowIfFailed(resetFailuresResult, $"LocalDevelopmentSeed could not clear failed-login count for '{email}'", logger, email);
            }

            foreach (var role in roles)
            {
                if (!await userManager.IsInRoleAsync(existing, role))
                {
                    var addRoleResult = await userManager.AddToRoleAsync(existing, role);
                    ThrowIfFailed(addRoleResult, $"LocalDevelopmentSeed could not add '{email}' to '{role}'", logger, email);
                }
            }

            if (!await userManager.CheckPasswordAsync(existing, password))
            {
                IdentityResult passwordResult;
                if (await userManager.HasPasswordAsync(existing))
                {
                    var resetToken = await userManager.GeneratePasswordResetTokenAsync(existing);
                    passwordResult = await userManager.ResetPasswordAsync(existing, resetToken, password);
                }
                else
                {
                    passwordResult = await userManager.AddPasswordAsync(existing, password);
                }
                ThrowIfFailed(passwordResult, $"LocalDevelopmentSeed could not reconcile the password for '{email}'", logger, email);
                logger.LogInformation("LocalDevelopmentSeed: reconciled the configured local password for {Email}.", email);
            }

            return existing;
        }

        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, CountryId = countryId };
        var result = await userManager.CreateAsync(user, password);
        ThrowIfFailed(result, $"LocalDevelopmentSeed could not create '{email}'", logger, email);

        foreach (var role in roles)
        {
            var addRoleResult = await userManager.AddToRoleAsync(user, role);
            ThrowIfFailed(addRoleResult, $"LocalDevelopmentSeed could not add '{email}' to '{role}'", logger, email);
        }

        return user;
    }

    private static async Task<long> CreateOrGetUserAsync(UserManager<ApplicationUser> userManager, string email, string password, string role, ILogger logger, CancellationToken ct)
    {
        var user = await CreateOrReconcileUserAsync(userManager, email, password, new[] { role }, logger, ct);
        return user.Id;
    }

    private static void ThrowIfFailed(IdentityResult result, string message, ILogger logger, string email)
    {
        if (result.Succeeded)
        {
            return;
        }

        logger.LogError("LocalDevelopmentSeed: could not reconcile {Email}: {Errors}", email,
            string.Join("; ", result.Errors.Select(e => e.Description)));
        throw new InvalidOperationException(message + " — see the log above.");
    }

    private static async Task<long?> SeedTeacherAsync(MvTeachesDbContext db, UserManager<ApplicationUser> userManager,
        ITeacherLevelAuthorizationService levelAuthorization, string password, long? adminUserId, Instant now, ILogger logger, CancellationToken ct)
    {
        var email = "local-teacher" + LocalDomain;
        var userId = await CreateOrGetUserAsync(userManager, email, password, RoleNames.Teacher, logger, ct);

        var teacher = await db.Teachers.FirstOrDefaultAsync(t => t.UserId == userId, ct);
        if (teacher is null)
        {
            teacher = new Teacher(userId, "Local Dummy Teacher", "Asia/Amman");
            db.Teachers.Add(teacher);
            await db.SaveChangesAsync(ct); // needs its own Id before the level grant below
        }

        // Reuses the real, audited grant path — never a raw insert — so this
        // seed exercises exactly the same code a real admin action would.
        if (!await levelAuthorization.IsAuthorizedForLevelAsync(teacher.Id, A1LevelId, ct))
        {
            await levelAuthorization.GrantAsync(teacher.Id, A1LevelId, adminUserId ?? userId, ct);
        }

        // Deliberately NOT given a TeacherMeetingConnection — faking Zoom/
        // Google OAuth tokens would weaken the real "not ready for online
        // sessions" production rule. This teacher stays "Not ready" until a
        // real account is connected from /Teacher/Connections; see
        // docs/LOCAL-DEVELOPMENT.md for exactly which actions that blocks.
        return teacher.Id;
    }

    private static async Task<(long GuardianId, IReadOnlyList<long> ChildIds)> SeedGuardianAndChildrenAsync(
        MvTeachesDbContext db, UserManager<ApplicationUser> userManager, int countryId, string password, ILogger logger, CancellationToken ct)
    {
        var email = "local-guardian" + LocalDomain;
        var userId = await CreateOrGetUserAsync(userManager, email, password, RoleNames.Guardian, logger, ct);

        var guardian = await db.Guardians.FirstOrDefaultAsync(g => g.UserId == userId, ct);
        if (guardian is null)
        {
            guardian = new Guardian(userId, "Local Dummy Guardian");
            db.Guardians.Add(guardian);
            await db.SaveChangesAsync(ct);
        }

        var existingChildren = await db.Guardianships
            .Where(g => g.GuardianId == guardian.Id)
            .Select(g => g.StudentId)
            .ToListAsync(ct);
        if (existingChildren.Count > 0)
        {
            return (guardian.Id, existingChildren);
        }

        // Two SEPARATE children, deliberately with no independent login (the
        // primary D-02/D-03 case) and no placement result yet — each one's
        // attempt/result/level/balance/bookings stay fully independent, per
        // rule 3's isolation requirement, which the walkthrough exercises.
        var child1 = new Student(countryId, "Local Dummy Child One", new LocalDate(2014, 3, 1));
        var child2 = new Student(countryId, "Local Dummy Child Two", new LocalDate(2016, 7, 15));
        child1.MarkVerified();
        child2.MarkVerified();
        db.Students.AddRange(child1, child2);
        await db.SaveChangesAsync(ct);

        db.Guardianships.AddRange(
            new Guardianship(guardian.Id, child1.Id, GuardianRelationship.Parent, isPrimary: true, userId),
            new Guardianship(guardian.Id, child2.Id, GuardianRelationship.Parent, isPrimary: true, userId));
        await db.SaveChangesAsync(ct);

        return (guardian.Id, new[] { child1.Id, child2.Id });
    }

    /// <summary>Deliberately left with NO StudentLevel row (verified but
    /// PendingLevel) — this is the account the walkthrough uses to
    /// demonstrate "no placement result yet ⟹ the purchase CTA, not a
    /// package list" before actually taking the dummy test.</summary>
    private static async Task SeedDirectLoginStudentAsync(MvTeachesDbContext db, UserManager<ApplicationUser> userManager,
        int countryId, string password, ILogger logger, CancellationToken ct)
    {
        var email = "local-student" + LocalDomain;
        var user = await CreateOrReconcileUserAsync(
            userManager,
            email,
            password,
            new[] { RoleNames.Student },
            logger,
            ct,
            countryId);

        if (await db.Students.AnyAsync(s => s.UserId == user.Id, ct))
        {
            return;
        }

        var student = new Student(countryId, "Local Dummy Direct Student", new LocalDate(2010, 5, 20), user.Id);
        student.MarkVerified();
        db.Students.Add(student);
        await db.SaveChangesAsync(ct);
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

    /// <summary>Owner decision 2026-08-30 rule 2: "clearly labelled dummy
    /// placement-test content" — one score range spanning the whole possible
    /// score, mapped to A1, so the local walkthrough's outcome is
    /// deterministic regardless of which answer is picked. This is
    /// technical test fixture data, never real academic content, and never
    /// exists unless LocalDevelopmentSeed:Enabled is explicitly true.</summary>
    private static async Task SeedDummyPlacementTestAsync(MvTeachesDbContext db, IPlacementTestAdminService placementAdmin,
        long adminUserId, CancellationToken ct, ILogger logger)
    {
        var existingActive = await placementAdmin.ListVersionsAsync(ct);
        if (existingActive.Any(v => v.IsActive))
        {
            return;
        }

        const string title = "[LOCAL DUMMY DATA] Placement Test — for local technical testing only";
        // Owner decision 2026-09-04: a placement test now belongs to a course.
        // The dummy test places into the centre's original course, the one all
        // pre-existing data already refers to.
        var placementCourseId = await db.Courses.Where(c => c.Code == "GENERAL-ENGLISH")
            .Select(c => c.Id).FirstAsync(ct);
        var draft = await placementAdmin.CreateDraftVersionAsync(title, placementCourseId, adminUserId, ct);

        await placementAdmin.AddQuestionAsync(draft.TestVersionId, "[Dummy] 1 + 1 = ?", points: 3,
            new[] { new AddQuestionChoice("2 (correct)", true), new AddQuestionChoice("3", false) }, sortOrder: 1, ct);
        await placementAdmin.AddQuestionAsync(draft.TestVersionId, "[Dummy] The sky is what colour?", points: 3,
            new[] { new AddQuestionChoice("Blue (correct)", true), new AddQuestionChoice("Green", false) }, sortOrder: 2, ct);

        // Spans the whole possible score [0,6] → A1, so any answer combination
        // during the walkthrough deterministically assigns A1.
        await placementAdmin.AddScoreRangeAsync(draft.TestVersionId, minScore: 0, maxScore: 6, levelId: A1LevelId, ct);

        var publish = await placementAdmin.PublishAsync(draft.TestVersionId, adminUserId, ct);
        if (publish.Outcome != PublishOutcome.Published)
        {
            logger.LogError("LocalDevelopmentSeed: dummy placement test failed to publish: {Errors}", string.Join("; ", publish.ValidationErrors));
            return;
        }

        await placementAdmin.ActivateAsync(draft.TestVersionId, ct);
    }

    private static async Task SeedOperatingExpenseSampleAsync(MvTeachesDbContext db, int countryId, long adminUserId,
        Instant now, ILogger logger, CancellationToken ct)
    {
        if (await db.OperatingExpenses.AnyAsync(ct))
        {
            return;
        }

        db.OperatingExpenses.Add(new Domain.Finance.OperatingExpense(
            countryId, "Office Rent (local dummy)", new Money(100m, "JOD"), now.InUtc().Date, "Seeded local example expense", adminUserId, now));
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
        // account) — see this method's own remarks on why that's safe here.
        // Every DB-level invariant (capacity-matches-type, no-overlap) still
        // applies regardless of how the row is created.
        var groupStart = now.Plus(Duration.FromDays(1));
        var privateStart = now.Plus(Duration.FromDays(2));

        db.ClassSessions.Add(new ClassSession(countryId, null, courseId, A1LevelId, ageGroupId, teacherId,
            groupStart, groupStart.Plus(Duration.FromMinutes(60)), "Asia/Amman", "10:00", SessionType.Group, now));
        db.ClassSessions.Add(new ClassSession(countryId, null, courseId, A1LevelId, ageGroupId, teacherId,
            privateStart, privateStart.Plus(Duration.FromMinutes(60)), "Asia/Amman", "11:00", SessionType.Private, now));
    }
}
