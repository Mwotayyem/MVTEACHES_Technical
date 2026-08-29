using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Notifications;
using MVTeaches.Domain.People;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Notifications;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace MVTeaches.Tests.Notifications;

/// <summary>Owner decision 2026-08-30 rule 9: "a 5-minute-before reminder
/// (idempotent job)".</summary>
[Collection(nameof(DatabaseCollection))]
public class SessionReminderJobTests
{
    private readonly TestDatabaseFixture _fixture;
    private static long _idSeed = 99_000_000; // a range distinct from every other test class sharing this DB

    public SessionReminderJobTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static long NextId() => Interlocked.Increment(ref _idSeed);

    private static string TwoLetterCode(long seed)
    {
        var n = (int)(seed % 676);
        return string.Concat((char)('A' + n / 26), (char)('A' + n % 26));
    }

    private static int? _sharedCountryId;

    private static async Task<int> GetOrSeedCountryAsync(MvTeachesDbContext db)
    {
        if (_sharedCountryId is { } existing)
        {
            return existing;
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var id = (int)NextId();
            db.Countries.Add(new Country(id, TwoLetterCode(id), "دولة", "Country", "JOD", "+962", "Asia/Amman"));
            try
            {
                await db.SaveChangesAsync();
                _sharedCountryId = id;
                return id;
            }
            catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
            {
                db.ChangeTracker.Clear();
            }
        }

        throw new InvalidOperationException("Could not find a free 2-letter country code after 10 attempts.");
    }

    private static async Task<long> CreateUserAsync(MvTeachesDbContext db)
    {
        var user = new ApplicationUser
        {
            UserName = $"u-{Guid.NewGuid():N}", NormalizedUserName = $"U-{Guid.NewGuid():N}",
            Email = $"{Guid.NewGuid():N}@test.mvteaches.local",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private record Scene(long SessionId, long StudentId, long StudentUserId);

    private static async Task<Scene> SeedEnrolledSessionAsync(MvTeachesDbContext db, Instant now, Duration startsIn, bool linkStudentToUser = true)
    {
        var countryId = await GetOrSeedCountryAsync(db);
        var courseId = NextId();
        var levelId = (int)NextId();
        var ageGroupId = (int)NextId();
        var teacherUserId = await CreateUserAsync(db);

        db.Courses.Add(new Course("C" + courseId, "دورة", "Course"));
        db.Levels.Add(new Level(levelId, "L" + levelId, "مستوى", "Level", levelId));
        db.AgeGroups.Add(new AgeGroup(ageGroupId, "A" + ageGroupId, 5, 12, true));
        var teacher = new Teacher(teacherUserId, "Teacher", "Asia/Amman");
        db.Teachers.Add(teacher);
        await db.SaveChangesAsync();

        var start = now.Plus(startsIn);
        var session = new ClassSession(countryId, null, courseId, levelId, ageGroupId, teacher.Id,
            start, start.Plus(Duration.FromMinutes(60)), "Asia/Amman", "10:00", SessionType.Group, now);
        db.ClassSessions.Add(session);
        await db.SaveChangesAsync();

        var studentUserId = linkStudentToUser ? await CreateUserAsync(db) : (long?)null;
        var student = new Student(countryId, "Student", new LocalDate(2015, 1, 1), studentUserId);
        db.Students.Add(student);
        await db.SaveChangesAsync();

        db.SessionEnrollments.Add(new SessionEnrollment(session.Id, student.Id, ageGroupId, studentUserId ?? NextId(), now));
        await db.SaveChangesAsync();

        return new Scene(session.Id, student.Id, studentUserId ?? 0);
    }

    private static SessionReminderJob CreateJob(MvTeachesDbContext db, Instant now) =>
        new(db, new FakeClock(now), NullLogger<SessionReminderJob>.Instance);

    [Fact]
    public async Task A_session_starting_in_five_minutes_queues_a_reminder_for_its_enrolled_student()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var scene = await SeedEnrolledSessionAsync(db, now, Duration.FromMinutes(5));

        await CreateJob(db, now).SendFiveMinuteRemindersAsync(CancellationToken.None);

        await using var verify = _fixture.CreateContext();
        Assert.True(await verify.NotificationOutboxItems.AnyAsync(
            n => n.Event == NotificationEvent.ZoomLink5Min && n.SessionId == scene.SessionId && n.RecipientUserId == scene.StudentUserId));
    }

    [Fact]
    public async Task Running_the_job_twice_for_the_same_session_queues_exactly_one_reminder()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var scene = await SeedEnrolledSessionAsync(db, now, Duration.FromMinutes(5));

        var job = CreateJob(db, now);
        await job.SendFiveMinuteRemindersAsync(CancellationToken.None);
        await job.SendFiveMinuteRemindersAsync(CancellationToken.None); // simulates the next minute's run

        await using var verify = _fixture.CreateContext();
        var count = await verify.NotificationOutboxItems.CountAsync(
            n => n.Event == NotificationEvent.ZoomLink5Min && n.SessionId == scene.SessionId && n.RecipientUserId == scene.StudentUserId);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task A_session_starting_in_thirty_minutes_is_not_yet_reminded()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var scene = await SeedEnrolledSessionAsync(db, now, Duration.FromMinutes(30));

        await CreateJob(db, now).SendFiveMinuteRemindersAsync(CancellationToken.None);

        await using var verify = _fixture.CreateContext();
        Assert.False(await verify.NotificationOutboxItems.AnyAsync(n => n.SessionId == scene.SessionId));
    }

    /// <summary>Same "no independent login, nothing lost" convention every
    /// other notification wiring this session already established.</summary>
    [Fact]
    public async Task A_guardian_only_child_with_no_login_is_not_queued_but_does_not_break_the_job()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var scene = await SeedEnrolledSessionAsync(db, now, Duration.FromMinutes(5), linkStudentToUser: false);

        await CreateJob(db, now).SendFiveMinuteRemindersAsync(CancellationToken.None);

        await using var verify = _fixture.CreateContext();
        Assert.False(await verify.NotificationOutboxItems.AnyAsync(n => n.SessionId == scene.SessionId));
    }
}
