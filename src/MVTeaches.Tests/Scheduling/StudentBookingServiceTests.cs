using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Scheduling;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Common;
using MVTeaches.Domain.Ledger;
using MVTeaches.Domain.People;
using MVTeaches.Domain.Placement;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Domain.Subscriptions;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using MVTeaches.Infrastructure.Scheduling;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace MVTeaches.Tests.Scheduling;

/// <summary>
/// Owner correction (student self-service booking, 2026-08-28), superseding
/// "Admin assigns the student's normal lesson dates": IStudentBookingService
/// is the new entry point a student uses to book their OWN future sessions,
/// filtered to their OWN level, within their OWN package's remaining
/// capacity — exercised here against a real PostgreSQL database, including
/// the genuine concurrency guarantee (a row lock, not a plain read-then-write)
/// that two simultaneous bookings by the same student cannot together
/// exceed one package.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class StudentBookingServiceTests
{
    private readonly TestDatabaseFixture _fixture;
    private static long _idSeed = 70_000_000;

    public StudentBookingServiceTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static long NextId() => Interlocked.Increment(ref _idSeed);

    private static string TwoLetterCode(long seed)
    {
        var n = (int)(seed % 676);
        return string.Concat((char)('A' + n / 26), (char)('A' + n % 26));
    }

    private static int? _sharedCountryId;

    private async Task<int> GetOrSeedCountryAsync(MvTeachesDbContext db)
    {
        if (_sharedCountryId is { } existing)
        {
            return existing;
        }

        // Retry-safe: the 2-letter code space (676 combinations) is shared
        // across every test class in this run via the same NextId()-derived
        // TwoLetterCode pattern, so a residue collision with some OTHER
        // class's own seed range is a real, previously-hit flake (see this
        // session's history) rather than a theoretical one. Catching the
        // real unique-violation and retrying with a fresh NextId() is the
        // same self-correcting pattern this codebase's own production code
        // uses everywhere else, and it removes the need to hand-verify no
        // other test class's arithmetic happens to collide.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var countryId = (int)NextId();
            db.Countries.Add(new Country(countryId, TwoLetterCode(countryId), "دولة", "Country", "JOD", "+962", "Asia/Amman"));
            try
            {
                await db.SaveChangesAsync();
                _sharedCountryId = countryId;
                return countryId;
            }
            catch (Microsoft.EntityFrameworkCore.DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
            {
                db.ChangeTracker.Clear();
            }
        }

        throw new InvalidOperationException("Could not find a free 2-letter country code after 10 attempts.");
    }

    private static async Task<long> CreateUserAsync(MvTeachesDbContext db, string label)
    {
        var user = new ApplicationUser
        {
            UserName = $"{label}-{Guid.NewGuid():N}",
            NormalizedUserName = $"{label}-{Guid.NewGuid():N}".ToUpperInvariant(),
            Email = $"{Guid.NewGuid():N}@test.mvteaches.local",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private record Fixture(long StudentId, long StudentUserId, int LevelId, long CourseId, int AgeGroupId,
        int CountryId, long TeacherId);

    /// <summary>Seeds a student with a CURRENT level assignment (no
    /// subscription — callers add one if the test needs balance).</summary>
    private async Task<Fixture> SeedStudentWithLevelAsync(MvTeachesDbContext db)
    {
        var countryId = await GetOrSeedCountryAsync(db);
        var courseId = NextId();
        var levelId = (int)NextId();
        var ageGroupId = (int)NextId();
        var studentUserId = await CreateUserAsync(db, "student");
        var teacherUserId = await CreateUserAsync(db, "teacher");

        db.Courses.Add(new Course("C" + courseId, "دورة", "Course"));
        db.Levels.Add(new Level(levelId, "L" + levelId, "مستوى", "Level", levelId));
        db.AgeGroups.Add(new AgeGroup(ageGroupId, "A" + ageGroupId, 5, 60, true));
        var teacher = new Teacher(teacherUserId, "Teacher", "Asia/Amman");
        db.Teachers.Add(teacher);
        var student = new Student(countryId, "Student", new LocalDate(2000, 1, 1), studentUserId);
        db.Students.Add(student);
        await db.SaveChangesAsync();

        db.StudentLevels.Add(new StudentLevel(student.Id, levelId, teacherUserId, AssignedByRole.Admin,
            LevelAssignmentSource.AdminOverride, null, "test setup", SystemClock.Instance.GetCurrentInstant()));
        await db.SaveChangesAsync();

        return new Fixture(student.Id, studentUserId, levelId, courseId, ageGroupId, countryId, teacher.Id);
    }

    private async Task<long> SeedSubscriptionAsync(MvTeachesDbContext db, Fixture fx, int minutes, Instant now)
    {
        var subscription = new Subscription(fx.StudentId, fx.CountryId, fx.CourseId, fx.LevelId,
            new Money(100m, "JOD"), null, 10, minutes, new LocalDate(2026, 1, 1), 365,
            SubscriptionOrigin.SelfPurchase, fx.StudentUserId, null);
        subscription.Activate();
        db.Subscriptions.Add(subscription);
        await db.SaveChangesAsync();

        db.EntitlementLedgerEntries.Add(EntitlementLedgerEntry.ForPurchase(
            fx.StudentId, subscription.Id, fx.CourseId, fx.LevelId, minutes, paymentId: NextId(), fx.StudentUserId, now.Minus(Duration.FromDays(1))));
        await db.SaveChangesAsync();
        return subscription.Id;
    }

    private ClassSession NewSession(Fixture fx, int levelId, long courseId, int durationMinutes, Instant start, SessionType sessionType = SessionType.Group) =>
        new(fx.CountryId, null, courseId, levelId, fx.AgeGroupId, fx.TeacherId, start, start.Plus(Duration.FromMinutes(durationMinutes)),
            "Asia/Amman", "10:00", sessionType, start.Minus(Duration.FromDays(1)));

    private IStudentBookingService CreateService(MvTeachesDbContext db, Instant now) => new StudentBookingService(db, new FakeClock(now));

    [Fact]
    public async Task A_student_cannot_book_a_session_for_another_students_account()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var fx = await SeedStudentWithLevelAsync(db);
        var session = NewSession(fx, fx.LevelId, fx.CourseId, 60, now.Plus(Duration.FromDays(1)));
        db.ClassSessions.Add(session);
        await db.SaveChangesAsync();

        var attackerUserId = await CreateUserAsync(db, "attacker");

        var result = await CreateService(db, now).BookSessionAsync(fx.StudentId, session.Id, attackerUserId, CancellationToken.None);

        Assert.Equal(BookSessionOutcome.Unauthorized, result.Outcome);
        Assert.False(await db.SessionEnrollments.AnyAsync(e => e.SessionId == session.Id));
    }

    [Fact]
    public async Task Booking_a_session_of_a_different_level_is_rejected_even_if_requested_directly()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var fx = await SeedStudentWithLevelAsync(db);
        var otherLevelId = (int)NextId();
        db.Levels.Add(new Level(otherLevelId, "L" + otherLevelId, "مستوى آخر", "Other level", otherLevelId));
        await db.SaveChangesAsync();

        var wrongLevelSession = NewSession(fx, otherLevelId, fx.CourseId, 60, now.Plus(Duration.FromDays(1)));
        db.ClassSessions.Add(wrongLevelSession);
        await db.SaveChangesAsync();

        var result = await CreateService(db, now).BookSessionAsync(fx.StudentId, wrongLevelSession.Id, fx.StudentUserId, CancellationToken.None);

        Assert.Equal(BookSessionOutcome.SessionLevelMismatch, result.Outcome);
        Assert.False(await db.SessionEnrollments.AnyAsync(e => e.SessionId == wrongLevelSession.Id));
    }

    [Fact]
    public async Task Booking_without_a_current_level_assignment_is_rejected()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var countryId = await GetOrSeedCountryAsync(db);
        var studentUserId = await CreateUserAsync(db, "nolevel");
        var student = new Student(countryId, "No Level Student", new LocalDate(2000, 1, 1), studentUserId);
        db.Students.Add(student);
        await db.SaveChangesAsync();

        var result = await CreateService(db, now).BookSessionAsync(student.Id, NextId(), studentUserId, CancellationToken.None);

        Assert.Equal(BookSessionOutcome.NoCurrentLevelAssigned, result.Outcome);
    }

    [Fact]
    public async Task A_student_can_book_an_available_session_matching_their_level_within_their_package()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var fx = await SeedStudentWithLevelAsync(db);
        await SeedSubscriptionAsync(db, fx, 120, now);
        var session = NewSession(fx, fx.LevelId, fx.CourseId, 60, now.Plus(Duration.FromDays(1)));
        db.ClassSessions.Add(session);
        await db.SaveChangesAsync();

        var result = await CreateService(db, now).BookSessionAsync(fx.StudentId, session.Id, fx.StudentUserId, CancellationToken.None);

        Assert.Equal(BookSessionOutcome.Booked, result.Outcome);
        await using var verify = _fixture.CreateContext();
        Assert.True(await verify.SessionEnrollments.AnyAsync(
            e => e.SessionId == session.Id && e.StudentId == fx.StudentId && e.State == EnrollmentState.Active));
        var refreshed = await verify.ClassSessions.AsNoTracking().FirstAsync(s => s.Id == session.Id);
        Assert.Equal(1, refreshed.SeatsTaken);
    }

    [Fact]
    public async Task Booking_the_same_session_twice_is_rejected_the_second_time()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var fx = await SeedStudentWithLevelAsync(db);
        await SeedSubscriptionAsync(db, fx, 120, now);
        var session = NewSession(fx, fx.LevelId, fx.CourseId, 60, now.Plus(Duration.FromDays(1)));
        db.ClassSessions.Add(session);
        await db.SaveChangesAsync();

        var first = await CreateService(db, now).BookSessionAsync(fx.StudentId, session.Id, fx.StudentUserId, CancellationToken.None);
        var second = await CreateService(db, now).BookSessionAsync(fx.StudentId, session.Id, fx.StudentUserId, CancellationToken.None);

        Assert.Equal(BookSessionOutcome.Booked, first.Outcome);
        Assert.Equal(BookSessionOutcome.AlreadyBooked, second.Outcome);
    }

    [Fact]
    public async Task Booking_beyond_the_remaining_package_balance_is_rejected()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var fx = await SeedStudentWithLevelAsync(db);
        await SeedSubscriptionAsync(db, fx, 60, now); // exactly one 60-minute session, no more
        var session1 = NewSession(fx, fx.LevelId, fx.CourseId, 60, now.Plus(Duration.FromDays(1)));
        var session2 = NewSession(fx, fx.LevelId, fx.CourseId, 60, now.Plus(Duration.FromDays(2)));
        db.ClassSessions.AddRange(session1, session2);
        await db.SaveChangesAsync();

        var service = CreateService(db, now);
        var first = await service.BookSessionAsync(fx.StudentId, session1.Id, fx.StudentUserId, CancellationToken.None);
        var second = await service.BookSessionAsync(fx.StudentId, session2.Id, fx.StudentUserId, CancellationToken.None);

        Assert.Equal(BookSessionOutcome.Booked, first.Outcome);
        Assert.Equal(BookSessionOutcome.PackageLimitExceeded, second.Outcome);

        await using var verify = _fixture.CreateContext();
        Assert.Equal(1, await verify.SessionEnrollments.CountAsync(e => e.StudentId == fx.StudentId && e.State == EnrollmentState.Active));
    }

    /// <summary>The genuine concurrency guarantee: two DIFFERENT sessions,
    /// each individually within the package if booked alone, booked at the
    /// SAME time via Task.WhenAll on separate DbContexts. Without the row
    /// lock in StudentBookingService, both could pass the balance check
    /// before either commits — this proves exactly one wins.</summary>
    [Fact]
    public async Task Two_concurrent_bookings_by_the_same_student_do_not_together_exceed_the_package()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var seedDb = _fixture.CreateContext();
        var fx = await SeedStudentWithLevelAsync(seedDb);
        await SeedSubscriptionAsync(seedDb, fx, 60, now); // only enough for ONE of the two
        var session1 = NewSession(fx, fx.LevelId, fx.CourseId, 60, now.Plus(Duration.FromDays(1)));
        var session2 = NewSession(fx, fx.LevelId, fx.CourseId, 60, now.Plus(Duration.FromDays(2)));
        seedDb.ClassSessions.AddRange(session1, session2);
        await seedDb.SaveChangesAsync();

        var service1 = new StudentBookingService(_fixture.CreateContext(), new FakeClock(now));
        var service2 = new StudentBookingService(_fixture.CreateContext(), new FakeClock(now));

        var task1 = service1.BookSessionAsync(fx.StudentId, session1.Id, fx.StudentUserId, CancellationToken.None);
        var task2 = service2.BookSessionAsync(fx.StudentId, session2.Id, fx.StudentUserId, CancellationToken.None);
        var results = await Task.WhenAll(task1, task2);

        Assert.Contains(results, r => r.Outcome == BookSessionOutcome.Booked);
        Assert.Contains(results, r => r.Outcome == BookSessionOutcome.PackageLimitExceeded);

        await using var verify = _fixture.CreateContext();
        Assert.Equal(1, await verify.SessionEnrollments.CountAsync(e => e.StudentId == fx.StudentId && e.State == EnrollmentState.Active));
    }

    /// <summary>Capacity concurrency through the self-booking entry point
    /// specifically (the underlying atomic seat claim is already proven
    /// elsewhere, but this path's own wiring to it needed its own proof).</summary>
    [Fact]
    public async Task Two_concurrent_bookings_by_different_students_do_not_exceed_one_seat_capacity()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var seedDb = _fixture.CreateContext();
        var fx1 = await SeedStudentWithLevelAsync(seedDb);
        await SeedSubscriptionAsync(seedDb, fx1, 60, now);

        // A second student sharing the SAME course/level as fx1, so both can
        // book the SAME single-seat session.
        var studentUserId2 = await CreateUserAsync(seedDb, "student2");
        var student2 = new Student(fx1.CountryId, "Student 2", new LocalDate(2000, 1, 1), studentUserId2);
        seedDb.Students.Add(student2);
        await seedDb.SaveChangesAsync();
        seedDb.StudentLevels.Add(new StudentLevel(student2.Id, fx1.LevelId, fx1.TeacherId, AssignedByRole.Admin,
            LevelAssignmentSource.AdminOverride, null, "test setup", now));
        await seedDb.SaveChangesAsync();
        var fx2 = fx1 with { StudentId = student2.Id, StudentUserId = studentUserId2 };
        await SeedSubscriptionAsync(seedDb, fx2, 60, now);

        var session = NewSession(fx1, fx1.LevelId, fx1.CourseId, 60, now.Plus(Duration.FromDays(1)), SessionType.Private);
        seedDb.ClassSessions.Add(session);
        await seedDb.SaveChangesAsync();

        var service1 = new StudentBookingService(_fixture.CreateContext(), new FakeClock(now));
        var service2 = new StudentBookingService(_fixture.CreateContext(), new FakeClock(now));

        var task1 = service1.BookSessionAsync(fx1.StudentId, session.Id, fx1.StudentUserId, CancellationToken.None);
        var task2 = service2.BookSessionAsync(fx2.StudentId, session.Id, fx2.StudentUserId, CancellationToken.None);
        var results = await Task.WhenAll(task1, task2);

        Assert.Contains(results, r => r.Outcome == BookSessionOutcome.Booked);
        Assert.Contains(results, r => r.Outcome == BookSessionOutcome.SessionFull);

        await using var verify = _fixture.CreateContext();
        var refreshed = await verify.ClassSessions.AsNoTracking().FirstAsync(s => s.Id == session.Id);
        Assert.Equal(1, refreshed.SeatsTaken);
    }

    /// <summary>New-package separation (D-17/owner correction): a fresh
    /// purchase after an earlier one is fully consumed is entirely
    /// independent — booking against it is never blocked by the OLDER
    /// package's exhausted history.</summary>
    [Fact]
    public async Task A_fresh_package_purchase_is_independent_of_an_earlier_exhausted_one()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var fx = await SeedStudentWithLevelAsync(db);
        await SeedSubscriptionAsync(db, fx, 60, now); // package #1 — exactly one session
        var session1 = NewSession(fx, fx.LevelId, fx.CourseId, 60, now.Plus(Duration.FromDays(1)));
        db.ClassSessions.Add(session1);
        await db.SaveChangesAsync();

        var service = CreateService(db, now);
        var first = await service.BookSessionAsync(fx.StudentId, session1.Id, fx.StudentUserId, CancellationToken.None);
        Assert.Equal(BookSessionOutcome.Booked, first.Outcome);

        // Package #1 is now fully committed. A brand-new purchase (package #2,
        // a new subscription, new purchase date) must open up fresh capacity —
        // never blocked by package #1's own history.
        await SeedSubscriptionAsync(db, fx, 60, now);
        var session2 = NewSession(fx, fx.LevelId, fx.CourseId, 60, now.Plus(Duration.FromDays(2)));
        db.ClassSessions.Add(session2);
        await db.SaveChangesAsync();

        var second = await service.BookSessionAsync(fx.StudentId, session2.Id, fx.StudentUserId, CancellationToken.None);
        Assert.Equal(BookSessionOutcome.Booked, second.Outcome);
    }
}
