using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Attendance;
using MVTeaches.Application.Scheduling;
using MVTeaches.Domain.Attendance;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Ledger;
using MVTeaches.Domain.People;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Domain.Subscriptions;
using MVTeaches.Infrastructure.Attendance;
using MVTeaches.Infrastructure.Persistence;
using MVTeaches.Infrastructure.Scheduling;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace MVTeaches.Tests.Scheduling;

/// <summary>
/// Owner clarification (2026-08-27), replacing the earlier standalone
/// makeup-credit design entirely. Two distinct cases, both exercised here
/// against a real PostgreSQL database:
///
/// 1. RescheduleUnattendedEnrollmentAsync — the student never pressed Join;
///    nothing was consumed; the admin just moves that specific lesson-hour.
///
/// 2. ApproveReplacementLessonAsync — the student DID press Join (their
///    consumption stands untouched forever) and the admin approves one
///    specific replacement session, which IJoinAttendanceService must let
///    the student Join for free (no second deduction) — verified by actually
///    calling JoinAttendanceService.JoinAsync against the real database, not
///    just checking the enrollment row.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class RescheduleAndCompensationTests
{
    private readonly TestDatabaseFixture _fixture;
    private static long _idSeed = 60_000_000;

    public RescheduleAndCompensationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static long NextId() => Interlocked.Increment(ref _idSeed);

    private static string TwoLetterCode(long seed)
    {
        var n = (int)(seed % 676);
        return string.Concat((char)('A' + n / 26), (char)('A' + n % 26));
    }

    private static async Task<long> CreateUserAsync(MvTeachesDbContext db, string label)
    {
        var user = new Infrastructure.Identity.ApplicationUser
        {
            UserName = $"{label}-{Guid.NewGuid():N}",
            NormalizedUserName = $"{label}-{Guid.NewGuid():N}".ToUpperInvariant(),
            Email = $"{Guid.NewGuid():N}@test.mvteaches.local",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private record Scenario(long OriginalSessionId, long ReplacementSessionId, long StudentId, long StudentUserId,
        int AgeGroupId, int DurationMinutes, Instant ReplacementStartsAtUtc);

    // One Country for the whole class (not one per test/scenario): the 2-letter
    // code space (676 combinations) is shared with every other test class in the
    // same run via the same NextId()-derived TwoLetterCode pattern, and a prior
    // session already hit a real cross-class collision from creating too many —
    // see MakeUpCreditServiceTests' history. Minimizing how many this class
    // creates keeps the odds negligible instead of merely unlikely.
    private static int? _sharedCountryId;

    private async Task<int> GetOrSeedCountryAsync(MvTeachesDbContext db)
    {
        if (_sharedCountryId is { } existing)
        {
            return existing;
        }

        var countryId = (int)NextId();
        db.Countries.Add(new Country(countryId, TwoLetterCode(countryId), "دولة", "Country", "JOD", "+962", "Asia/Amman"));
        await db.SaveChangesAsync();
        _sharedCountryId = countryId;
        return countryId;
    }

    /// <summary>Seeds a course/level/age-group/teacher/student under the one
    /// shared country, two already-startable sessions, and — if requested — an
    /// Active subscription with enough balance for one session.</summary>
    private async Task<Scenario> SeedScenarioAsync(MvTeachesDbContext db, Instant now, bool enrollInOriginal = true, bool withSubscription = false)
    {
        var countryId = await GetOrSeedCountryAsync(db);
        var courseId = NextId();
        var levelId = (int)NextId();
        var ageGroupId = (int)NextId();
        var teacherUserId = await CreateUserAsync(db, "teacher");
        var studentUserId = await CreateUserAsync(db, "student");

        db.Courses.Add(new Course("C" + courseId, "دورة", "Course"));
        db.Levels.Add(new Level(levelId, "L" + levelId, "مستوى", "Level", levelId));
        db.AgeGroups.Add(new AgeGroup(ageGroupId, "A" + ageGroupId, 5, 17, true));
        var teacher = new Teacher(teacherUserId, "Teacher", "Asia/Amman");
        db.Teachers.Add(teacher);
        var student = new Student(countryId, "Student", new LocalDate(2015, 1, 1), studentUserId);
        db.Students.Add(student);
        await db.SaveChangesAsync();

        // The original already started (immediately Join-able — D-83 has no
        // upper bound on a late Join). The replacement is in the FUTURE
        // relative to `now` — owner correction (2026-08-28): ApproveReplacementLessonAsync
        // now rejects a replacement that isn't a future session, since this
        // method is reachable from a student's own compensation request, not
        // only a trusted admin. Different calendar day than the original, so
        // no_teacher_overlap (a real EXCLUDE constraint) is trivially satisfied.
        var original = new ClassSession(countryId, null, courseId, levelId, ageGroupId, teacher.Id,
            now.Minus(Duration.FromMinutes(130)), now.Minus(Duration.FromMinutes(70)), "Asia/Amman", "10:00", SessionType.Group, 4, now);
        var replacement = new ClassSession(countryId, null, courseId, levelId, ageGroupId, teacher.Id,
            now.Plus(Duration.FromDays(1)), now.Plus(Duration.FromDays(1)).Plus(Duration.FromMinutes(60)), "Asia/Amman", "12:00", SessionType.Group, 4, now);
        db.ClassSessions.AddRange(original, replacement);
        await db.SaveChangesAsync();

        if (enrollInOriginal)
        {
            db.SessionEnrollments.Add(new SessionEnrollment(original.Id, student.Id, ageGroupId, studentUserId, now));
            await db.SaveChangesAsync();
        }

        if (withSubscription)
        {
            var subscription = new Subscription(student.Id, countryId, courseId, levelId,
                new MVTeaches.Domain.Common.Money(100m, "JOD"), null, 10, 60,
                new LocalDate(2026, 1, 1), 90, SubscriptionOrigin.SelfPurchase, studentUserId, null);
            subscription.Activate();
            db.Subscriptions.Add(subscription);
            await db.SaveChangesAsync();

            db.EntitlementLedgerEntries.Add(EntitlementLedgerEntry.ForPurchase(
                student.Id, subscription.Id, courseId, levelId, 60, paymentId: NextId(), studentUserId, now.Minus(Duration.FromDays(1))));
            await db.SaveChangesAsync();
        }

        return new Scenario(original.Id, replacement.Id, student.Id, studentUserId, ageGroupId, original.DurationMinutes,
            replacement.StartsAtUtc);
    }

    private IEnrollmentService CreateEnrollmentService(MvTeachesDbContext db, Instant now) => new EnrollmentService(db, new FakeClock(now));
    private IJoinAttendanceService CreateJoinService(MvTeachesDbContext db, Instant now) => new JoinAttendanceService(db, new FakeClock(now));

    // ---- Case 1: reschedule an unattended lesson ----

    [Fact]
    public async Task Rescheduling_an_unattended_lesson_moves_the_enrollment_with_no_balance_change()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var s = await SeedScenarioAsync(db, now);

        var result = await CreateEnrollmentService(db, now).RescheduleUnattendedEnrollmentAsync(
            s.OriginalSessionId, s.ReplacementSessionId, s.StudentId, NextId(), CancellationToken.None);

        Assert.Equal(RescheduleOutcome.Rescheduled, result.Outcome);

        await using var verifyDb = _fixture.CreateContext();
        var originalEnrollment = await verifyDb.SessionEnrollments.SingleAsync(e => e.SessionId == s.OriginalSessionId && e.StudentId == s.StudentId);
        Assert.Equal(EnrollmentState.Transferred, originalEnrollment.State);
        Assert.True(await verifyDb.SessionEnrollments.AnyAsync(
            e => e.SessionId == s.ReplacementSessionId && e.StudentId == s.StudentId && e.State == EnrollmentState.Active));
        Assert.Equal(0, await verifyDb.EntitlementLedgerEntries.CountAsync(l => l.StudentId == s.StudentId));
    }

    [Fact]
    public async Task Rescheduling_is_rejected_if_the_original_was_already_consumed()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var s = await SeedScenarioAsync(db, now);
        db.AttendanceRecords.Add(new AttendanceRecord(s.OriginalSessionId, s.StudentId, s.StudentUserId, now, isPresent: true));
        await db.SaveChangesAsync();

        var result = await CreateEnrollmentService(db, now).RescheduleUnattendedEnrollmentAsync(
            s.OriginalSessionId, s.ReplacementSessionId, s.StudentId, NextId(), CancellationToken.None);

        Assert.Equal(RescheduleOutcome.OriginalSessionAlreadyConsumed, result.Outcome);

        await using var verifyDb = _fixture.CreateContext();
        var originalEnrollment = await verifyDb.SessionEnrollments.SingleAsync(e => e.SessionId == s.OriginalSessionId && e.StudentId == s.StudentId);
        Assert.Equal(EnrollmentState.Active, originalEnrollment.State); // untouched
    }

    [Fact]
    public async Task Rescheduling_with_no_original_enrollment_is_rejected()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var s = await SeedScenarioAsync(db, now, enrollInOriginal: false);

        var result = await CreateEnrollmentService(db, now).RescheduleUnattendedEnrollmentAsync(
            s.OriginalSessionId, s.ReplacementSessionId, s.StudentId, NextId(), CancellationToken.None);

        Assert.Equal(RescheduleOutcome.OriginalEnrollmentNotFound, result.Outcome);
    }

    [Fact]
    public async Task Rescheduling_to_the_same_session_is_rejected()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var s = await SeedScenarioAsync(db, now);

        var result = await CreateEnrollmentService(db, now).RescheduleUnattendedEnrollmentAsync(
            s.OriginalSessionId, s.OriginalSessionId, s.StudentId, NextId(), CancellationToken.None);

        Assert.Equal(RescheduleOutcome.ReplacementSessionIsTheSameSession, result.Outcome);
    }

    // ---- Case 2: approve a replacement lesson for an affected, already-consumed session ----

    [Fact]
    public async Task Approving_a_replacement_lets_the_student_join_it_for_free_with_no_second_deduction()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var s = await SeedScenarioAsync(db, now, withSubscription: true);

        // The student actually Joins the original session first — a real consumption.
        var joinOriginal = await CreateJoinService(db, now).JoinAsync(
            new JoinAttendanceRequest(s.OriginalSessionId, s.StudentId, s.StudentUserId), CancellationToken.None);
        Assert.Equal(JoinOutcome.Recorded, joinOriginal.Outcome);

        var approve = await CreateEnrollmentService(db, now).ApproveReplacementLessonAsync(
            s.OriginalSessionId, s.ReplacementSessionId, s.StudentId, NextId(), CancellationToken.None);
        Assert.Equal(ApproveReplacementOutcome.Approved, approve.Outcome);

        // The replacement is a future session at approval time (now) — simulate
        // time passing to when it actually starts before joining it.
        var atReplacementTime = s.ReplacementStartsAtUtc;
        var joinReplacement = await CreateJoinService(db, atReplacementTime).JoinAsync(
            new JoinAttendanceRequest(s.ReplacementSessionId, s.StudentId, s.StudentUserId), CancellationToken.None);
        Assert.Equal(JoinOutcome.Recorded, joinReplacement.Outcome);

        await using var verifyDb = _fixture.CreateContext();
        Assert.True(await verifyDb.AttendanceRecords.AnyAsync(a => a.SessionId == s.ReplacementSessionId && a.StudentId == s.StudentId));
        // No ledger entry at all for the replacement session — not a second deduction, not a credit either.
        Assert.Equal(0, await verifyDb.EntitlementLedgerEntries.CountAsync(l => l.SessionId == s.ReplacementSessionId));
        // The ORIGINAL consumption is untouched — exactly one Consumption entry, for the original session only.
        Assert.Equal(1, await verifyDb.EntitlementLedgerEntries.CountAsync(
            l => l.StudentId == s.StudentId && l.Reason == LedgerReason.Consumption));
        Assert.True(await verifyDb.EntitlementLedgerEntries.AnyAsync(
            l => l.SessionId == s.OriginalSessionId && l.Reason == LedgerReason.Consumption));
    }

    [Fact]
    public async Task A_replacement_lesson_is_usable_exactly_once()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var s = await SeedScenarioAsync(db, now, withSubscription: true);

        await CreateJoinService(db, now).JoinAsync(new JoinAttendanceRequest(s.OriginalSessionId, s.StudentId, s.StudentUserId), CancellationToken.None);
        await CreateEnrollmentService(db, now).ApproveReplacementLessonAsync(s.OriginalSessionId, s.ReplacementSessionId, s.StudentId, NextId(), CancellationToken.None);

        var atReplacementTime = s.ReplacementStartsAtUtc;
        var first = await CreateJoinService(db, atReplacementTime).JoinAsync(new JoinAttendanceRequest(s.ReplacementSessionId, s.StudentId, s.StudentUserId), CancellationToken.None);
        var second = await CreateJoinService(db, atReplacementTime).JoinAsync(new JoinAttendanceRequest(s.ReplacementSessionId, s.StudentId, s.StudentUserId), CancellationToken.None);

        Assert.Equal(JoinOutcome.Recorded, first.Outcome);
        Assert.Equal(JoinOutcome.AlreadyRecorded, second.Outcome);

        await using var verifyDb = _fixture.CreateContext();
        Assert.Equal(1, await verifyDb.AttendanceRecords.CountAsync(a => a.SessionId == s.ReplacementSessionId && a.StudentId == s.StudentId));
        Assert.Equal(0, await verifyDb.EntitlementLedgerEntries.CountAsync(l => l.SessionId == s.ReplacementSessionId));
    }

    [Fact]
    public async Task Approving_a_replacement_is_rejected_if_the_original_was_never_consumed()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var s = await SeedScenarioAsync(db, now);

        var result = await CreateEnrollmentService(db, now).ApproveReplacementLessonAsync(
            s.OriginalSessionId, s.ReplacementSessionId, s.StudentId, NextId(), CancellationToken.None);

        Assert.Equal(ApproveReplacementOutcome.OriginalNotYetConsumed, result.Outcome);

        await using var verifyDb = _fixture.CreateContext();
        Assert.False(await verifyDb.SessionEnrollments.AnyAsync(e => e.SessionId == s.ReplacementSessionId && e.StudentId == s.StudentId));
    }

    [Fact]
    public async Task Approving_a_replacement_to_the_same_session_is_rejected()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var s = await SeedScenarioAsync(db, now, withSubscription: true);
        await CreateJoinService(db, now).JoinAsync(new JoinAttendanceRequest(s.OriginalSessionId, s.StudentId, s.StudentUserId), CancellationToken.None);

        var result = await CreateEnrollmentService(db, now).ApproveReplacementLessonAsync(
            s.OriginalSessionId, s.OriginalSessionId, s.StudentId, NextId(), CancellationToken.None);

        Assert.Equal(ApproveReplacementOutcome.ReplacementSessionIsTheSameSession, result.Outcome);
    }
}
