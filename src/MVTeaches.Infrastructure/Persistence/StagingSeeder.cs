using System.Globalization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MVTeaches.Application.Attendance;
using MVTeaches.Application.Payments;
using MVTeaches.Application.Placement;
using MVTeaches.Application.Scheduling;
using MVTeaches.Application.Subscriptions;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Common;
using MVTeaches.Domain.Payments;
using MVTeaches.Domain.People;
using MVTeaches.Domain.Placement;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Domain.Subscriptions;
using MVTeaches.Application.People;
using MVTeaches.Infrastructure.Identity;
using NodaTime;

namespace MVTeaches.Infrastructure.Persistence;

/// <summary>
/// Local Staging bootstrap: applies pending migrations and inserts a realistic,
/// clearly-labelled set of test accounts/content so a real acceptance pass —
/// or simply eyeballing every dashboard — can exercise every role against real
/// services and a real (but isolated) database. No repository fork, no
/// simulated business logic: every teacher/guardian/student/session/purchase
/// below is created either as a plain entity (the same shape DataSeeder's own
/// reference rows use) or by calling the SAME application services a real
/// admin/teacher/guardian/student action would call (IEnrollmentService,
/// ISubscriptionService, IPaymentService, IJoinAttendanceService,
/// ISessionFinalizationService, ICompensationRequestService) — so every
/// resulting balance, ledger entry, and attendance record is exactly what the
/// real feature would have produced, not a hand-faked shortcut.
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
    private const int A2LevelId = 2;
    private const int B1LevelId = 3;
    private const int B2LevelId = 4;
    private const int C1LevelId = 5;
    private const int C2LevelId = 6;
    private const int AdultsAgeGroupId = 3;
    private const string StagingDomain = "@staging.mvteaches.local";
    private const string TestDataMarker = "[STAGING TEST DATA]";

    /// <summary>Every "transfer" reported below is fictitious — recorded and
    /// confirmed through the real payment flow purely so a Confirmed payment,
    /// an active subscription, and a real entitlement ledger entry exist to
    /// look at; no money changes hands anywhere in Local Staging.</summary>
    private const string DemoTransferNote = "Staging demo data — no real transfer; recorded for acceptance testing only.";

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
        var paymentMethodConfigs = services.GetRequiredService<IPaymentMethodConfigService>();
        var subscriptions = services.GetRequiredService<ISubscriptionService>();
        var paymentService = services.GetRequiredService<IPaymentService>();
        var enrollmentService = services.GetRequiredService<IEnrollmentService>();
        var joinAttendance = services.GetRequiredService<IJoinAttendanceService>();
        var finalization = services.GetRequiredService<ISessionFinalizationService>();
        var compensationRequests = services.GetRequiredService<ICompensationRequestService>();
        var studentAdmission = services.GetRequiredService<IStudentAdmissionService>();
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
                "(real environment variables) and restart before this bootstrap can seed teachers/guardians/students, " +
                "packages, sessions, or payments that need an acting admin id.");
            return;
        }

        var courseId = await db.Courses.Where(c => c.Code == "GENERAL-ENGLISH").Select(c => (long?)c.Id).FirstOrDefaultAsync(cancellationToken)
            ?? await db.Courses.Select(c => (long?)c.Id).FirstOrDefaultAsync(cancellationToken);
        const int countryId = 1; // JO — seeded by DataSeeder.SeedCountriesAsync

        if (courseId is null)
        {
            logger.LogWarning("StagingSeed: no course exists yet (DataSeeder should have created one) — skipping the whole demo dataset this run.");
            return;
        }

        // ---- Teachers ------------------------------------------------
        var teacherSpecs = new[]
        {
            new TeacherSpec("staging-teacher" + StagingDomain, "أحمد الزعبي", new[] { A1LevelId, A2LevelId }),
            new TeacherSpec("staging-teacher2" + StagingDomain, "ليلى الحوراني", new[] { A2LevelId, B1LevelId }),
            new TeacherSpec("staging-teacher3" + StagingDomain, "عمر النابلسي", new[] { B1LevelId, B2LevelId }),
            new TeacherSpec("staging-teacher4" + StagingDomain, "رنا خصاونة", new[] { B2LevelId, C1LevelId }),
            new TeacherSpec("staging-teacher5" + StagingDomain, "سامر عبدالله", new[] { C1LevelId, C2LevelId }),
        };
        var teacherIds = new long[teacherSpecs.Length];
        for (var i = 0; i < teacherSpecs.Length; i++)
        {
            teacherIds[i] = await SeedTeacherAsync(db, userManager, levelAuthorization, teacherSpecs[i],
                options.SeedPassword!, adminUserId.Value, logger, cancellationToken);
        }

        // ---- Guardians + children --------------------------------------
        var guardianSpecs = new[]
        {
            new GuardianSpec("staging-guardian" + StagingDomain, "منى العمري", new[]
            {
                new ChildSpec("يزن العمري", new LocalDate(2014, 3, 1), A1LevelId),
                new ChildSpec("جود العمري", new LocalDate(2018, 7, 15), null),
            }),
            new GuardianSpec("staging-guardian2" + StagingDomain, "خالد فريحات", new[]
            {
                new ChildSpec("ريان فريحات", new LocalDate(2012, 5, 10), A2LevelId),
            }),
            new GuardianSpec("staging-guardian3" + StagingDomain, "سلمى دعيبس", new[]
            {
                new ChildSpec("تالا دعيبس", new LocalDate(2016, 9, 20), A1LevelId),
                new ChildSpec("زيد دعيبس", new LocalDate(2013, 2, 14), null),
            }),
            new GuardianSpec("staging-guardian4" + StagingDomain, "وائل الشوابكة", new[]
            {
                new ChildSpec("نور الشوابكة", new LocalDate(2009, 11, 30), B1LevelId),
            }),
        };

        var studentsByName = new Dictionary<string, SeededStudent>();
        foreach (var guardianSpec in guardianSpecs)
        {
            var children = await SeedGuardianAsync(db, userManager, studentAdmission, countryId, courseId.Value, guardianSpec,
                options.SeedPassword!, adminUserId.Value, now, logger, cancellationToken);
            foreach (var (name, student) in children)
            {
                studentsByName[name] = student;
            }
        }

        // ---- Direct-login students -------------------------------------
        var directStudentSpecs = new[]
        {
            // No level yet on purpose — demonstrates "no placement result yet
            // ⟹ the purchase CTA, not a package list" for a real, empty-state screen.
            new DirectStudentSpec("staging-student" + StagingDomain, "علي المصري", new LocalDate(2000, 1, 10), null),
            new DirectStudentSpec("staging-student2" + StagingDomain, "مريم صالح", new LocalDate(1998, 4, 12), A2LevelId),
            new DirectStudentSpec("staging-student3" + StagingDomain, "فراس القضاة", new LocalDate(1995, 11, 2), B2LevelId),
            new DirectStudentSpec("staging-student4" + StagingDomain, "هبة النجار", new LocalDate(1990, 9, 20), C1LevelId),
        };
        foreach (var spec in directStudentSpecs)
        {
            var student = await SeedDirectStudentAsync(db, userManager, studentAdmission, countryId, courseId.Value, spec,
                options.SeedPassword!, adminUserId.Value, now, logger, cancellationToken);
            studentsByName[spec.FullName] = student;
        }

        // ---- Payment methods --------------------------------------------
        var cliqId = await EnsurePaymentMethodAsync(db, paymentMethodConfigs, PaymentMethod.CliQ,
            $"{TestDataMarker} Beneficiary", "staging-cliq-alias", null, null, null, "Jordan",
            $"{TestDataMarker} — do not send real money to this alias.", new[] { "JOD" }, adminUserId.Value, cancellationToken);
        await EnsurePaymentMethodAsync(db, paymentMethodConfigs, PaymentMethod.BankTransfer,
            $"{TestDataMarker} Beneficiary", null, "JO00STAGE0000000000000000", "MVTeaches Staging Bank", "STAGEJOXX", "Jordan",
            $"{TestDataMarker} — a local bank transfer option, for testing only.", new[] { "JOD" }, adminUserId.Value, cancellationToken);

        // ---- Pricing plans: every level, Group + Private -----------------
        var levelIds = new[] { A1LevelId, A2LevelId, B1LevelId, B2LevelId, C1LevelId, C2LevelId };
        var planIds = new Dictionary<(int LevelId, SessionType Type), long>();
        var today = now.InUtc().Date;
        foreach (var levelId in levelIds)
        {
            planIds[(levelId, SessionType.Group)] = await EnsurePricingPlanAsync(db, subscriptions, countryId,
                courseId.Value, levelId, SessionType.Group, sessionsCount: 10, minutesTotal: 600,
                new Money(50m, "JOD"), validityDays: 90, today, adminUserId.Value, cancellationToken);
            planIds[(levelId, SessionType.Private)] = await EnsurePricingPlanAsync(db, subscriptions, countryId,
                courseId.Value, levelId, SessionType.Private, sessionsCount: 5, minutesTotal: 300,
                new Money(120m, "JOD"), validityDays: 90, today, adminUserId.Value, cancellationToken);
        }

        // ---- Purchases: active packages, a self-paid one, and one still
        // awaiting admin confirmation ---------------------------------------
        var yazan = studentsByName["يزن العمري"];
        var tala = studentsByName["تالا دعيبس"];
        var rayan = studentsByName["ريان فريحات"];
        var noor = studentsByName["نور الشوابكة"];
        var maryam = studentsByName["مريم صالح"];
        var firas = studentsByName["فراس القضاة"];
        var heba = studentsByName["هبة النجار"];

        await PurchaseAndPayAsync(db, subscriptions, paymentService, yazan.StudentId, yazan.ActingUserId,
            planIds[(A1LevelId, SessionType.Group)], SubscriptionOrigin.GuardianPurchase, cliqId, adminUserId.Value,
            confirmPayment: true, "منى العمري", today, logger, cancellationToken);
        await PurchaseAndPayAsync(db, subscriptions, paymentService, tala.StudentId, tala.ActingUserId,
            planIds[(A1LevelId, SessionType.Group)], SubscriptionOrigin.GuardianPurchase, cliqId, adminUserId.Value,
            confirmPayment: true, "سلمى دعيبس", today, logger, cancellationToken);
        await PurchaseAndPayAsync(db, subscriptions, paymentService, rayan.StudentId, rayan.ActingUserId,
            planIds[(A2LevelId, SessionType.Group)], SubscriptionOrigin.GuardianPurchase, cliqId, adminUserId.Value,
            confirmPayment: true, "خالد فريحات", today, logger, cancellationToken);
        await PurchaseAndPayAsync(db, subscriptions, paymentService, noor.StudentId, noor.ActingUserId,
            planIds[(B1LevelId, SessionType.Group)], SubscriptionOrigin.GuardianPurchase, cliqId, adminUserId.Value,
            confirmPayment: true, "وائل الشوابكة", today, logger, cancellationToken);
        await PurchaseAndPayAsync(db, subscriptions, paymentService, maryam.StudentId, maryam.ActingUserId,
            planIds[(A2LevelId, SessionType.Group)], SubscriptionOrigin.SelfPurchase, cliqId, adminUserId.Value,
            confirmPayment: true, "مريم صالح", today, logger, cancellationToken);
        await PurchaseAndPayAsync(db, subscriptions, paymentService, firas.StudentId, firas.ActingUserId,
            planIds[(B2LevelId, SessionType.Private)], SubscriptionOrigin.SelfPurchase, cliqId, adminUserId.Value,
            confirmPayment: true, "فراس القضاة", today, logger, cancellationToken);

        // هبة: an older package that has since expired (needs renewal) ...
        var hebaExpiredSubId = await PurchaseAndPayAsync(db, subscriptions, paymentService, heba.StudentId, heba.ActingUserId,
            planIds[(C1LevelId, SessionType.Private)], SubscriptionOrigin.SelfPurchase, cliqId, adminUserId.Value,
            confirmPayment: true, "هبة النجار", today, logger, cancellationToken);
        await MarkSubscriptionExpiredIfActiveAsync(db, hebaExpiredSubId, cancellationToken);

        // ... and a fresh renewal she has reported paying for but that no
        // admin has confirmed yet — a real "needs admin review" item.
        await PurchaseAndPayAsync(db, subscriptions, paymentService, heba.StudentId, heba.ActingUserId,
            planIds[(C1LevelId, SessionType.Group)], SubscriptionOrigin.SelfPurchase, cliqId, adminUserId.Value,
            confirmPayment: false, "هبة النجار", today, logger, cancellationToken);

        // ---- Future sessions (scheduling) --------------------------------
        var futureSessions = new[]
        {
            new SessionSpec(teacherIds[0], A1LevelId, SessionType.Group, SessionStart(now, 1, "10:00"), "10:00", new[] { yazan, tala }),
            new SessionSpec(teacherIds[0], A2LevelId, SessionType.Private, SessionStart(now, 2, "11:00"), "11:00", Array.Empty<SeededStudent>()),
            new SessionSpec(teacherIds[1], A2LevelId, SessionType.Group, SessionStart(now, 1, "12:00"), "12:00", new[] { maryam, rayan }),
            new SessionSpec(teacherIds[1], B1LevelId, SessionType.Group, SessionStart(now, 2, "13:00"), "13:00", new[] { noor }),
            new SessionSpec(teacherIds[2], B2LevelId, SessionType.Private, SessionStart(now, 1, "14:00"), "14:00", new[] { firas }),
            new SessionSpec(teacherIds[2], B1LevelId, SessionType.Group, SessionStart(now, 3, "15:00"), "15:00", Array.Empty<SeededStudent>()),
            new SessionSpec(teacherIds[3], C1LevelId, SessionType.Group, SessionStart(now, 2, "16:00"), "16:00", Array.Empty<SeededStudent>()),
            new SessionSpec(teacherIds[4], C2LevelId, SessionType.Private, SessionStart(now, 3, "17:00"), "17:00", Array.Empty<SeededStudent>()),
        };
        foreach (var spec in futureSessions)
        {
            await SeedSessionAsync(db, enrollmentService, countryId, courseId.Value, spec, now, logger, cancellationToken);
        }

        // ---- Past sessions (scheduling + attendance history) ---------------
        var firasNoShowStart = SessionStart(now, -2, "14:00");
        var pastSessions = new[]
        {
            new SessionSpec(teacherIds[0], A1LevelId, SessionType.Group, SessionStart(now, -5, "10:00"), "10:00", new[] { yazan }),
            new SessionSpec(teacherIds[0], A1LevelId, SessionType.Group, SessionStart(now, -2, "10:00"), "10:00", new[] { yazan, tala }),
            new SessionSpec(teacherIds[1], A2LevelId, SessionType.Group, SessionStart(now, -3, "12:00"), "12:00", new[] { maryam, rayan }),
            new SessionSpec(teacherIds[2], B2LevelId, SessionType.Private, SessionStart(now, -4, "14:00"), "14:00", new[] { firas }),
            new SessionSpec(teacherIds[2], B2LevelId, SessionType.Private, firasNoShowStart, "14:00", new[] { firas }),
        };
        // Every past student except رياان (no-show, no request) and the
        // second فراس session (no-show, becomes a compensation request below)
        // presses Join for real — the same call the student's own "Join" button makes.
        var presentByStartOffset = new HashSet<(long TeacherId, long StudentId)>
        {
            (teacherIds[0], yazan.StudentId),
            (teacherIds[1], maryam.StudentId),
        };
        long? firasNoShowSessionId = null;
        foreach (var spec in pastSessions)
        {
            var sessionId = await SeedSessionAsync(db, enrollmentService, countryId, courseId.Value, spec, now, logger, cancellationToken);
            if (sessionId is null)
            {
                continue;
            }

            foreach (var student in spec.Students)
            {
                var isFirasSecondPrivateSession = spec.TeacherId == teacherIds[2] && spec.SessionType == SessionType.Private
                    && student.StudentId == firas.StudentId && spec.StartsAtUtc == firasNoShowStart;
                var isRayanGroupSession = student.StudentId == rayan.StudentId;

                if (isFirasSecondPrivateSession)
                {
                    firasNoShowSessionId = sessionId; // left un-joined on purpose — see below
                    continue;
                }
                if (isRayanGroupSession)
                {
                    continue; // left un-joined on purpose — a plain, unrequested no-show
                }

                await joinAttendance.JoinAsync(new JoinAttendanceRequest(sessionId.Value, student.StudentId, student.ActingUserId), cancellationToken);
            }
        }

        // The frequent Hangfire sweep (see Program.cs) would do this on its
        // own within minutes of each session ending; running it once here
        // immediately gives every past session its real Completed status and
        // every un-joined enrollment its real no-show attendance record and
        // ledger entry — the exact outcome the sweep itself would produce.
        await finalization.FinalizeEndedSessionsAsync(cancellationToken);

        if (firasNoShowSessionId is not null)
        {
            var requestResult = await compensationRequests.RequestReplacementAsync(firas.StudentId, firasNoShowSessionId.Value,
                "لم أتمكن من حضور الحصة بسبب ظرف طارئ. [STAGING TEST DATA]", firas.ActingUserId, cancellationToken);
            if (requestResult.Outcome != SubmitCompensationRequestOutcome.Submitted
                && requestResult.Outcome != SubmitCompensationRequestOutcome.DuplicateRequest)
            {
                logger.LogWarning("StagingSeed: could not submit the demo compensation request: {Outcome}", requestResult.Outcome);
            }
        }

        await SeedTestPlacementAsync(placementAdmin, courseId.Value, adminUserId.Value, cancellationToken, logger);

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("StagingSeed: Local Staging test data is ready — see docs/LOCAL-STAGING.md for the seeded account list.");
    }

    private sealed record TeacherSpec(string Email, string FullName, int[] LevelIds);
    private sealed record ChildSpec(string FullName, LocalDate DateOfBirth, int? LevelId);
    private sealed record GuardianSpec(string Email, string FullName, ChildSpec[] Children);
    private sealed record DirectStudentSpec(string Email, string FullName, LocalDate DateOfBirth, int? LevelId);

    /// <summary><paramref name="ActingUserId"/> is who a purchase/booking/Join
    /// call should be attributed to: the student's own login for a
    /// direct-login student, or the primary guardian's login for a
    /// guardian-only child with no independent login of their own.</summary>
    private sealed record SeededStudent(long StudentId, long ActingUserId, int? LevelId);

    private sealed record SessionSpec(long TeacherId, int LevelId, SessionType SessionType, Instant StartsAtUtc,
        string LocalStartText, IReadOnlyList<SeededStudent> Students);

    /// <summary>The zone every seeded session is scheduled in — the same one
    /// stored on the session itself, so its wall-clock time means what the
    /// label says.</summary>
    private static readonly DateTimeZone StagingSessionZone = DateTimeZoneProviders.Tzdb["Asia/Amman"];

    /// <summary>
    /// A session's start, anchored to a calendar day at a fixed local time
    /// rather than "now plus N days".
    ///
    /// The earlier form carried the current time-of-day into every start, so
    /// no two runs ever produced the same instant: the idempotency check on
    /// (teacher, start) never matched, every startup tried to insert a fresh
    /// set of sessions, and PostgreSQL rejected each one that landed on top of
    /// an existing booking for that teacher — over a hundred
    /// `no_teacher_overlap` violations logged per start, with the demo data
    /// drifting a little further each time. Anchoring the time makes a repeat
    /// run on the same day a genuine no-op, and makes the stored time agree
    /// with the label beside it (10:00 really is 10:00 in Amman).
    /// </summary>
    private static Instant SessionStart(Instant now, int dayOffset, string localStartText)
    {
        var parts = localStartText.Split(':');
        var time = new LocalTime(int.Parse(parts[0], CultureInfo.InvariantCulture),
            int.Parse(parts[1], CultureInfo.InvariantCulture));
        var day = now.InZone(StagingSessionZone).Date.PlusDays(dayOffset);
        return day.At(time).InZoneLeniently(StagingSessionZone).ToInstant();
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

    private static async Task<long> SeedTeacherAsync(MvTeachesDbContext db, UserManager<ApplicationUser> userManager,
        ITeacherLevelAuthorizationService levelAuthorization, TeacherSpec spec, string password, long adminUserId,
        ILogger logger, CancellationToken ct)
    {
        var user = await CreateOrReconcileUserAsync(userManager, spec.Email, password, new[] { RoleNames.Teacher }, logger, ct);

        var teacher = await db.Teachers.FirstOrDefaultAsync(t => t.UserId == user.Id, ct);
        if (teacher is null)
        {
            teacher = new Teacher(user.Id, spec.FullName, "Asia/Amman");
            db.Teachers.Add(teacher);
            await db.SaveChangesAsync(ct);
        }

        foreach (var levelId in spec.LevelIds)
        {
            if (!await levelAuthorization.IsAuthorizedForLevelAsync(teacher.Id, levelId, ct))
            {
                await levelAuthorization.GrantAsync(teacher.Id, levelId, adminUserId, ct);
            }
        }

        // Deliberately NOT given a TeacherMeetingConnection — faking Zoom/
        // Google OAuth tokens would weaken the real "not ready for online
        // sessions" rule. See docs/LOCAL-STAGING.md for exactly which
        // actions that blocks (Start a session, publish new slots).
        return teacher.Id;
    }

    private static async Task<IReadOnlyDictionary<string, SeededStudent>> SeedGuardianAsync(
        MvTeachesDbContext db, UserManager<ApplicationUser> userManager, IStudentAdmissionService studentAdmission,
        int countryId, long courseId, GuardianSpec spec, string password, long adminUserId, Instant now, ILogger logger,
        CancellationToken ct)
    {
        var user = await CreateOrReconcileUserAsync(userManager, spec.Email, password, new[] { RoleNames.Guardian }, logger, ct);

        var guardian = await db.Guardians.FirstOrDefaultAsync(g => g.UserId == user.Id, ct);
        if (guardian is null)
        {
            guardian = new Guardian(user.Id, spec.FullName);
            db.Guardians.Add(guardian);
            await db.SaveChangesAsync(ct);
        }

        var result = new Dictionary<string, SeededStudent>();
        foreach (var child in spec.Children)
        {
            var existingLink = await db.Guardianships
                .Where(g => g.GuardianId == guardian.Id)
                .Join(db.Students, g => g.StudentId, s => s.Id, (g, s) => new { g.StudentId, s.FullName })
                .FirstOrDefaultAsync(x => x.FullName == child.FullName, ct);

            long studentId;
            if (existingLink is not null)
            {
                studentId = existingLink.StudentId;
            }
            else
            {
                var student = new Student(countryId, child.FullName, child.DateOfBirth);
                student.MarkVerified();
                db.Students.Add(student);
                await db.SaveChangesAsync(ct);

                db.Guardianships.Add(new Guardianship(guardian.Id, student.Id, GuardianRelationship.Parent, isPrimary: true, user.Id));
                await db.SaveChangesAsync(ct);
                studentId = student.Id;
            }

            if (child.LevelId is not null)
            {
                await EnsureStudentLevelAsync(db, studentAdmission, studentId, courseId, child.LevelId.Value, adminUserId, ct);
            }

            result[child.FullName] = new SeededStudent(studentId, user.Id, child.LevelId);
        }

        return result;
    }

    private static async Task<SeededStudent> SeedDirectStudentAsync(MvTeachesDbContext db, UserManager<ApplicationUser> userManager,
        IStudentAdmissionService studentAdmission, int countryId, long courseId, DirectStudentSpec spec, string password,
        long adminUserId, Instant now, ILogger logger, CancellationToken ct)
    {
        var user = await CreateOrReconcileUserAsync(userManager, spec.Email, password, new[] { RoleNames.Student }, logger, ct, countryId);

        var existing = await db.Students.FirstOrDefaultAsync(s => s.UserId == user.Id, ct);
        long studentId;
        if (existing is not null)
        {
            studentId = existing.Id;
        }
        else
        {
            var student = new Student(countryId, spec.FullName, spec.DateOfBirth, user.Id);
            student.MarkVerified();
            db.Students.Add(student);
            await db.SaveChangesAsync(ct);
            studentId = student.Id;
        }

        if (spec.LevelId is not null)
        {
            await EnsureStudentLevelAsync(db, studentAdmission, studentId, courseId, spec.LevelId.Value, adminUserId, ct);
        }

        return new SeededStudent(studentId, user.Id, spec.LevelId);
    }

    /// <summary>Delegates to <see cref="IStudentAdmissionService.AssignLevelAsync"/> —
    /// the same admin action the Admin/Students page itself calls (an explicit,
    /// reasoned AdminOverride) — rather than inserting a StudentLevel row by
    /// hand, specifically because that service is also what advances
    /// Student.Status PendingLevel → Active (§8.1); a hand-rolled insert would
    /// leave the student's own status stuck at PendingLevel despite having a
    /// current level, which is exactly the kind of inconsistent state a real
    /// admin action never produces.</summary>
    private static async Task EnsureStudentLevelAsync(MvTeachesDbContext db, IStudentAdmissionService studentAdmission,
        long studentId, long courseId, int levelId, long adminUserId, CancellationToken ct)
    {
        // Owner decision 2026-09-04 (multi-course levels): scoped to the course
        // this seeded student is actually being placed in, so re-running the
        // seeder for a second course does not look like an existing placement.
        var hasCurrent = await db.StudentLevels
            .AnyAsync(l => l.StudentId == studentId && l.CourseId == courseId && l.IsCurrent, ct);
        if (hasCurrent)
        {
            return;
        }

        await studentAdmission.AssignLevelAsync(studentId, courseId, levelId, adminUserId,
            $"{TestDataMarker} level assigned directly for acceptance testing.", ct);
    }

    private static async Task<long> EnsurePaymentMethodAsync(MvTeachesDbContext db, IPaymentMethodConfigService paymentMethods,
        PaymentMethod type, string beneficiaryName, string? cliqAlias, string? iban, string? bankName, string? swiftBic,
        string? countryName, string? instructions, IReadOnlyList<string> acceptedCurrencies, long adminUserId, CancellationToken ct)
    {
        var existing = await paymentMethods.ListAllAsync(ct);
        var match = existing.FirstOrDefault(m => m.Type == type);
        if (match is not null)
        {
            return match.Id;
        }

        var created = await paymentMethods.CreateAsync(type, beneficiaryName, cliqAlias, iban, bankName, swiftBic,
            countryName, instructions, acceptedCurrencies, adminUserId, ct);
        return created.Id;
    }

    private static async Task<long> EnsurePricingPlanAsync(MvTeachesDbContext db, ISubscriptionService subscriptions,
        int countryId, long courseId, int levelId, SessionType sessionType, int sessionsCount, int minutesTotal,
        Money amount, int validityDays, LocalDate effectiveFrom, long adminUserId, CancellationToken ct)
    {
        var existing = await db.PricingPlans.FirstOrDefaultAsync(
            p => p.CourseId == courseId && p.LevelId == levelId && p.SessionType == sessionType, ct);
        if (existing is not null)
        {
            return existing.Id;
        }

        var result = await subscriptions.CreatePricingPlanAsync(countryId, courseId, levelId, ageGroupId: null,
            sessionType, sessionsCount, minutesTotal, amount, validityDays, effectiveFrom, adminUserId, ct);
        return result.PricingPlanId;
    }

    /// <summary>The full real self-service purchase journey — request a
    /// package, report a transfer, and (unless <paramref name="confirmPayment"/>
    /// is false, for the one demo case that should stay "awaiting admin
    /// review") have an admin confirm it — never a hand-crafted shortcut.
    /// Idempotent on (StudentId, LevelId, SessionType): re-running this
    /// bootstrap never creates a second subscription for the same one.</summary>
    private static async Task<long> PurchaseAndPayAsync(MvTeachesDbContext db, ISubscriptionService subscriptions,
        IPaymentService paymentService, long studentId, long actingUserId, long planId, SubscriptionOrigin origin,
        long paymentMethodConfigId, long adminUserId, bool confirmPayment, string payerDisplayName, LocalDate transferDate,
        ILogger logger, CancellationToken ct)
    {
        // A fresh identity map for every purchase. Without this, re-reading
        // the SAME PricingPlan for a second student's purchase (every level
        // has exactly one Group and one Private plan, shared by design)
        // hands EF back the exact same tracked "Amount#Money" owned-entity
        // instance it gave the FIRST purchase — which that first Subscription
        // has already claimed as its own "Price#Money". EF then tries to
        // re-parent that single shared instance onto the second Subscription,
        // which fails hard ("part of a key and cannot be modified") because
        // an owned entity's key includes its owner's id. A single, short-lived
        // request-scoped DbContext (the real, normal way every one of these
        // services is actually called) never buffers two purchases together
        // long enough to hit this; this long-lived seeding run does, purely
        // because it is not itself a normal request. Clearing here is a
        // seeder-only workaround — it changes nothing about how a purchase
        // behaves, only forces this script to re-read each entity fresh.
        db.ChangeTracker.Clear();

        var plan = await db.PricingPlans.FirstAsync(p => p.Id == planId, ct);
        var existing = await db.Subscriptions.FirstOrDefaultAsync(
            s => s.StudentId == studentId && s.LevelId == plan.LevelId && s.SessionType == plan.SessionType, ct);
        if (existing is not null)
        {
            return existing.Id;
        }

        var purchase = await subscriptions.PurchaseFromPlanAsync(studentId, planId, actingUserId, origin, isAdminInitiated: false, ct);
        if (purchase.Outcome != PurchaseFromPlanOutcome.Purchased || purchase.SubscriptionId is null)
        {
            logger.LogError("StagingSeed: demo purchase failed for student {StudentId}, plan {PlanId}: {Outcome}",
                studentId, planId, purchase.Outcome);
            throw new InvalidOperationException($"StagingSeed could not purchase plan {planId} for student {studentId}: {purchase.Outcome}");
        }

        var request = await paymentService.RequestOwnPaymentAsync(studentId, purchase.SubscriptionId.Value, paymentMethodConfigId, actingUserId, ct);
        if (request.Outcome != RequestOwnPaymentOutcome.Requested || request.PaymentId is null)
        {
            logger.LogError("StagingSeed: demo payment request failed for subscription {SubscriptionId}: {Outcome}",
                purchase.SubscriptionId, request.Outcome);
            throw new InvalidOperationException($"StagingSeed could not request payment for subscription {purchase.SubscriptionId}: {request.Outcome}");
        }

        await paymentService.AttachTransferDetailsAsync(request.PaymentId.Value, actingUserId, isAdminInitiated: false,
            payerDisplayName, transferDate, $"STG-{request.PaymentId.Value:D6}", receiptFileId: null, ct);

        if (confirmPayment)
        {
            await paymentService.ConfirmAsync(request.PaymentId.Value, adminUserId, ct);
        }

        return purchase.SubscriptionId.Value;
    }

    private static async Task MarkSubscriptionExpiredIfActiveAsync(MvTeachesDbContext db, long subscriptionId, CancellationToken ct)
    {
        var subscription = await db.Subscriptions.FirstOrDefaultAsync(s => s.Id == subscriptionId, ct);
        if (subscription is null || subscription.Status != SubscriptionStatus.Active)
        {
            return; // already expired by an earlier run, or never activated — nothing to do
        }

        // Local Staging cannot fast-forward wall-clock time to let the real
        // nightly expiry sweep (§19.3) reach this subscription naturally —
        // this calls the exact same public domain method that sweep calls,
        // to represent its outcome for a demo "needs renewal" package.
        subscription.MarkExpired();
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Direct entity construction — same reasoning as the original
    /// seed's own remark: going through ITeacherSlotPublishingService would
    /// correctly refuse every one of these teachers for having no connected
    /// video account. Every DB-level invariant (capacity-matches-type,
    /// no-overlap) still applies regardless of how the row is created.
    /// Returns null (and logs) instead of throwing if the no-overlap
    /// constraint rejects a slot — acceptable for a demo dataset, never for
    /// real scheduling.</summary>
    private static async Task<long?> SeedSessionAsync(MvTeachesDbContext db, IEnrollmentService enrollmentService,
        int countryId, long courseId, SessionSpec spec, Instant now, ILogger logger, CancellationToken ct)
    {
        var existing = await db.ClassSessions.FirstOrDefaultAsync(
            s => s.TeacherId == spec.TeacherId && s.StartsAtUtc == spec.StartsAtUtc, ct);
        long sessionId;
        if (existing is not null)
        {
            sessionId = existing.Id;
        }
        else
        {
            var session = new ClassSession(countryId, null, courseId, spec.LevelId, AdultsAgeGroupId, spec.TeacherId,
                spec.StartsAtUtc, spec.StartsAtUtc.Plus(Duration.FromMinutes(60)), "Asia/Amman", spec.LocalStartText,
                spec.SessionType, now);
            db.ClassSessions.Add(session);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                logger.LogWarning(ex, "StagingSeed: could not create a demo session for teacher {TeacherId} at {StartsAtUtc} — skipping it.",
                    spec.TeacherId, spec.StartsAtUtc);
                db.ChangeTracker.Clear();
                return null;
            }
            sessionId = session.Id;
        }

        foreach (var student in spec.Students)
        {
            await enrollmentService.EnrollInSessionAsync(sessionId, student.StudentId, student.ActingUserId, ct);
        }

        return sessionId;
    }

    /// <summary>Same deterministic-outcome shape as LocalDevelopmentSeeder's
    /// own dummy test: trivial, clearly-marked placeholder content, one
    /// score range spanning the whole possible score so the acceptance
    /// walkthrough's result is predictable regardless of which answer is
    /// picked. Never real academic content.</summary>
    private static async Task SeedTestPlacementAsync(IPlacementTestAdminService placementAdmin, long courseId, long adminUserId,
        CancellationToken ct, ILogger logger)
    {
        var existingActive = await placementAdmin.ListVersionsAsync(ct);
        if (existingActive.Any(v => v.IsActive))
        {
            return;
        }

        var draft = await placementAdmin.CreateDraftVersionAsync(
            $"{TestDataMarker} Placement Test — for staging acceptance testing only", courseId, adminUserId, ct);

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
}
