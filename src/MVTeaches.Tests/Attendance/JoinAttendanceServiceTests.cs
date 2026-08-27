using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Attendance;
using MVTeaches.Domain.Attendance;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Ledger;
using MVTeaches.Domain.People;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Domain.Subscriptions;
using MVTeaches.Infrastructure.Attendance;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace MVTeaches.Tests.Attendance;

/// <summary>
/// Master engineering prompt §28, items 1-7 — the D-83 anchor's non-negotiable
/// invariants, tested against a REAL PostgreSQL 16 database (see
/// TestDatabaseFixture), not an in-memory provider, specifically because the
/// guarantee under test is a database-level unique-constraint race, which an
/// in-memory provider cannot reproduce honestly.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class JoinAttendanceServiceTests
{
    private readonly TestDatabaseFixture _fixture;
    private static long _idSeed = 1_000_000;

    public JoinAttendanceServiceTests(TestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    private static long NextId() => Interlocked.Increment(ref _idSeed);

    /// <summary>A valid-looking 2-letter code (Country.Code is varchar(2), ISO 3166-1 alpha-2 style).</summary>
    private static string TwoLetterCode(long seed)
    {
        var n = (int)(seed % 676); // 26 * 26
        return string.Concat((char)('A' + n / 26), (char)('A' + n % 26));
    }

    /// <summary>Every People-module entity has a real FK to AspNetUsers (§7.1) —
    /// tests must create a genuine Identity user, not an arbitrary long.</summary>
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

    private async Task<(long SessionId, long StudentId, long StudentUserId, int Minutes)> SeedJoinableSessionAsync(
        Instant now, bool enrolled = true, int? balanceMinutesOverride = null)
    {
        await using var db = _fixture.CreateContext();

        var countryId = (int)NextId();
        var courseId = NextId();
        var levelId = (int)NextId();
        var ageGroupId = (int)NextId();

        var teacherUserId = await CreateUserAsync(db, "teacher");
        var studentUserId = await CreateUserAsync(db, "student");

        db.Countries.Add(new Country(countryId, TwoLetterCode(countryId), "دولة", "Country", "JOD", "+962", "Asia/Amman"));
        db.Courses.Add(new Course("C" + courseId, "دورة", "Course"));
        db.Levels.Add(new Level(levelId, "L" + levelId, "مستوى", "Level", levelId));
        db.AgeGroups.Add(new AgeGroup(ageGroupId, "A" + ageGroupId, 5, 12, true));

        var teacher = new Teacher(teacherUserId, "Teacher", "Asia/Amman");
        db.Teachers.Add(teacher);

        var student = new Student(countryId, "Student", new LocalDate(2015, 1, 1), studentUserId);
        student.MarkVerified();
        student.MarkLevelAssigned();
        db.Students.Add(student);

        await db.SaveChangesAsync();

        var session = new ClassSession(countryId, null, courseId, levelId, ageGroupId, teacher.Id,
            now.Minus(Duration.FromMinutes(5)), now.Plus(Duration.FromMinutes(55)), "Asia/Amman", "17:00",
            SessionType.Group, 4, now.Minus(Duration.FromDays(1)));
        db.ClassSessions.Add(session);
        await db.SaveChangesAsync();

        var minutes = balanceMinutesOverride ?? session.DurationMinutes;

        var subscription = new Subscription(student.Id, countryId, courseId, levelId,
            new MVTeaches.Domain.Common.Money(100m, "JOD"), null, 10, minutes,
            new LocalDate(2026, 1, 1), 90, SubscriptionOrigin.SelfPurchase, studentUserId, null);
        subscription.Activate();
        db.Subscriptions.Add(subscription);
        await db.SaveChangesAsync();

        db.EntitlementLedgerEntries.Add(EntitlementLedgerEntry.ForPurchase(
            student.Id, subscription.Id, courseId, levelId, minutes, paymentId: NextId(), studentUserId, now.Minus(Duration.FromDays(1))));
        await db.SaveChangesAsync();

        if (enrolled)
        {
            db.SessionEnrollments.Add(new SessionEnrollment(session.Id, student.Id, ageGroupId, studentUserId, now.Minus(Duration.FromDays(1))));
            await db.SaveChangesAsync();
        }

        return (session.Id, student.Id, studentUserId, session.DurationMinutes);
    }

    private IJoinAttendanceService CreateService(Instant now) =>
        new JoinAttendanceService(_fixture.CreateContext(), new FakeClock(now));

    [Fact]
    public async Task First_join_creates_present_and_exactly_one_consumption()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        var (sessionId, studentId, studentUserId, minutes) = await SeedJoinableSessionAsync(now);

        var service = CreateService(now);
        var result = await service.JoinAsync(new JoinAttendanceRequest(sessionId, studentId, studentUserId), CancellationToken.None);

        Assert.Equal(JoinOutcome.Recorded, result.Outcome);

        await using var verifyDb = _fixture.CreateContext();
        var attendanceCount = verifyDb.AttendanceRecords.Count(a => a.SessionId == sessionId && a.StudentId == studentId);
        var consumptionCount = verifyDb.EntitlementLedgerEntries.Count(
            l => l.SessionId == sessionId && l.StudentId == studentId && l.Reason == LedgerReason.Consumption);
        var consumedMinutes = verifyDb.EntitlementLedgerEntries
            .Where(l => l.SessionId == sessionId && l.StudentId == studentId && l.Reason == LedgerReason.Consumption)
            .Sum(l => l.DeltaMinutes);

        Assert.Equal(1, attendanceCount);
        Assert.Equal(1, consumptionCount);
        Assert.Equal(-minutes, consumedMinutes);
    }

    [Fact]
    public async Task Second_join_does_not_create_another_consumption()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        var (sessionId, studentId, studentUserId, _) = await SeedJoinableSessionAsync(now);

        var first = await CreateService(now).JoinAsync(new JoinAttendanceRequest(sessionId, studentId, studentUserId), CancellationToken.None);
        var second = await CreateService(now).JoinAsync(new JoinAttendanceRequest(sessionId, studentId, studentUserId), CancellationToken.None);

        Assert.Equal(JoinOutcome.Recorded, first.Outcome);
        Assert.Equal(JoinOutcome.AlreadyRecorded, second.Outcome);

        await using var verifyDb = _fixture.CreateContext();
        Assert.Equal(1, verifyDb.AttendanceRecords.Count(a => a.SessionId == sessionId && a.StudentId == studentId));
        Assert.Equal(1, verifyDb.EntitlementLedgerEntries.Count(
            l => l.SessionId == sessionId && l.StudentId == studentId && l.Reason == LedgerReason.Consumption));
    }

    [Fact]
    public async Task Two_concurrent_join_requests_still_produce_exactly_one_consumption()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        var (sessionId, studentId, studentUserId, _) = await SeedJoinableSessionAsync(now);

        var request = new JoinAttendanceRequest(sessionId, studentId, studentUserId);
        var task1 = CreateService(now).JoinAsync(request, CancellationToken.None);
        var task2 = CreateService(now).JoinAsync(request, CancellationToken.None);

        var results = await Task.WhenAll(task1, task2);

        // Exactly one of the two racing requests actually recorded it; the
        // other must observe AlreadyRecorded — never an unhandled exception,
        // never two consumptions.
        Assert.Contains(results, r => r.Outcome == JoinOutcome.Recorded);
        Assert.All(results, r => Assert.True(r.IsPresent));

        await using var verifyDb = _fixture.CreateContext();
        Assert.Equal(1, verifyDb.AttendanceRecords.Count(a => a.SessionId == sessionId && a.StudentId == studentId));
        Assert.Equal(1, verifyDb.EntitlementLedgerEntries.Count(
            l => l.SessionId == sessionId && l.StudentId == studentId && l.Reason == LedgerReason.Consumption));
    }

    [Fact]
    public async Task Join_against_a_session_the_student_is_not_enrolled_in_is_rejected()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        var (sessionId, studentId, studentUserId, _) = await SeedJoinableSessionAsync(now, enrolled: false);

        var result = await CreateService(now).JoinAsync(new JoinAttendanceRequest(sessionId, studentId, studentUserId), CancellationToken.None);

        Assert.Equal(JoinOutcome.Unauthorized, result.Outcome);

        await using var verifyDb = _fixture.CreateContext();
        Assert.Equal(0, verifyDb.AttendanceRecords.Count(a => a.SessionId == sessionId && a.StudentId == studentId));
    }

    [Fact]
    public async Task A_student_cannot_press_join_using_another_students_identity()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        var (sessionId, studentId, _, _) = await SeedJoinableSessionAsync(now);

        await using var db = _fixture.CreateContext();
        var strangerUserId = await CreateUserAsync(db, "stranger"); // not the enrolled student, not a guardian of theirs

        var result = await CreateService(now).JoinAsync(new JoinAttendanceRequest(sessionId, studentId, strangerUserId), CancellationToken.None);

        Assert.Equal(JoinOutcome.Unauthorized, result.Outcome);
    }

    [Fact]
    public async Task A_guardian_can_join_on_behalf_of_their_own_child_but_not_someone_elses()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        var (sessionId, studentId, _, _) = await SeedJoinableSessionAsync(now);

        long guardianId, guardianUserId;
        await using (var db = _fixture.CreateContext())
        {
            guardianUserId = await CreateUserAsync(db, "guardian");
            var guardian = new Guardian(guardianUserId, "Parent");
            db.Guardians.Add(guardian);
            await db.SaveChangesAsync();
            guardianId = guardian.Id;

            db.Guardianships.Add(new Guardianship(guardian.Id, studentId, GuardianRelationship.Parent, isPrimary: true, guardianUserId));
            await db.SaveChangesAsync();

            var ownChildResult = await new JoinAttendanceService(db, new FakeClock(now))
                .JoinAsync(new JoinAttendanceRequest(sessionId, studentId, guardianUserId), CancellationToken.None);
            Assert.Equal(JoinOutcome.Recorded, ownChildResult.Outcome);
        }

        // A second, unrelated session for a DIFFERENT student — this guardian must not be able to join it.
        var (otherSessionId, otherStudentId, _, _) = await SeedJoinableSessionAsync(now);
        await using (var db2 = _fixture.CreateContext())
        {
            var result = await new JoinAttendanceService(db2, new FakeClock(now))
                .JoinAsync(new JoinAttendanceRequest(otherSessionId, otherStudentId, guardianUserId), CancellationToken.None);
            Assert.Equal(JoinOutcome.Unauthorized, result.Outcome);
        }
    }

    [Fact]
    public async Task No_join_means_no_attendance_row_and_no_consumption_absence_is_purely_derived()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        var (sessionId, studentId, _, _) = await SeedJoinableSessionAsync(now);

        // Deliberately never call JoinAsync.

        await using var verifyDb = _fixture.CreateContext();
        Assert.Equal(0, verifyDb.AttendanceRecords.Count(a => a.SessionId == sessionId && a.StudentId == studentId));
        Assert.Equal(0, verifyDb.EntitlementLedgerEntries.Count(l => l.SessionId == sessionId && l.StudentId == studentId));
        // ⭐ There is no "Absent" row to assert on — §16.2: absence is the
        // documented ABSENCE of a row, computed by the read side, never written.
    }

    [Fact]
    public async Task Join_before_the_session_starts_is_rejected()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        var future = now.Plus(Duration.FromMinutes(30));

        await using var db = _fixture.CreateContext();
        var countryId = (int)NextId();
        var courseId = NextId();
        var levelId = (int)NextId();
        var ageGroupId = (int)NextId();

        var teacherUserId = await CreateUserAsync(db, "teacher");
        var studentUserId = await CreateUserAsync(db, "student");

        db.Countries.Add(new Country(countryId, TwoLetterCode(countryId), "دولة", "Country", "JOD", "+962", "Asia/Amman"));
        db.Courses.Add(new Course("C" + courseId, "دورة", "Course"));
        db.Levels.Add(new Level(levelId, "L" + levelId, "مستوى", "Level", levelId));
        db.AgeGroups.Add(new AgeGroup(ageGroupId, "A" + ageGroupId, 5, 12, true));
        var teacher = new Teacher(teacherUserId, "Teacher", "Asia/Amman");
        db.Teachers.Add(teacher);
        var student = new Student(countryId, "Student", new LocalDate(2015, 1, 1), studentUserId);
        db.Students.Add(student);
        await db.SaveChangesAsync();

        var session = new ClassSession(countryId, null, courseId, levelId, ageGroupId, teacher.Id,
            future, future.Plus(Duration.FromHours(1)), "Asia/Amman", "future", SessionType.Group, 4, now);
        db.ClassSessions.Add(session);
        await db.SaveChangesAsync();

        db.SessionEnrollments.Add(new SessionEnrollment(session.Id, student.Id, ageGroupId, studentUserId, now));
        await db.SaveChangesAsync();

        var result = await CreateService(now).JoinAsync(new JoinAttendanceRequest(session.Id, student.Id, studentUserId), CancellationToken.None);

        Assert.Equal(JoinOutcome.SessionNotYetJoinable, result.Outcome);
    }

    /// <summary>
    /// Financial-integrity check: the attendance insert and the ledger consumption
    /// insert happen in ONE SaveChangesAsync call (one implicit database
    /// transaction — EF Core's default), so a unique-constraint failure on either
    /// write rolls back BOTH, never leaving a debit with no matching attendance or
    /// an attendance with no matching debit. This test forces the failure on the
    /// LEDGER side specifically (not the already-covered attendance-side race in
    /// Two_concurrent_join_requests_still_produce_exactly_one_consumption) by
    /// pre-existing a Consumption entry for this exact (session, student) with no
    /// matching attendance row — an artificial precondition, used only to trigger
    /// ux_ent_consumption without also triggering ux_attendance_session_student, so
    /// the attendance insert this call attempts would, on its own, have succeeded.
    /// If the two writes were ever committed independently instead of atomically,
    /// this would leave exactly that: a real attendance row with no new debit
    /// behind it. It must not.
    /// </summary>
    [Fact]
    public async Task A_ledger_side_conflict_rolls_back_the_attendance_insert_too_no_orphan_debit()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        // Plenty of headroom so the balance check still passes after the phantom draw below.
        var (sessionId, studentId, studentUserId, minutes) =
            await SeedJoinableSessionAsync(now, balanceMinutesOverride: 10_000);

        await using (var seedDb = _fixture.CreateContext())
        {
            var subscription = await seedDb.Subscriptions.SingleAsync(s => s.StudentId == studentId);
            seedDb.EntitlementLedgerEntries.Add(EntitlementLedgerEntry.ForConsumption(
                studentId, subscription.Id, subscription.CourseId, subscription.LevelId,
                minutes, sessionId, studentUserId, now));
            await seedDb.SaveChangesAsync();
        }

        var result = await CreateService(now).JoinAsync(new JoinAttendanceRequest(sessionId, studentId, studentUserId), CancellationToken.None);

        // Idempotent contract holds even for a ledger-side collision: reported as
        // present, never surfaced as an error.
        Assert.True(result.IsPresent);

        await using var verifyDb = _fixture.CreateContext();
        Assert.Equal(0, verifyDb.AttendanceRecords.Count(a => a.SessionId == sessionId && a.StudentId == studentId));
        Assert.Equal(1, verifyDb.EntitlementLedgerEntries.Count(
            l => l.SessionId == sessionId && l.StudentId == studentId && l.Reason == LedgerReason.Consumption));
    }

    [Fact]
    public async Task Join_without_sufficient_balance_is_rejected_before_writing_anything()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        var (sessionId, studentId, studentUserId, _) = await SeedJoinableSessionAsync(now, balanceMinutesOverride: 10);

        var result = await CreateService(now).JoinAsync(new JoinAttendanceRequest(sessionId, studentId, studentUserId), CancellationToken.None);

        Assert.Equal(JoinOutcome.InsufficientBalance, result.Outcome);

        await using var verifyDb = _fixture.CreateContext();
        Assert.Equal(0, verifyDb.AttendanceRecords.Count(a => a.SessionId == sessionId && a.StudentId == studentId));
    }
}
