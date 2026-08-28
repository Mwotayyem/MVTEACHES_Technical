using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Attendance;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Common;
using MVTeaches.Domain.Ledger;
using MVTeaches.Domain.People;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Domain.Subscriptions;
using MVTeaches.Infrastructure.Attendance;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using MVTeaches.Infrastructure.Scheduling;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace MVTeaches.Tests.Attendance;

/// <summary>
/// Owner correction (student self-service booking, 2026-08-28), superseding
/// D-83's original "a student who never joins is never debited": a session
/// that ends with an enrolled student never having pressed Join is now
/// finalized as a no-show and consumes the scheduled duration exactly once —
/// exercised here against a real PostgreSQL database.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class SessionFinalizationServiceTests
{
    private readonly TestDatabaseFixture _fixture;
    private static long _idSeed = 80_000_000;

    public SessionFinalizationServiceTests(TestDatabaseFixture fixture) => _fixture = fixture;

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

    private record Fixture(long StudentId, long StudentUserId, int LevelId, long CourseId, int AgeGroupId, int CountryId, long TeacherId);

    private async Task<Fixture> SeedStudentAsync(MvTeachesDbContext db)
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
            fx.StudentId, subscription.Id, fx.CourseId, fx.LevelId, minutes, paymentId: NextId(), fx.StudentUserId, now.Minus(Duration.FromDays(2))));
        await db.SaveChangesAsync();
        return subscription.Id;
    }

    private ClassSession NewEndedSession(Fixture fx, int durationMinutes, Instant now, int startedMinutesAgo) =>
        new(fx.CountryId, null, fx.CourseId, fx.LevelId, fx.AgeGroupId, fx.TeacherId,
            now.Minus(Duration.FromMinutes(startedMinutesAgo)),
            now.Minus(Duration.FromMinutes(startedMinutesAgo - durationMinutes)),
            "Asia/Amman", "10:00", SessionType.Group, 4, now.Minus(Duration.FromDays(1)));

    private ISessionFinalizationService CreateFinalizer(MvTeachesDbContext db, Instant now) =>
        new SessionFinalizationService(db, new FakeClock(now));

    [Fact]
    public async Task A_student_who_never_joined_an_ended_session_is_finalized_as_no_show_and_consumes_the_duration()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var fx = await SeedStudentAsync(db);
        await SeedSubscriptionAsync(db, fx, 60, now);
        var session = NewEndedSession(fx, 60, now, startedMinutesAgo: 90); // ended 30 min ago
        db.ClassSessions.Add(session);
        await db.SaveChangesAsync();
        db.SessionEnrollments.Add(new SessionEnrollment(session.Id, fx.StudentId, fx.AgeGroupId, fx.StudentUserId, now.Minus(Duration.FromDays(1))));
        await db.SaveChangesAsync();

        var summary = await CreateFinalizer(db, now).FinalizeEndedSessionsAsync(CancellationToken.None);

        Assert.Equal(1, summary.SessionsFinalized);
        Assert.Equal(1, summary.StudentsMarkedNoShow);
        Assert.Equal(0, summary.NoShowsWithNoConsumableSubscription);

        await using var verify = _fixture.CreateContext();
        var attendance = await verify.AttendanceRecords.SingleAsync(a => a.SessionId == session.Id && a.StudentId == fx.StudentId);
        Assert.False(attendance.IsPresent);
        Assert.Null(attendance.MarkedByUserId);

        Assert.Equal(1, await verify.EntitlementLedgerEntries.CountAsync(
            l => l.SessionId == session.Id && l.StudentId == fx.StudentId && l.Reason == LedgerReason.Consumption));

        var refreshedSession = await verify.ClassSessions.AsNoTracking().FirstAsync(s => s.Id == session.Id);
        Assert.Equal(ClassSessionStatus.Completed, refreshedSession.Status);
    }

    [Fact]
    public async Task A_student_who_joined_is_never_touched_by_finalization()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var fx = await SeedStudentAsync(db);
        var session = NewEndedSession(fx, 60, now, startedMinutesAgo: 90);
        db.ClassSessions.Add(session);
        await db.SaveChangesAsync();
        db.SessionEnrollments.Add(new SessionEnrollment(session.Id, fx.StudentId, fx.AgeGroupId, fx.StudentUserId, now.Minus(Duration.FromDays(1))));
        db.AttendanceRecords.Add(new Domain.Attendance.AttendanceRecord(session.Id, fx.StudentId, fx.StudentUserId, now.Minus(Duration.FromMinutes(80)), isPresent: true));
        await db.SaveChangesAsync();

        await CreateFinalizer(db, now).FinalizeEndedSessionsAsync(CancellationToken.None);

        await using var verify = _fixture.CreateContext();
        var attendance = await verify.AttendanceRecords.SingleAsync(a => a.SessionId == session.Id && a.StudentId == fx.StudentId);
        Assert.True(attendance.IsPresent); // untouched
        Assert.Equal(0, await verify.EntitlementLedgerEntries.CountAsync(l => l.SessionId == session.Id));
    }

    /// <summary>Centre cancellation: a cancelled session's Status is never
    /// Scheduled, so it never enters the finalization query at all — the
    /// student's hours must remain untouched.</summary>
    [Fact]
    public async Task A_centre_cancelled_session_is_never_finalized_and_never_consumes_hours()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var fx = await SeedStudentAsync(db);
        await SeedSubscriptionAsync(db, fx, 60, now);
        var session = NewEndedSession(fx, 60, now, startedMinutesAgo: 90);
        session.Cancel("teacher sick", cancelledByUserId: NextId());
        db.ClassSessions.Add(session);
        await db.SaveChangesAsync();
        db.SessionEnrollments.Add(new SessionEnrollment(session.Id, fx.StudentId, fx.AgeGroupId, fx.StudentUserId, now.Minus(Duration.FromDays(1))));
        await db.SaveChangesAsync();

        var summary = await CreateFinalizer(db, now).FinalizeEndedSessionsAsync(CancellationToken.None);

        Assert.Equal(0, summary.SessionsFinalized);
        await using var verify = _fixture.CreateContext();
        Assert.False(await verify.AttendanceRecords.AnyAsync(a => a.SessionId == session.Id));
        Assert.Equal(0, await verify.EntitlementLedgerEntries.CountAsync(l => l.SessionId == session.Id));
    }

    /// <summary>A replacement enrollment's own no-show must not write a second
    /// ledger entry — its cost was already paid by the ORIGINAL consumption.</summary>
    [Fact]
    public async Task A_no_show_on_a_replacement_enrollment_writes_no_ledger_entry()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var fx = await SeedStudentAsync(db);
        var originalSessionId = NextId(); // the real session id doesn't need to exist for this unit's purposes
        var replacement = NewEndedSession(fx, 60, now, startedMinutesAgo: 90);
        db.ClassSessions.Add(replacement);
        await db.SaveChangesAsync();
        db.SessionEnrollments.Add(SessionEnrollment.AsReplacementLesson(
            replacement.Id, fx.StudentId, fx.AgeGroupId, originalSessionId, approvedByUserId: NextId(), now.Minus(Duration.FromDays(1))));
        await db.SaveChangesAsync();

        var summary = await CreateFinalizer(db, now).FinalizeEndedSessionsAsync(CancellationToken.None);

        Assert.Equal(1, summary.StudentsMarkedNoShow);
        await using var verify = _fixture.CreateContext();
        var attendance = await verify.AttendanceRecords.SingleAsync(a => a.SessionId == replacement.Id && a.StudentId == fx.StudentId);
        Assert.False(attendance.IsPresent);
        Assert.Equal(0, await verify.EntitlementLedgerEntries.CountAsync(l => l.SessionId == replacement.Id));
    }

    /// <summary>Running the sweep twice must never double-finalize — the
    /// session is already Completed after the first run and drops out of the
    /// query entirely.</summary>
    [Fact]
    public async Task Running_finalization_twice_is_idempotent()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var fx = await SeedStudentAsync(db);
        await SeedSubscriptionAsync(db, fx, 60, now);
        var session = NewEndedSession(fx, 60, now, startedMinutesAgo: 90);
        db.ClassSessions.Add(session);
        await db.SaveChangesAsync();
        db.SessionEnrollments.Add(new SessionEnrollment(session.Id, fx.StudentId, fx.AgeGroupId, fx.StudentUserId, now.Minus(Duration.FromDays(1))));
        await db.SaveChangesAsync();

        await CreateFinalizer(db, now).FinalizeEndedSessionsAsync(CancellationToken.None);
        var second = await CreateFinalizer(db, now).FinalizeEndedSessionsAsync(CancellationToken.None);

        Assert.Equal(0, second.SessionsFinalized); // already Completed, not picked up again

        await using var verify = _fixture.CreateContext();
        Assert.Equal(1, await verify.AttendanceRecords.CountAsync(a => a.SessionId == session.Id));
        Assert.Equal(1, await verify.EntitlementLedgerEntries.CountAsync(l => l.SessionId == session.Id));
    }

    /// <summary>The core race guarantee the owner asked for explicitly: Join
    /// and no-show finalization racing at the session boundary must still
    /// produce exactly one consumption and one final attendance outcome —
    /// proven with a genuine concurrent database race (separate DbContexts,
    /// Task.WhenAll), not a sequential simulation.</summary>
    [Fact]
    public async Task A_late_join_racing_finalization_produces_exactly_one_outcome_and_one_consumption()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var seedDb = _fixture.CreateContext();
        var fx = await SeedStudentAsync(seedDb);
        await SeedSubscriptionAsync(seedDb, fx, 60, now);
        var session = NewEndedSession(fx, 60, now, startedMinutesAgo: 90);
        seedDb.ClassSessions.Add(session);
        await seedDb.SaveChangesAsync();
        seedDb.SessionEnrollments.Add(new SessionEnrollment(session.Id, fx.StudentId, fx.AgeGroupId, fx.StudentUserId, now.Minus(Duration.FromDays(1))));
        await seedDb.SaveChangesAsync();

        var joinService = new JoinAttendanceService(_fixture.CreateContext(), new FakeClock(now));
        var finalizer = new SessionFinalizationService(_fixture.CreateContext(), new FakeClock(now));

        var joinTask = joinService.JoinAsync(new JoinAttendanceRequest(session.Id, fx.StudentId, fx.StudentUserId), CancellationToken.None);
        var finalizeTask = finalizer.FinalizeEndedSessionsAsync(CancellationToken.None);
        await Task.WhenAll(joinTask, finalizeTask);

        await using var verify = _fixture.CreateContext();
        // Exactly one AttendanceRecord — whichever side won, never two rows,
        // never zero.
        Assert.Equal(1, await verify.AttendanceRecords.CountAsync(a => a.SessionId == session.Id && a.StudentId == fx.StudentId));
        // Exactly one Consumption ledger entry either way.
        Assert.Equal(1, await verify.EntitlementLedgerEntries.CountAsync(
            l => l.SessionId == session.Id && l.StudentId == fx.StudentId && l.Reason == LedgerReason.Consumption));
    }
}
