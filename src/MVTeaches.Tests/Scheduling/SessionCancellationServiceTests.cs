using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Scheduling;
using MVTeaches.Domain.Attendance;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.People;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Infrastructure.Persistence;
using MVTeaches.Infrastructure.Scheduling;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace MVTeaches.Tests.Scheduling;

/// <summary>
/// D-20 ("no double makeup" — a direct replacement transfers the enrollment
/// with no ledger movement; a plain cancellation is left for IMakeUpCreditService,
/// D-19) exercised against a real PostgreSQL database. ClassSession.Cancel/
/// CancelAndReplace have existed at the domain layer since an earlier pass but
/// were never wired to anything before this.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class SessionCancellationServiceTests
{
    private readonly TestDatabaseFixture _fixture;
    private static long _idSeed = 50_000_000;

    public SessionCancellationServiceTests(TestDatabaseFixture fixture) => _fixture = fixture;

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

    private async Task<(int CountryId, long CourseId, int LevelId, int AgeGroupId, long TeacherId)> SeedCatalogAsync(MvTeachesDbContext db)
    {
        var countryId = (int)NextId();
        var courseId = NextId();
        var levelId = (int)NextId();
        var ageGroupId = (int)NextId();
        var teacherUserId = await CreateUserAsync(db, "teacher");

        db.Countries.Add(new Country(countryId, TwoLetterCode(countryId), "دولة", "Country", "JOD", "+962", "Asia/Amman"));
        db.Courses.Add(new Course("C" + courseId, "دورة", "Course"));
        db.Levels.Add(new Level(levelId, "L" + levelId, "مستوى", "Level", levelId));
        db.AgeGroups.Add(new AgeGroup(ageGroupId, "A" + ageGroupId, 5, 17, true));
        var teacher = new Teacher(teacherUserId, "Teacher", "Asia/Amman");
        db.Teachers.Add(teacher);
        await db.SaveChangesAsync();

        return (countryId, courseId, levelId, ageGroupId, teacher.Id);
    }

    /// <summary>daysFromNow lets two sessions for the SAME teacher coexist —
    /// no_teacher_overlap (a real EXCLUDE constraint) correctly rejects two
    /// sessions at the same time for one teacher, so a replacement session
    /// must be scheduled at a different time, exactly as a real admin would.</summary>
    private static ClassSession NewSession(int countryId, long courseId, int levelId, int ageGroupId, long teacherId,
        int capacity, Instant now, int daysFromNow = 1) =>
        new(countryId, null, courseId, levelId, ageGroupId, teacherId,
            now.Plus(Duration.FromDays(daysFromNow)), now.Plus(Duration.FromDays(daysFromNow)).Plus(Duration.FromHours(1)),
            "Asia/Amman", "10:00", SessionType.Group, capacity, now);

    private async Task<long> SeedStudentAsync(MvTeachesDbContext db, int countryId)
    {
        var studentUserId = await CreateUserAsync(db, "student");
        var student = new Student(countryId, "Student", new LocalDate(2015, 1, 1), studentUserId);
        db.Students.Add(student);
        await db.SaveChangesAsync();
        return student.Id;
    }

    private SessionCancellationService CreateService(MvTeachesDbContext db, Instant now) =>
        new(db, new EnrollmentService(db, new FakeClock(now)));

    [Fact]
    public async Task Cancelling_with_no_replacement_cancels_unconsumed_enrollments()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var (countryId, courseId, levelId, ageGroupId, teacherId) = await SeedCatalogAsync(db);
        var session = NewSession(countryId, courseId, levelId, ageGroupId, teacherId, 4, now);
        db.ClassSessions.Add(session);
        await db.SaveChangesAsync();

        var studentId = await SeedStudentAsync(db, countryId);
        db.SessionEnrollments.Add(new SessionEnrollment(session.Id, studentId, ageGroupId, studentId, now));
        await db.SaveChangesAsync();

        var result = await CreateService(db, now).CancelAsync(session.Id, "teacher sick", cancelledByUserId: NextId(),
            replacementSessionId: null, CancellationToken.None);

        Assert.Equal(CancelSessionOutcome.Cancelled, result.Outcome);
        Assert.Equal(1, result.EnrollmentsMovedOrCancelled);
        Assert.Equal(0, result.EnrollmentsLeftUntouchedBecauseAlreadyConsumed);

        await using var verifyDb = _fixture.CreateContext();
        var refreshedSession = await verifyDb.ClassSessions.AsNoTracking().FirstAsync(s => s.Id == session.Id);
        Assert.Equal(ClassSessionStatus.Cancelled, refreshedSession.Status);
        var enrollment = await verifyDb.SessionEnrollments.SingleAsync(e => e.SessionId == session.Id && e.StudentId == studentId);
        Assert.Equal(EnrollmentState.Cancelled, enrollment.State);
    }

    [Fact]
    public async Task Cancelling_with_a_replacement_transfers_unconsumed_enrollments_with_no_ledger_movement()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var (countryId, courseId, levelId, ageGroupId, teacherId) = await SeedCatalogAsync(db);
        var original = NewSession(countryId, courseId, levelId, ageGroupId, teacherId, 4, now);
        var replacement = NewSession(countryId, courseId, levelId, ageGroupId, teacherId, 4, now, daysFromNow: 2);
        db.ClassSessions.AddRange(original, replacement);
        await db.SaveChangesAsync();

        var studentId = await SeedStudentAsync(db, countryId);
        db.SessionEnrollments.Add(new SessionEnrollment(original.Id, studentId, ageGroupId, studentId, now));
        await db.SaveChangesAsync();

        var result = await CreateService(db, now).CancelAsync(original.Id, "cancelled, moved to another group",
            cancelledByUserId: NextId(), replacementSessionId: replacement.Id, CancellationToken.None);

        Assert.Equal(CancelSessionOutcome.Cancelled, result.Outcome);
        Assert.Equal(1, result.EnrollmentsMovedOrCancelled);
        Assert.Equal(0, result.EnrollmentsThatCouldNotBeMovedToReplacement);

        await using var verifyDb = _fixture.CreateContext();
        var refreshedOriginal = await verifyDb.ClassSessions.AsNoTracking().FirstAsync(s => s.Id == original.Id);
        Assert.Equal(ClassSessionStatus.Cancelled, refreshedOriginal.Status);
        Assert.Equal(replacement.Id, refreshedOriginal.ReplacedBySessionId);

        var originalEnrollment = await verifyDb.SessionEnrollments.SingleAsync(e => e.SessionId == original.Id && e.StudentId == studentId);
        Assert.Equal(EnrollmentState.Transferred, originalEnrollment.State);

        Assert.True(await verifyDb.SessionEnrollments.AnyAsync(
            e => e.SessionId == replacement.Id && e.StudentId == studentId && e.State == EnrollmentState.Active));

        // D-20: the entitlement transfers — never a ledger movement for the move itself.
        Assert.Equal(0, await verifyDb.EntitlementLedgerEntries.CountAsync(l => l.StudentId == studentId));
    }

    [Fact]
    public async Task An_already_consumed_enrollment_is_left_untouched()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var (countryId, courseId, levelId, ageGroupId, teacherId) = await SeedCatalogAsync(db);
        var session = NewSession(countryId, courseId, levelId, ageGroupId, teacherId, 4, now);
        db.ClassSessions.Add(session);
        await db.SaveChangesAsync();

        var studentId = await SeedStudentAsync(db, countryId);
        db.SessionEnrollments.Add(new SessionEnrollment(session.Id, studentId, ageGroupId, studentId, now));
        // Simulate the student already having pressed Join before the problem surfaced.
        db.AttendanceRecords.Add(new AttendanceRecord(session.Id, studentId, studentId, now));
        await db.SaveChangesAsync();

        var result = await CreateService(db, now).CancelAsync(session.Id, "teacher's connection failed mid-session",
            cancelledByUserId: NextId(), replacementSessionId: null, CancellationToken.None);

        Assert.Equal(CancelSessionOutcome.Cancelled, result.Outcome);
        Assert.Equal(0, result.EnrollmentsMovedOrCancelled);
        Assert.Equal(1, result.EnrollmentsLeftUntouchedBecauseAlreadyConsumed);

        await using var verifyDb = _fixture.CreateContext();
        var enrollment = await verifyDb.SessionEnrollments.SingleAsync(e => e.SessionId == session.Id && e.StudentId == studentId);
        Assert.Equal(EnrollmentState.Active, enrollment.State); // untouched — admin decides separately (IMakeUpCreditService)
    }

    [Fact]
    public async Task Cancelling_a_nonexistent_session_is_reported_not_thrown()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();

        var result = await CreateService(db, now).CancelAsync(NextId(), "n/a", NextId(), null, CancellationToken.None);

        Assert.Equal(CancelSessionOutcome.SessionNotFound, result.Outcome);
    }

    [Fact]
    public async Task Cancelling_an_already_cancelled_session_is_rejected()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var (countryId, courseId, levelId, ageGroupId, teacherId) = await SeedCatalogAsync(db);
        var session = NewSession(countryId, courseId, levelId, ageGroupId, teacherId, 4, now);
        db.ClassSessions.Add(session);
        await db.SaveChangesAsync();

        var service = CreateService(db, now);
        var first = await service.CancelAsync(session.Id, "first", NextId(), null, CancellationToken.None);
        Assert.Equal(CancelSessionOutcome.Cancelled, first.Outcome);

        var second = await service.CancelAsync(session.Id, "second", NextId(), null, CancellationToken.None);
        Assert.Equal(CancelSessionOutcome.NotCancellable, second.Outcome);
    }

    [Fact]
    public async Task A_nonexistent_replacement_session_is_rejected()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var (countryId, courseId, levelId, ageGroupId, teacherId) = await SeedCatalogAsync(db);
        var session = NewSession(countryId, courseId, levelId, ageGroupId, teacherId, 4, now);
        db.ClassSessions.Add(session);
        await db.SaveChangesAsync();

        var result = await CreateService(db, now).CancelAsync(session.Id, "x", NextId(), NextId(), CancellationToken.None);

        Assert.Equal(CancelSessionOutcome.ReplacementSessionNotFound, result.Outcome);
    }

    [Fact]
    public async Task A_session_cannot_replace_itself()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var (countryId, courseId, levelId, ageGroupId, teacherId) = await SeedCatalogAsync(db);
        var session = NewSession(countryId, courseId, levelId, ageGroupId, teacherId, 4, now);
        db.ClassSessions.Add(session);
        await db.SaveChangesAsync();

        var result = await CreateService(db, now).CancelAsync(session.Id, "x", NextId(), session.Id, CancellationToken.None);

        Assert.Equal(CancelSessionOutcome.ReplacementSessionIsTheSameSession, result.Outcome);
    }

    [Fact]
    public async Task A_student_who_cannot_fit_in_a_full_replacement_is_counted_not_silently_dropped()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var (countryId, courseId, levelId, ageGroupId, teacherId) = await SeedCatalogAsync(db);
        var original = NewSession(countryId, courseId, levelId, ageGroupId, teacherId, 4, now);
        var fullReplacement = NewSession(countryId, courseId, levelId, ageGroupId, teacherId, 1, now, daysFromNow: 2);
        db.ClassSessions.AddRange(original, fullReplacement);
        await db.SaveChangesAsync();

        var displacedStudentId = await SeedStudentAsync(db, countryId);
        db.SessionEnrollments.Add(new SessionEnrollment(original.Id, displacedStudentId, ageGroupId, displacedStudentId, now));

        // Fill the replacement's only seat with someone else first.
        var otherStudentId = await SeedStudentAsync(db, countryId);
        db.SessionEnrollments.Add(new SessionEnrollment(fullReplacement.Id, otherStudentId, ageGroupId, otherStudentId, now));
        fullReplacement.TryTakeSeat();
        await db.SaveChangesAsync();

        var result = await CreateService(db, now).CancelAsync(original.Id, "x", NextId(), fullReplacement.Id, CancellationToken.None);

        Assert.Equal(CancelSessionOutcome.Cancelled, result.Outcome);
        Assert.Equal(0, result.EnrollmentsMovedOrCancelled);
        Assert.Equal(1, result.EnrollmentsThatCouldNotBeMovedToReplacement);

        await using var verifyDb = _fixture.CreateContext();
        var displacedEnrollment = await verifyDb.SessionEnrollments.SingleAsync(
            e => e.SessionId == original.Id && e.StudentId == displacedStudentId);
        Assert.Equal(EnrollmentState.Active, displacedEnrollment.State); // never marked Transferred — it didn't happen
    }
}
