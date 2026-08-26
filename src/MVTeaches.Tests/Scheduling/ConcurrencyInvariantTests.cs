using Microsoft.EntityFrameworkCore;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.People;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;
using Npgsql;
using Xunit;

namespace MVTeaches.Tests.Scheduling;

/// <summary>
/// Master engineering prompt §28 items 13-14 — database-enforced invariants
/// that must hold even if application code has a bug, because they are
/// physically impossible to violate at the database level (§14.2/§15.1).
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class ConcurrencyInvariantTests
{
    private readonly TestDatabaseFixture _fixture;
    private static long _idSeed = 5_000_000;

    public ConcurrencyInvariantTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static long NextId() => Interlocked.Increment(ref _idSeed);

    private static string TwoLetterCode(long seed)
    {
        var n = (int)(seed % 676);
        return string.Concat((char)('A' + n / 26), (char)('A' + n % 26));
    }

    private async Task<long> CreateUserAsync(MvTeachesDbContext db)
    {
        var user = new ApplicationUser
        {
            UserName = $"u-{Guid.NewGuid():N}",
            NormalizedUserName = $"U-{Guid.NewGuid():N}",
            Email = $"{Guid.NewGuid():N}@test.mvteaches.local",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    [Fact]
    public async Task Teacher_schedule_collision_is_rejected_by_the_database_exclude_constraint()
    {
        await using var db = _fixture.CreateContext();

        var countryId = (int)NextId();
        var courseId = NextId();
        var levelId = (int)NextId();
        var ageGroupId = (int)NextId();
        var teacherUserId = await CreateUserAsync(db);

        db.Countries.Add(new Country(countryId, TwoLetterCode(countryId), "دولة", "Country", "JOD", "+962", "Asia/Amman"));
        db.Courses.Add(new Course("C" + courseId, "دورة", "Course"));
        db.Levels.Add(new Level(levelId, "L" + levelId, "مستوى", "Level", levelId));
        db.AgeGroups.Add(new AgeGroup(ageGroupId, "A" + ageGroupId, 5, 12, true));
        var teacher = new Teacher(teacherUserId, "Teacher", "Asia/Amman");
        db.Teachers.Add(teacher);
        await db.SaveChangesAsync();

        var now = SystemClock.Instance.GetCurrentInstant();
        var start = now.Plus(Duration.FromHours(1));

        var first = new ClassSession(countryId, null, courseId, levelId, ageGroupId, teacher.Id,
            start, start.Plus(Duration.FromMinutes(60)), "Asia/Amman", "18:00", SessionType.Group, 4, now);
        db.ClassSessions.Add(first);
        await db.SaveChangesAsync();

        // Overlaps the first session by 30 minutes, same teacher.
        var overlapStart = start.Plus(Duration.FromMinutes(30));
        var second = new ClassSession(countryId, null, courseId, levelId, ageGroupId, teacher.Id,
            overlapStart, overlapStart.Plus(Duration.FromMinutes(60)), "Asia/Amman", "18:30", SessionType.Group, 4, now);
        db.ClassSessions.Add(second);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal("23P01", ((PostgresException)ex.InnerException!).SqlState); // exclusion_violation
    }

    [Fact]
    public async Task Non_overlapping_sessions_for_the_same_teacher_are_allowed()
    {
        await using var db = _fixture.CreateContext();

        var countryId = (int)NextId();
        var courseId = NextId();
        var levelId = (int)NextId();
        var ageGroupId = (int)NextId();
        var teacherUserId = await CreateUserAsync(db);

        db.Countries.Add(new Country(countryId, TwoLetterCode(countryId), "دولة", "Country", "JOD", "+962", "Asia/Amman"));
        db.Courses.Add(new Course("C" + courseId, "دورة", "Course"));
        db.Levels.Add(new Level(levelId, "L" + levelId, "مستوى", "Level", levelId));
        db.AgeGroups.Add(new AgeGroup(ageGroupId, "A" + ageGroupId, 5, 12, true));
        var teacher = new Teacher(teacherUserId, "Teacher", "Asia/Amman");
        db.Teachers.Add(teacher);
        await db.SaveChangesAsync();

        var now = SystemClock.Instance.GetCurrentInstant();
        var start = now.Plus(Duration.FromHours(2));

        db.ClassSessions.Add(new ClassSession(countryId, null, courseId, levelId, ageGroupId, teacher.Id,
            start, start.Plus(Duration.FromMinutes(60)), "Asia/Amman", "19:00", SessionType.Group, 4, now));
        // Starts exactly when the first one ends — back-to-back, not overlapping.
        db.ClassSessions.Add(new ClassSession(countryId, null, courseId, levelId, ageGroupId, teacher.Id,
            start.Plus(Duration.FromMinutes(60)), start.Plus(Duration.FromMinutes(120)), "Asia/Amman", "20:00", SessionType.Group, 4, now));

        await db.SaveChangesAsync(); // must not throw
        Assert.Equal(2, db.ClassSessions.Count(s => s.TeacherId == teacher.Id));
    }

    [Fact]
    public async Task Duplicate_active_enrollment_for_the_same_session_and_student_is_rejected()
    {
        await using var db = _fixture.CreateContext();

        var countryId = (int)NextId();
        var courseId = NextId();
        var levelId = (int)NextId();
        var ageGroupId = (int)NextId();
        var teacherUserId = await CreateUserAsync(db);
        var studentUserId = await CreateUserAsync(db);

        db.Countries.Add(new Country(countryId, TwoLetterCode(countryId), "دولة", "Country", "JOD", "+962", "Asia/Amman"));
        db.Courses.Add(new Course("C" + courseId, "دورة", "Course"));
        db.Levels.Add(new Level(levelId, "L" + levelId, "مستوى", "Level", levelId));
        db.AgeGroups.Add(new AgeGroup(ageGroupId, "A" + ageGroupId, 5, 12, true));
        var teacher = new Teacher(teacherUserId, "Teacher", "Asia/Amman");
        db.Teachers.Add(teacher);
        var student = new Student(countryId, "Student", new LocalDate(2015, 1, 1), studentUserId);
        db.Students.Add(student);
        await db.SaveChangesAsync();

        var now = SystemClock.Instance.GetCurrentInstant();
        var session = new ClassSession(countryId, null, courseId, levelId, ageGroupId, teacher.Id,
            now, now.Plus(Duration.FromMinutes(60)), "Asia/Amman", "17:00", SessionType.Group, 4, now);
        db.ClassSessions.Add(session);
        await db.SaveChangesAsync();

        db.SessionEnrollments.Add(new SessionEnrollment(session.Id, student.Id, ageGroupId, studentUserId, now));
        await db.SaveChangesAsync();

        db.SessionEnrollments.Add(new SessionEnrollment(session.Id, student.Id, ageGroupId, studentUserId, now));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal("23505", ((PostgresException)ex.InnerException!).SqlState); // unique_violation
    }

    [Fact]
    public async Task Payroll_line_cannot_be_recorded_twice_for_the_same_session_in_the_same_period()
    {
        await using var db = _fixture.CreateContext();

        var countryId = (int)NextId();
        var periodId = NextId();
        var teacherId = NextId();
        var sessionId = NextId();

        db.Countries.Add(new Country(countryId, TwoLetterCode(countryId), "دولة", "Country", "JOD", "+962", "Asia/Amman"));
        var period = new MVTeaches.Domain.Payroll.PayrollPeriod(countryId, new LocalDate(2026, 1, 1), new LocalDate(2026, 1, 31));
        db.PayrollPeriods.Add(period);
        await db.SaveChangesAsync();

        db.PayrollLines.Add(new MVTeaches.Domain.Payroll.PayrollLine(period.Id, teacherId, sessionId, 60, 12m, "JOD", 12m));
        await db.SaveChangesAsync();

        db.PayrollLines.Add(new MVTeaches.Domain.Payroll.PayrollLine(period.Id, teacherId, sessionId, 60, 12m, "JOD", 12m));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal("23505", ((PostgresException)ex.InnerException!).SqlState);
    }

    [Fact]
    public async Task The_ledger_cannot_be_updated_or_deleted_even_directly()
    {
        await using var db = _fixture.CreateContext();

        var studentId = NextId();
        var courseId = NextId();
        var levelId = (int)NextId();
        var now = SystemClock.Instance.GetCurrentInstant();

        var entry = MVTeaches.Domain.Ledger.EntitlementLedgerEntry.ForAdminGrant(
            studentId, subscriptionId: NextId(), courseId, levelId, 60, performedByUserId: NextId(), "test grant", now);
        db.EntitlementLedgerEntries.Add(entry);
        await db.SaveChangesAsync();

        // Attempt a raw UPDATE against the append-only table — must be rejected
        // by the trigger even though it bypasses the C# domain model entirely.
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlAsync($"UPDATE entitlement_ledger SET note = 'tampered' WHERE \"Id\" = {entry.Id}"));
        Assert.Equal("P0001", ex.SqlState); // raise_exception
    }
}
