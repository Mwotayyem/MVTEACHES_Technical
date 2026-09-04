using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MVTeaches.Application.People;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.People;
using MVTeaches.Domain.Placement;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.People;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace MVTeaches.Tests.People;

/// <summary>
/// §7/§8/§10 — the admin-driven onboarding path, standing in for the
/// documented phone+OTP self-registration flow while WhatsApp remains
/// unconfigured (see docs/deployment/STATUS.md). Exercises the real
/// ux_guardianship_primary partial unique index against PostgreSQL, not a
/// mock — a second primary guardian for the same student must be physically
/// rejected by the database, not merely by application logic.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class StudentAdmissionServiceTests
{
    private readonly TestDatabaseFixture _fixture;
    private static long _idSeed = 29_000_000; // a range distinct from every other test class sharing this DB

    public StudentAdmissionServiceTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static long NextId() => Interlocked.Increment(ref _idSeed);

    private static string TwoLetterCode(long seed)
    {
        var n = (int)(seed % 676);
        return string.Concat((char)('A' + n / 26), (char)('A' + n % 26));
    }

    private static async Task<int> SeedCountryAsync(MvTeachesDbContext db)
    {
        var countryId = (int)NextId();
        db.Countries.Add(new Country(countryId, TwoLetterCode(countryId), "دولة", "Country", "JOD", "+962", "Asia/Amman"));
        await db.SaveChangesAsync();
        return countryId;
    }

    /// <summary>Owner decision 2026-09-04: a level belongs to a course, so
    /// assigning one needs a course to assign it in.</summary>
    private static async Task<long> SeedCourseAsync(MvTeachesDbContext db)
    {
        var course = new MVTeaches.Domain.Catalog.Course("C" + Guid.NewGuid().ToString("N")[..8], "دورة", "Course");
        db.Courses.Add(course);
        await db.SaveChangesAsync();
        return course.Id;
    }

    private static async Task<int> SeedLevelAsync(MvTeachesDbContext db)
    {
        var levelId = (int)NextId();
        db.Levels.Add(new Level(levelId, "L" + levelId, "مستوى", "Level", levelId));
        await db.SaveChangesAsync();
        return levelId;
    }

    /// <summary>Roles are normally seeded once at app startup by DataSeeder,
    /// which the test host never runs — ensure they exist idempotently here,
    /// exactly the way DataSeeder itself does it.</summary>
    private static async Task EnsureRolesExistAsync(RoleManagerType roleManager)
    {
        foreach (var role in RoleNames.All)
        {
            if (!await roleManager.Manager.RoleExistsAsync(role))
            {
                await roleManager.Manager.CreateAsync(new ApplicationRole(role));
            }
        }
    }

    // Thin wrapper so the helper above doesn't need the fully-qualified generic
    // RoleManager<ApplicationRole> type spelled out at every call site.
    private sealed class RoleManagerType
    {
        public required Microsoft.AspNetCore.Identity.RoleManager<ApplicationRole> Manager { get; init; }
    }

    private static (MvTeachesDbContext Db, IStudentAdmissionService Service, RoleManagerType Roles) CreateService(TestDatabaseFixture fixture)
    {
        var db = fixture.CreateContext();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(db);
        services.AddIdentityCore<ApplicationUser>(options => options.Password.RequiredLength = 10)
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<MvTeachesDbContext>();
        var provider = services.BuildServiceProvider();

        var userManager = provider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>>();
        var roleManager = provider.GetRequiredService<Microsoft.AspNetCore.Identity.RoleManager<ApplicationRole>>();
        var clock = new FakeClock(SystemClock.Instance.GetCurrentInstant());
        var service = new StudentAdmissionService(db, userManager, clock);
        return (db, service, new RoleManagerType { Manager = roleManager });
    }

    [Fact]
    public async Task Registering_a_guardian_creates_a_real_login_and_role_membership()
    {
        var (db, service, roles) = CreateService(_fixture);
        await using var _ = db;
        await EnsureRolesExistAsync(roles);

        var email = $"guardian-{Guid.NewGuid():N}@test.mvteaches.local";
        var result = await service.RegisterGuardianAsync(email, "CorrectHorse123!", "Guardian Name",
            "+962790000001", CancellationToken.None);

        Assert.Equal(RegisterGuardianOutcome.Registered, result.Outcome);
        Assert.NotNull(result.GuardianId);

        var guardian = await db.Guardians.FirstAsync(g => g.Id == result.GuardianId);
        Assert.Equal("Guardian Name", guardian.FullName);

        var user = await db.Users.FirstAsync(u => u.Id == guardian.UserId);
        Assert.Equal(email, user.Email);
        // Owner decision 2026-09-04 (phone capture): stored on Identity's own
        // existing column, which is what made this possible without a migration.
        Assert.Equal("+962790000001", user.PhoneNumber);
        Assert.True(await roles.Manager.RoleExistsAsync(RoleNames.Guardian));
    }

    [Fact]
    public async Task Registering_a_student_with_no_login_leaves_UserId_null()
    {
        var (db, service, _) = CreateService(_fixture);
        await using var _ = db;
        var countryId = await SeedCountryAsync(db);

        var result = await service.RegisterStudentAsync(countryId, "Child Student", new LocalDate(2016, 1, 1),
            loginEmail: null, loginPassword: null, phoneNumber: null, CancellationToken.None);

        Assert.Equal(RegisterStudentOutcome.Registered, result.Outcome);
        var student = await db.Students.FirstAsync(s => s.Id == result.StudentId);
        Assert.Null(student.UserId);
        Assert.Equal(StudentStatus.PendingVerification, student.Status);
    }

    [Fact]
    public async Task Registering_an_adult_student_with_a_login_links_it()
    {
        var (db, service, roles) = CreateService(_fixture);
        await using var _ = db;
        await EnsureRolesExistAsync(roles);
        var countryId = await SeedCountryAsync(db);

        var email = $"student-{Guid.NewGuid():N}@test.mvteaches.local";
        var result = await service.RegisterStudentAsync(countryId, "Adult Student", new LocalDate(1995, 1, 1),
            email, "CorrectHorse123!", "+962790000002", CancellationToken.None);

        Assert.Equal(RegisterStudentOutcome.Registered, result.Outcome);
        var student = await db.Students.FirstAsync(s => s.Id == result.StudentId);
        Assert.NotNull(student.UserId);
        // The independent-learner case: their own login carries their own number.
        var user = await db.Users.FirstAsync(u => u.Id == student.UserId);
        Assert.Equal("+962790000002", user.PhoneNumber);
        Assert.True(await roles.Manager.RoleExistsAsync(RoleNames.Student));
    }

    [Fact]
    public async Task Linking_the_same_guardian_and_student_pair_twice_is_a_safe_no_op()
    {
        var (db, service, _) = CreateService(_fixture);
        await using var _ = db;
        var countryId = await SeedCountryAsync(db);
        var student = new Student(countryId, "Student", new LocalDate(2015, 1, 1));
        db.Students.Add(student);
        var guardianUser = new ApplicationUser { UserName = $"gu-{Guid.NewGuid():N}", Email = $"{Guid.NewGuid():N}@test.mvteaches.local" };
        db.Users.Add(guardianUser);
        await db.SaveChangesAsync();
        var guardian = new Guardian(guardianUser.Id, "Guardian");
        db.Guardians.Add(guardian);
        await db.SaveChangesAsync();

        var first = await service.LinkGuardianAsync(guardian.Id, student.Id, GuardianRelationship.Parent, isPrimary: true, linkedByUserId: 0, CancellationToken.None);
        Assert.Equal(LinkGuardianOutcome.Linked, first.Outcome);

        var second = await service.LinkGuardianAsync(guardian.Id, student.Id, GuardianRelationship.Parent, isPrimary: true, linkedByUserId: 0, CancellationToken.None);
        Assert.Equal(LinkGuardianOutcome.AlreadyLinked, second.Outcome);

        Assert.Equal(1, await db.Guardianships.CountAsync(g => g.GuardianId == guardian.Id && g.StudentId == student.Id));
    }

    /// <summary>Owner decision 2026-09-04: one responsible guardian per student
    /// in the MVP. This test used to assert PrimaryConflict — the database's
    /// ux_guardianship_primary index catching a second PRIMARY guardian. That
    /// index is still there and still enforced; it simply no longer gets a turn,
    /// because the service now refuses ANY second guardian (primary or not)
    /// before attempting the insert. Both halves are asserted below: the new
    /// outcome, and that a non-primary second guardian — which the old rule
    /// would have happily accepted — is refused as well.</summary>
    [Fact]
    public async Task A_second_guardian_for_the_same_student_is_rejected()
    {
        var (db, service, _) = CreateService(_fixture);
        await using var _ = db;
        var countryId = await SeedCountryAsync(db);
        var student = new Student(countryId, "Student", new LocalDate(2015, 1, 1));
        db.Students.Add(student);
        var user1 = new ApplicationUser { UserName = $"g1-{Guid.NewGuid():N}", Email = $"{Guid.NewGuid():N}@test.mvteaches.local" };
        var user2 = new ApplicationUser { UserName = $"g2-{Guid.NewGuid():N}", Email = $"{Guid.NewGuid():N}@test.mvteaches.local" };
        db.Users.AddRange(user1, user2);
        await db.SaveChangesAsync();
        var guardian1 = new Guardian(user1.Id, "Guardian One");
        var guardian2 = new Guardian(user2.Id, "Guardian Two");
        db.Guardians.AddRange(guardian1, guardian2);
        await db.SaveChangesAsync();

        var first = await service.LinkGuardianAsync(guardian1.Id, student.Id, GuardianRelationship.Parent, isPrimary: true, linkedByUserId: 0, CancellationToken.None);
        Assert.Equal(LinkGuardianOutcome.Linked, first.Outcome);

        var second = await service.LinkGuardianAsync(guardian2.Id, student.Id, GuardianRelationship.Other, isPrimary: true, linkedByUserId: 0, CancellationToken.None);
        Assert.Equal(LinkGuardianOutcome.StudentAlreadyHasGuardian, second.Outcome);

        // The case the old primary-only rule let through: a SECOND guardian
        // added as non-primary. That is exactly what "one responsible guardian"
        // has to close, or the rule is decorative.
        var secondaryAttempt = await service.LinkGuardianAsync(guardian2.Id, student.Id, GuardianRelationship.Other, isPrimary: false, linkedByUserId: 0, CancellationToken.None);
        Assert.Equal(LinkGuardianOutcome.StudentAlreadyHasGuardian, secondaryAttempt.Outcome);

        Assert.Equal(1, await db.Guardianships.CountAsync(g => g.StudentId == student.Id));
        Assert.Equal(1, await db.Guardianships.CountAsync(g => g.StudentId == student.Id && g.IsPrimary));
    }

    [Fact]
    public async Task Verifying_a_student_advances_PendingVerification_to_PendingLevel_and_is_idempotent_afterward()
    {
        var (db, service, _) = CreateService(_fixture);
        await using var _ = db;
        var countryId = await SeedCountryAsync(db);
        var student = new Student(countryId, "Student", new LocalDate(2015, 1, 1));
        db.Students.Add(student);
        await db.SaveChangesAsync();

        await service.VerifyStudentAsync(student.Id, CancellationToken.None);
        var afterFirstVerify = await db.Students.AsNoTracking().FirstAsync(s => s.Id == student.Id);
        Assert.Equal(StudentStatus.PendingLevel, afterFirstVerify.Status);

        // Manually advance past PendingLevel, then confirm a stale "verify" click never regresses status.
        var toAdvance = await db.Students.FirstAsync(s => s.Id == student.Id);
        toAdvance.MarkLevelAssigned();
        await db.SaveChangesAsync();

        await service.VerifyStudentAsync(student.Id, CancellationToken.None);
        var afterSecondVerify = await db.Students.AsNoTracking().FirstAsync(s => s.Id == student.Id);
        Assert.Equal(StudentStatus.Active, afterSecondVerify.Status);
    }

    [Fact]
    public async Task Assigning_a_level_advances_PendingLevel_to_Active_and_a_later_promotion_supersedes_it()
    {
        var (db, service, _) = CreateService(_fixture);
        await using var _ = db;
        var countryId = await SeedCountryAsync(db);
        var courseId = await SeedCourseAsync(db);
        var levelA = await SeedLevelAsync(db);
        var levelB = await SeedLevelAsync(db);
        var student = new Student(countryId, "Student", new LocalDate(2015, 1, 1));
        student.MarkVerified(); // PendingLevel
        db.Students.Add(student);
        await db.SaveChangesAsync();

        await service.AssignLevelAsync(student.Id, courseId, levelA, assignedByUserId: 0, reason: "Placement call", CancellationToken.None);

        var afterFirst = await db.Students.AsNoTracking().FirstAsync(s => s.Id == student.Id);
        Assert.Equal(StudentStatus.Active, afterFirst.Status);
        var currentAfterFirst = await db.StudentLevels.Where(l => l.StudentId == student.Id && l.IsCurrent).ToListAsync();
        Assert.Single(currentAfterFirst);
        Assert.Equal(levelA, currentAfterFirst[0].LevelId);

        await service.AssignLevelAsync(student.Id, courseId, levelB, assignedByUserId: 0, reason: "Promoted", CancellationToken.None);

        var currentAfterSecond = await db.StudentLevels.Where(l => l.StudentId == student.Id && l.IsCurrent).ToListAsync();
        Assert.Single(currentAfterSecond);
        Assert.Equal(levelB, currentAfterSecond[0].LevelId);
        var superseded = await db.StudentLevels.Where(l => l.StudentId == student.Id && l.LevelId == levelA).ToListAsync();
        Assert.All(superseded, l => Assert.False(l.IsCurrent));
    }

    /// <summary>Owner decision 2026-09-04, stage 1 of the big-features work:
    /// Student.PhoneNumber closed the gap this test used to pin OPEN. A child
    /// with no login of their own now has somewhere to keep a number — the
    /// Student row itself — which is precisely the case the centre most needed
    /// and previously could not record at all. No Identity user is invented for
    /// them; the number simply lives where the student does.</summary>
    [Fact]
    public async Task A_student_with_no_login_can_now_store_their_own_phone_number()
    {
        var (db, service, _) = CreateService(_fixture);
        await using var _ = db;
        var countryId = await SeedCountryAsync(db);

        var result = await service.RegisterStudentAsync(countryId, "Child Student", new LocalDate(2016, 1, 1),
            loginEmail: null, loginPassword: null, phoneNumber: "+962790000003", CancellationToken.None);

        Assert.Equal(RegisterStudentOutcome.Registered, result.Outcome);
        var student = await db.Students.FirstAsync(s => s.Id == result.StudentId);
        Assert.Null(student.UserId); // still no login - that has not changed
        Assert.Equal("+962790000003", student.PhoneNumber);
        // And no Identity user was conjured up just to hold it.
        Assert.Equal(0, await db.Users.CountAsync(u => u.PhoneNumber == "+962790000003"));
    }

    /// <summary>The column is nullable and stays nullable: a child registered
    /// with no number at all is a legitimate, unbroken record, which is what
    /// lets every student who predates the column survive its arrival.</summary>
    [Fact]
    public async Task A_student_registered_without_a_phone_number_is_still_valid()
    {
        var (db, service, _) = CreateService(_fixture);
        await using var _ = db;
        var countryId = await SeedCountryAsync(db);

        var result = await service.RegisterStudentAsync(countryId, "No Number", new LocalDate(2016, 1, 1),
            loginEmail: null, loginPassword: null, phoneNumber: null, CancellationToken.None);

        Assert.Equal(RegisterStudentOutcome.Registered, result.Outcome);
        var student = await db.Students.FirstAsync(s => s.Id == result.StudentId);
        Assert.Null(student.PhoneNumber);
    }

    /// <summary>Owner decision 2026-09-04, the multi-course rule in one test:
    /// a student holds one CURRENT level in each course at the same time, and
    /// being placed in a second course leaves the first one's level standing.
    /// Before the course column existed, ux_student_current_level made this
    /// physically impossible — the second assignment superseded the first, so
    /// every course after the first silently inherited or destroyed the
    /// previous placement.</summary>
    [Fact]
    public async Task A_student_holds_a_separate_current_level_in_each_course()
    {
        var (db, service, _) = CreateService(_fixture);
        await using var _ = db;
        var countryId = await SeedCountryAsync(db);
        var english = await SeedCourseAsync(db);
        var spanish = await SeedCourseAsync(db);
        var advanced = await SeedLevelAsync(db);
        var beginner = await SeedLevelAsync(db);
        var student = new Student(countryId, "Two Courses", new LocalDate(2000, 1, 1));
        student.MarkVerified();
        db.Students.Add(student);
        await db.SaveChangesAsync();

        await service.AssignLevelAsync(student.Id, english, advanced, assignedByUserId: 0,
            reason: "Placed advanced in English", CancellationToken.None);
        await service.AssignLevelAsync(student.Id, spanish, beginner, assignedByUserId: 0,
            reason: "Beginner in Spanish", CancellationToken.None);

        var current = await db.StudentLevels
            .Where(l => l.StudentId == student.Id && l.IsCurrent)
            .ToListAsync();

        Assert.Equal(2, current.Count);
        Assert.Equal(advanced, current.Single(l => l.CourseId == english).LevelId);
        Assert.Equal(beginner, current.Single(l => l.CourseId == spanish).LevelId);
    }

    /// <summary>The other half of the same rule: a promotion still supersedes,
    /// but only within its own course. One current row per course is the
    /// guarantee ux_student_course_current_level enforces.</summary>
    [Fact]
    public async Task A_promotion_supersedes_only_the_level_in_that_same_course()
    {
        var (db, service, _) = CreateService(_fixture);
        await using var _ = db;
        var countryId = await SeedCountryAsync(db);
        var english = await SeedCourseAsync(db);
        var spanish = await SeedCourseAsync(db);
        var first = await SeedLevelAsync(db);
        var second = await SeedLevelAsync(db);
        var spanishLevel = await SeedLevelAsync(db);
        var student = new Student(countryId, "Promoted", new LocalDate(2000, 1, 1));
        student.MarkVerified();
        db.Students.Add(student);
        await db.SaveChangesAsync();

        await service.AssignLevelAsync(student.Id, english, first, 0, "Initial English", CancellationToken.None);
        await service.AssignLevelAsync(student.Id, spanish, spanishLevel, 0, "Initial Spanish", CancellationToken.None);
        await service.AssignLevelAsync(student.Id, english, second, 0, "Promoted in English", CancellationToken.None);

        var current = await db.StudentLevels
            .Where(l => l.StudentId == student.Id && l.IsCurrent)
            .ToListAsync();

        // Still exactly one current row per course, and Spanish is untouched.
        Assert.Equal(2, current.Count);
        Assert.Equal(second, current.Single(l => l.CourseId == english).LevelId);
        Assert.Equal(spanishLevel, current.Single(l => l.CourseId == spanish).LevelId);

        // The superseded English row is kept as history, never deleted.
        Assert.Equal(2, await db.StudentLevels.CountAsync(l => l.StudentId == student.Id && l.CourseId == english));
    }

    /// <summary>Owner decision 2026-09-04, the whole point of the unlink path:
    /// correcting a wrongly-attached guardian must cost the family NOTHING.
    /// This seeds a student with a real package, a real confirmed payment and a
    /// real entitlement balance, unlinks the guardian, then asserts every one of
    /// those still stands. The owner named each of them explicitly, so each is
    /// asserted explicitly rather than trusting that "we only deleted one row"
    /// stays true as the schema grows.</summary>
    [Fact]
    public async Task Unlinking_a_guardian_leaves_the_subscription_payment_and_balance_untouched()
    {
        var (db, service, _) = CreateService(_fixture);
        await using var _ = db;
        var countryId = await SeedCountryAsync(db);
        var courseId = await SeedCourseAsync(db);
        var levelId = await SeedLevelAsync(db);

        var student = new Student(countryId, "Wrongly Linked Child", new LocalDate(2015, 1, 1));
        student.MarkVerified();
        db.Students.Add(student);
        var guardianUser = new ApplicationUser
        {
            UserName = $"gu-{Guid.NewGuid():N}",
            Email = $"{Guid.NewGuid():N}@test.mvteaches.local",
        };
        db.Users.Add(guardianUser);
        await db.SaveChangesAsync();
        var guardian = new Guardian(guardianUser.Id, "Wrong Guardian");
        db.Guardians.Add(guardian);
        await db.SaveChangesAsync();

        var linked = await service.LinkGuardianAsync(guardian.Id, student.Id, GuardianRelationship.Parent,
            isPrimary: true, linkedByUserId: 0, CancellationToken.None);
        Assert.Equal(LinkGuardianOutcome.Linked, linked.Outcome);

        // A package the family really paid for, and the hours it bought.
        var subscription = new MVTeaches.Domain.Subscriptions.Subscription(student.Id, countryId, courseId, levelId,
            MVTeaches.Domain.Catalog.SessionType.Group, new MVTeaches.Domain.Common.Money(50m, "JOD"),
            pricingPlanId: null, sessionsCount: 10, minutesTotal: 600, new LocalDate(2026, 1, 1), validityDays: 90,
            MVTeaches.Domain.Subscriptions.SubscriptionOrigin.GuardianPurchase, createdByUserId: 0,
            createdReason: null);
        subscription.Activate();
        db.Subscriptions.Add(subscription);
        await db.SaveChangesAsync();

        var now = SystemClock.Instance.GetCurrentInstant();
        var payment = new MVTeaches.Domain.Payments.Payment(student.Id, subscription.Id, payerUserId: null,
            new MVTeaches.Domain.Common.Money(50m, "JOD"), MVTeaches.Domain.Payments.PaymentMethod.BankTransfer,
            providerKey: "manual", referenceCode: $"UNLINK-{student.Id}", now);
        payment.Confirm(confirmedByUserId: 0, now, 50m, "JOD");
        db.Payments.Add(payment);
        db.EntitlementLedgerEntries.Add(MVTeaches.Domain.Ledger.EntitlementLedgerEntry.ForAdminGrant(
            student.Id, subscription.Id, courseId, levelId, MVTeaches.Domain.Catalog.SessionType.Group,
            600, performedByUserId: 0, "seed", now));
        await db.SaveChangesAsync();

        var result = await service.UnlinkGuardianAsync(guardian.Id, student.Id, actingUserId: 7,
            "Linked to the wrong family by mistake", CancellationToken.None);

        Assert.Equal(UnlinkGuardianOutcome.Unlinked, result.Outcome);

        // The link is the ONLY thing gone.
        Assert.False(await db.Guardianships.AnyAsync(g => g.GuardianId == guardian.Id && g.StudentId == student.Id));
        Assert.True(await db.Students.AnyAsync(s => s.Id == student.Id));
        Assert.True(await db.Guardians.AnyAsync(g => g.Id == guardian.Id));
        Assert.True(await db.Subscriptions.AnyAsync(s => s.Id == subscription.Id));
        Assert.True(await db.Payments.AnyAsync(p => p.Id == payment.Id));
        Assert.Equal(600, await db.EntitlementLedgerEntries
            .Where(l => l.SubscriptionId == subscription.Id).SumAsync(l => l.DeltaMinutes));

        // And the reason outlived the row it explains.
        Assert.True(await db.AuditLogEntries.AnyAsync(a => a.Action == "GuardianUnlinked"
            && a.Reason == "Linked to the wrong family by mistake"));
    }

    /// <summary>The correction actually completes: after unlinking, the right
    /// guardian can be linked, which the one-guardian rule would otherwise have
    /// refused forever. This is the dead end the unlink path exists to open.</summary>
    [Fact]
    public async Task After_unlinking_the_correct_guardian_can_be_linked()
    {
        var (db, service, _) = CreateService(_fixture);
        await using var _ = db;
        var countryId = await SeedCountryAsync(db);
        var student = new Student(countryId, "Student", new LocalDate(2015, 1, 1));
        db.Students.Add(student);
        var user1 = new ApplicationUser { UserName = $"g1-{Guid.NewGuid():N}", Email = $"{Guid.NewGuid():N}@test.mvteaches.local" };
        var user2 = new ApplicationUser { UserName = $"g2-{Guid.NewGuid():N}", Email = $"{Guid.NewGuid():N}@test.mvteaches.local" };
        db.Users.AddRange(user1, user2);
        await db.SaveChangesAsync();
        var wrong = new Guardian(user1.Id, "Wrong Guardian");
        var right = new Guardian(user2.Id, "Right Guardian");
        db.Guardians.AddRange(wrong, right);
        await db.SaveChangesAsync();

        await service.LinkGuardianAsync(wrong.Id, student.Id, GuardianRelationship.Parent, true, 0, CancellationToken.None);

        // Blocked while the wrong one is still attached — the dead end.
        var blocked = await service.LinkGuardianAsync(right.Id, student.Id, GuardianRelationship.Parent, true, 0, CancellationToken.None);
        Assert.Equal(LinkGuardianOutcome.StudentAlreadyHasGuardian, blocked.Outcome);

        var unlinked = await service.UnlinkGuardianAsync(wrong.Id, student.Id, 0, "Wrong family", CancellationToken.None);
        Assert.Equal(UnlinkGuardianOutcome.Unlinked, unlinked.Outcome);

        var relinked = await service.LinkGuardianAsync(right.Id, student.Id, GuardianRelationship.Parent, true, 0, CancellationToken.None);
        Assert.Equal(LinkGuardianOutcome.Linked, relinked.Outcome);
        Assert.Equal(1, await db.Guardianships.CountAsync(g => g.StudentId == student.Id));
    }

    /// <summary>A reason is mandatory, and a refusal writes nothing: the link is
    /// still there afterwards.</summary>
    [Fact]
    public async Task Unlinking_without_a_reason_is_refused_and_changes_nothing()
    {
        var (db, service, _) = CreateService(_fixture);
        await using var _ = db;
        var countryId = await SeedCountryAsync(db);
        var student = new Student(countryId, "Student", new LocalDate(2015, 1, 1));
        db.Students.Add(student);
        var guardianUser = new ApplicationUser { UserName = $"gu-{Guid.NewGuid():N}", Email = $"{Guid.NewGuid():N}@test.mvteaches.local" };
        db.Users.Add(guardianUser);
        await db.SaveChangesAsync();
        var guardian = new Guardian(guardianUser.Id, "Guardian");
        db.Guardians.Add(guardian);
        await db.SaveChangesAsync();
        await service.LinkGuardianAsync(guardian.Id, student.Id, GuardianRelationship.Parent, true, 0, CancellationToken.None);

        var blank = await service.UnlinkGuardianAsync(guardian.Id, student.Id, 0, "   ", CancellationToken.None);

        Assert.Equal(UnlinkGuardianOutcome.ReasonRequired, blank.Outcome);
        Assert.True(await db.Guardianships.AnyAsync(g => g.GuardianId == guardian.Id && g.StudentId == student.Id));
    }

    /// <summary>Unlinking a pair that was never linked reports it rather than
    /// throwing — the state the admin wanted already holds, and a second click
    /// on a slow page is not an error.</summary>
    [Fact]
    public async Task Unlinking_a_pair_that_is_not_linked_reports_it_rather_than_throwing()
    {
        var (db, service, _) = CreateService(_fixture);
        await using var _ = db;
        var countryId = await SeedCountryAsync(db);
        var student = new Student(countryId, "Student", new LocalDate(2015, 1, 1));
        db.Students.Add(student);
        var guardianUser = new ApplicationUser { UserName = $"gu-{Guid.NewGuid():N}", Email = $"{Guid.NewGuid():N}@test.mvteaches.local" };
        db.Users.Add(guardianUser);
        await db.SaveChangesAsync();
        var guardian = new Guardian(guardianUser.Id, "Guardian");
        db.Guardians.Add(guardian);
        await db.SaveChangesAsync();

        var result = await service.UnlinkGuardianAsync(guardian.Id, student.Id, 0, "Never linked", CancellationToken.None);

        Assert.Equal(UnlinkGuardianOutcome.NotLinked, result.Outcome);
    }
}
