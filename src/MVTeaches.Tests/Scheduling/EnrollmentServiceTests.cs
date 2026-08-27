using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Scheduling;
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
/// §15.1's atomic conditional UPDATE and §12.2's age-group snapshot,
/// exercised against a real PostgreSQL database. This is the first thing in
/// the whole codebase that lets a student actually get INTO a session at
/// all — without it, IJoinAttendanceService (9 tests, the highest-risk
/// piece per docs/deployment/STATUS.md) had no real front door.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class EnrollmentServiceTests
{
    private readonly TestDatabaseFixture _fixture;
    private static long _idSeed = 45_000_000; // a range distinct from every other test class sharing this DB

    public EnrollmentServiceTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static long NextId() => Interlocked.Increment(ref _idSeed);

    private static string TwoLetterCode(long seed)
    {
        var n = (int)(seed % 676);
        return string.Concat((char)('A' + n / 26), (char)('A' + n % 26));
    }

    private static async Task<long> CreateUserAsync(MvTeachesDbContext db)
    {
        var user = new Infrastructure.Identity.ApplicationUser
        {
            UserName = $"u-{Guid.NewGuid():N}",
            NormalizedUserName = $"U-{Guid.NewGuid():N}",
            Email = $"{Guid.NewGuid():N}@test.mvteaches.local",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<(int CountryId, long CourseId, int LevelId, int AgeGroupId, long TeacherId, long StudentId)>
        SeedCatalogAsync(MvTeachesDbContext db, LocalDate studentDateOfBirth)
    {
        var countryId = (int)NextId();
        var courseId = NextId();
        var levelId = (int)NextId();
        var ageGroupId = (int)NextId();
        var teacherUserId = await CreateUserAsync(db);

        db.Countries.Add(new Country(countryId, TwoLetterCode(countryId), "دولة", "Country", "JOD", "+962", "Asia/Amman"));
        db.Courses.Add(new Course("C" + courseId, "دورة", "Course"));
        db.Levels.Add(new Level(levelId, "L" + levelId, "مستوى", "Level", levelId));
        db.AgeGroups.Add(new AgeGroup(ageGroupId, "A" + ageGroupId, 5, 17, true)); // covers the test student's age
        var teacher = new Teacher(teacherUserId, "Teacher", "Asia/Amman");
        db.Teachers.Add(teacher);
        var student = new Student(countryId, "Student", studentDateOfBirth);
        db.Students.Add(student);
        await db.SaveChangesAsync();

        return (countryId, courseId, levelId, ageGroupId, teacher.Id, student.Id);
    }

    private static ClassSession CreateFutureSession(int countryId, long? recurringId, long courseId, int levelId,
        int ageGroupId, long teacherId, int capacity, Instant now) =>
        new(countryId, recurringId, courseId, levelId, ageGroupId, teacherId,
            now.Plus(Duration.FromDays(1)), now.Plus(Duration.FromDays(1)).Plus(Duration.FromHours(1)),
            "Asia/Amman", "10:00", SessionType.Group, capacity, now);

    [Fact]
    public async Task Enrolling_a_student_creates_a_row_and_increments_the_seat_count()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var (countryId, courseId, levelId, ageGroupId, teacherId, studentId) =
            await SeedCatalogAsync(db, new LocalDate(2015, 1, 1));

        var session = CreateFutureSession(countryId, null, courseId, levelId, ageGroupId, teacherId, capacity: 4, now);
        db.ClassSessions.Add(session);
        await db.SaveChangesAsync();

        var service = new EnrollmentService(db, new FakeClock(now));
        var result = await service.EnrollInSessionAsync(session.Id, studentId, enrolledByUserId: NextId(), CancellationToken.None);

        Assert.Equal(EnrollOutcome.Enrolled, result.Outcome);
        var enrollment = await db.SessionEnrollments.SingleAsync(e => e.SessionId == session.Id && e.StudentId == studentId);
        Assert.Equal(EnrollmentState.Active, enrollment.State);

        var refreshedSession = await db.ClassSessions.AsNoTracking().FirstAsync(s => s.Id == session.Id);
        Assert.Equal(1, refreshedSession.SeatsTaken);
    }

    [Fact]
    public async Task Enrolling_the_same_student_twice_is_reported_as_already_enrolled_not_a_duplicate_row()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var (countryId, courseId, levelId, ageGroupId, teacherId, studentId) =
            await SeedCatalogAsync(db, new LocalDate(2015, 1, 1));

        var session = CreateFutureSession(countryId, null, courseId, levelId, ageGroupId, teacherId, capacity: 4, now);
        db.ClassSessions.Add(session);
        await db.SaveChangesAsync();

        var service = new EnrollmentService(db, new FakeClock(now));
        var first = await service.EnrollInSessionAsync(session.Id, studentId, NextId(), CancellationToken.None);
        Assert.Equal(EnrollOutcome.Enrolled, first.Outcome);

        var second = await service.EnrollInSessionAsync(session.Id, studentId, NextId(), CancellationToken.None);
        Assert.Equal(EnrollOutcome.AlreadyEnrolled, second.Outcome);

        Assert.Equal(1, await db.SessionEnrollments.CountAsync(e => e.SessionId == session.Id && e.StudentId == studentId));
        var refreshedSession = await db.ClassSessions.AsNoTracking().FirstAsync(s => s.Id == session.Id);
        Assert.Equal(1, refreshedSession.SeatsTaken); // the second attempt must not double-count the seat
    }

    [Fact]
    public async Task A_full_session_rejects_a_new_enrollment()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var (countryId, courseId, levelId, ageGroupId, teacherId, _) =
            await SeedCatalogAsync(db, new LocalDate(2015, 1, 1));

        var session = CreateFutureSession(countryId, null, courseId, levelId, ageGroupId, teacherId, capacity: 1, now);
        db.ClassSessions.Add(session);
        await db.SaveChangesAsync();

        var service = new EnrollmentService(db, new FakeClock(now));
        var firstStudent = (await SeedCatalogAsync(db, new LocalDate(2015, 1, 1))).StudentId;
        var secondStudent = (await SeedCatalogAsync(db, new LocalDate(2015, 1, 1))).StudentId;

        var first = await service.EnrollInSessionAsync(session.Id, firstStudent, NextId(), CancellationToken.None);
        Assert.Equal(EnrollOutcome.Enrolled, first.Outcome);

        var second = await service.EnrollInSessionAsync(session.Id, secondStudent, NextId(), CancellationToken.None);
        Assert.Equal(EnrollOutcome.SessionFull, second.Outcome);
    }

    [Fact]
    public async Task Bulk_enrolling_into_upcoming_sessions_skips_already_started_ones()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var (countryId, courseId, levelId, ageGroupId, teacherId, studentId) =
            await SeedCatalogAsync(db, new LocalDate(2015, 1, 1));

        var recurringId = NextId(); // stands in for a real RecurringSchedule id — only used as a grouping key here
        var upcoming1 = CreateFutureSession(countryId, recurringId, courseId, levelId, ageGroupId, teacherId, 4, now);
        var upcoming2 = new ClassSession(countryId, recurringId, courseId, levelId, ageGroupId, teacherId,
            now.Plus(Duration.FromDays(8)), now.Plus(Duration.FromDays(8)).Plus(Duration.FromHours(1)),
            "Asia/Amman", "10:00", SessionType.Group, 4, now);
        var alreadyStarted = new ClassSession(countryId, recurringId, courseId, levelId, ageGroupId, teacherId,
            now.Minus(Duration.FromHours(2)), now.Minus(Duration.FromHours(1)),
            "Asia/Amman", "10:00", SessionType.Group, 4, now);
        db.ClassSessions.AddRange(upcoming1, upcoming2, alreadyStarted);
        await db.SaveChangesAsync();

        var service = new EnrollmentService(db, new FakeClock(now));
        var enrolledCount = await service.EnrollInUpcomingSessionsAsync(recurringId, studentId, NextId(), CancellationToken.None);

        Assert.Equal(2, enrolledCount); // only the two future sessions, not the already-started one
        Assert.False(await db.SessionEnrollments.AnyAsync(e => e.SessionId == alreadyStarted.Id));
    }
}
