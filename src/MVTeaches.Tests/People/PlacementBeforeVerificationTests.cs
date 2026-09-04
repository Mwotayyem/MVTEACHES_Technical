using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.People;
using MVTeaches.Domain.People;
using MVTeaches.Domain.Placement;
using MVTeaches.Infrastructure.People;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace MVTeaches.Tests.People;

/// <summary>
/// Owner report 2026-09-05, from a real Local Staging run. A guardian
/// registered her daughters, they sat the placement test, they saw their
/// results — and their cards still read "awaiting a level", so the exam looked
/// as though it had done nothing.
///
/// <para>What actually happened is an ordering assumption. §8.1's ladder is
/// PendingVerification → PendingLevel → Active, and both level-assigning paths
/// (SubmitAttemptAsync and AssignLevelAsync) promote a student only
/// <c>if (Status == PendingLevel)</c>. Nothing stops a family sitting the test
/// the same evening they register, before the centre has confirmed anything —
/// and when they do, the level IS written but the status stays
/// PendingVerification. Confirming the registration afterwards then moved them
/// FORWARD onto PendingLevel, a rung they were already past, which is the
/// literal string the guardian was reading. It could never clear, because a
/// placement test does not run twice.</para>
///
/// <para>Verification now confirms the family's details and nothing else: if a
/// level already exists, the student lands on Active where they belong.</para>
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class PlacementBeforeVerificationTests
{
    private readonly TestDatabaseFixture _fixture;
    private static long _idSeed = 99_000_000;

    public PlacementBeforeVerificationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static long NextId() => Interlocked.Increment(ref _idSeed);

    private static string TwoLetterCode(long seed)
    {
        var n = (int)(seed % 676);
        return string.Concat((char)('A' + n / 26), (char)('A' + n % 26));
    }

    private static async Task<int> SeedCountryAsync(MvTeachesDbContext db)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var countryId = (int)NextId();
            db.Countries.Add(new MVTeaches.Domain.Catalog.Country(countryId, TwoLetterCode(countryId),
                "دولة", "Country", "JOD", "+962", "Asia/Amman"));
            try
            {
                await db.SaveChangesAsync();
                return countryId;
            }
            catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
            {
                db.ChangeTracker.Clear();
            }
        }

        throw new InvalidOperationException("Could not find a free 2-letter country code after 10 attempts.");
    }

    private static IStudentAdmissionService CreateService(MvTeachesDbContext db) =>
        new StudentAdmissionService(db, null!, new FakeClock(SystemClock.Instance.GetCurrentInstant()));

    /// <summary>A child registered and placed, but not yet verified — exactly
    /// the order the owner's family went in.</summary>
    private static async Task<(long StudentId, long CourseId, int LevelId)> SeedPlacedButUnverifiedAsync(
        MvTeachesDbContext db)
    {
        var countryId = await SeedCountryAsync(db);

        var course = new MVTeaches.Domain.Catalog.Course($"PBV-{NextId()}", "دورة", "Course");
        db.Courses.Add(course);
        var levelId = (int)NextId();
        db.Levels.Add(new MVTeaches.Domain.Catalog.Level(levelId, $"PB{levelId}", "مستوى", "Level", levelId));
        await db.SaveChangesAsync();

        var student = new Student(countryId, "Placed Before Verified", new LocalDate(2013, 2, 2), userId: null);
        db.Students.Add(student);
        await db.SaveChangesAsync();

        // What SubmitAttemptAsync writes: the level, by the scoring engine.
        db.StudentLevels.Add(new StudentLevel(student.Id, course.Id, levelId, 1L, AssignedByRole.System,
            LevelAssignmentSource.PlacementTest, null, null, SystemClock.Instance.GetCurrentInstant()));
        await db.SaveChangesAsync();

        return (student.Id, course.Id, levelId);
    }

    /// <summary>The owner's bug, stated directly: confirming the registration
    /// of a child who has already been placed must not announce that she is
    /// awaiting a level.</summary>
    [Fact]
    public async Task Verifying_an_already_placed_student_does_not_send_them_back_to_awaiting_a_level()
    {
        await using var db = _fixture.CreateContext();
        var (studentId, _, _) = await SeedPlacedButUnverifiedAsync(db);
        var admissions = CreateService(db);

        await admissions.VerifyStudentAsync(studentId, CancellationToken.None);

        db.ChangeTracker.Clear();
        var student = await db.Students.SingleAsync(s => s.Id == studentId);
        Assert.NotEqual(StudentStatus.PendingLevel, student.Status);
        Assert.Equal(StudentStatus.Active, student.Status);
    }

    /// <summary>The ordinary order is untouched: a child verified BEFORE any
    /// placement still lands on PendingLevel, because for her that is true.</summary>
    [Fact]
    public async Task Verifying_a_student_with_no_level_still_leaves_them_awaiting_one()
    {
        await using var db = _fixture.CreateContext();
        var countryId = await SeedCountryAsync(db);
        var student = new Student(countryId, "Verified First", new LocalDate(2013, 3, 3), userId: null);
        db.Students.Add(student);
        await db.SaveChangesAsync();

        var admissions = CreateService(db);
        await admissions.VerifyStudentAsync(student.Id, CancellationToken.None);

        db.ChangeTracker.Clear();
        var reloaded = await db.Students.SingleAsync(s => s.Id == student.Id);
        Assert.Equal(StudentStatus.PendingLevel, reloaded.Status);
    }

    /// <summary>And the ladder still runs the ordinary way round: verified,
    /// then placed by an admin, ends Active. Asserted so the fix above cannot
    /// be mistaken for the only route to Active.</summary>
    [Fact]
    public async Task Verified_then_placed_still_ends_active()
    {
        await using var db = _fixture.CreateContext();
        var countryId = await SeedCountryAsync(db);

        var course = new MVTeaches.Domain.Catalog.Course($"PBV-{NextId()}", "دورة", "Course");
        db.Courses.Add(course);
        var levelId = (int)NextId();
        db.Levels.Add(new MVTeaches.Domain.Catalog.Level(levelId, $"PB{levelId}", "مستوى", "Level", levelId));
        var student = new Student(countryId, "Ordinary Order", new LocalDate(2013, 4, 4), userId: null);
        db.Students.Add(student);
        await db.SaveChangesAsync();

        var admissions = CreateService(db);
        await admissions.VerifyStudentAsync(student.Id, CancellationToken.None);
        await admissions.AssignLevelAsync(student.Id, course.Id, levelId, 1L, "interview", CancellationToken.None);

        db.ChangeTracker.Clear();
        var reloaded = await db.Students.SingleAsync(s => s.Id == student.Id);
        Assert.Equal(StudentStatus.Active, reloaded.Status);
    }

    /// <summary>Verification stays a no-op for anyone already past it — an
    /// admin pressing the button twice must not disturb a running student.</summary>
    [Fact]
    public async Task Verifying_twice_changes_nothing_the_second_time()
    {
        await using var db = _fixture.CreateContext();
        var (studentId, _, _) = await SeedPlacedButUnverifiedAsync(db);
        var admissions = CreateService(db);

        await admissions.VerifyStudentAsync(studentId, CancellationToken.None);
        await admissions.VerifyStudentAsync(studentId, CancellationToken.None);

        db.ChangeTracker.Clear();
        var student = await db.Students.SingleAsync(s => s.Id == studentId);
        Assert.Equal(StudentStatus.Active, student.Status);
    }
}
