using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Scheduling;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Integrations;
using MVTeaches.Domain.People;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using MVTeaches.Infrastructure.Scheduling;
using NodaTime;
using NodaTime.Testing;
using Npgsql;
using Xunit;

namespace MVTeaches.Tests.Scheduling;

/// <summary>
/// Owner decision 2026-08-30 rule 7: the teacher-facing screen for publishing
/// a single, one-off available slot within their own authorized levels —
/// distinct from RecurringScheduleService (an admin-only weekly roster).
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class TeacherSlotPublishingServiceTests
{
    private readonly TestDatabaseFixture _fixture;
    private static long _idSeed = 91_000_000;

    public TeacherSlotPublishingServiceTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static long NextId() => Interlocked.Increment(ref _idSeed);

    private static string TwoLetterCode(long seed)
    {
        var n = (int)(seed % 676);
        return string.Concat((char)('A' + n / 26), (char)('A' + n % 26));
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

    private record Scene(int CountryId, long CourseId, int LevelId, int AgeGroupId, long TeacherId, long TeacherUserId);

    // One Country for the whole class, not one per test: the 2-letter code
    // space (676 combinations) is shared with every other test class in the
    // same run via the same NextId()-derived TwoLetterCode pattern, and a real
    // cross-class collision has already happened before (see
    // RescheduleAndCompensationTests' own comment on the same issue).
    private static int? _sharedCountryId;

    private static async Task<int> GetOrSeedCountryAsync(MvTeachesDbContext db)
    {
        if (_sharedCountryId is { } existing)
        {
            return existing;
        }

        // A shared country per class only reduces the ODDS of a cross-class
        // collision on the 676-code space; it does not prevent one, since two
        // unrelated classes' independent NextId() sequences can still land on
        // the same code % 676 by pure coincidence. Retrying with a fresh id on
        // an actual collision (as MeetingProvisioningServiceTests already
        // does) is what makes this genuinely collision-proof rather than
        // merely unlikely to collide.
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
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
            {
                db.ChangeTracker.Clear();
            }
        }

        throw new InvalidOperationException("Could not find a free 2-letter country code after 10 attempts.");
    }

    private static async Task<Scene> SeedReadyAuthorizedTeacherAsync(MvTeachesDbContext db, bool connected = true, bool levelGranted = true)
    {
        var countryId = await GetOrSeedCountryAsync(db);
        // Course.Id is database-generated: read the real id back after
        // SaveChanges rather than reusing the NextId() seed. Harmless while
        // nothing referenced courses by key; TeacherLevelAssignment.CourseId
        // (2026-09-04) does, so a fabricated id now violates a real foreign key.
        var courseSeed = NextId();
        var levelId = (int)NextId();
        var ageGroupId = (int)NextId();
        var teacherUserId = await CreateUserAsync(db);

        var course = new Course("C" + courseSeed, "دورة", "Course");
        db.Courses.Add(course);
        db.Levels.Add(new Level(levelId, "L" + levelId, "مستوى", "Level", levelId));
        db.AgeGroups.Add(new AgeGroup(ageGroupId, "A" + ageGroupId, 5, 12, true));
        var teacher = new Teacher(teacherUserId, "Teacher", "Asia/Amman");
        db.Teachers.Add(teacher);
        await db.SaveChangesAsync();
        var courseId = course.Id;

        if (connected)
        {
            db.TeacherMeetingConnections.Add(new TeacherMeetingConnection(teacher.Id, VideoProviderType.GoogleMeet,
                "acct-" + teacher.Id, "t@example.test", "enc::access", "enc::refresh", null, SystemClock.Instance.GetCurrentInstant()));
        }
        if (levelGranted)
        {
            db.TeacherLevelAssignments.Add(new TeacherLevelAssignment(teacher.Id, courseId, levelId, teacherUserId, SystemClock.Instance.GetCurrentInstant()));
        }
        await db.SaveChangesAsync();

        return new Scene(countryId, courseId, levelId, ageGroupId, teacher.Id, teacherUserId);
    }

    private static ITeacherSlotPublishingService CreateService(MvTeachesDbContext db) => new TeacherSlotPublishingService(db, new FakeClock(SystemClock.Instance.GetCurrentInstant()));

    [Fact]
    public async Task An_authorized_ready_teacher_can_publish_a_group_slot_with_capacity_derived_automatically()
    {
        await using var db = _fixture.CreateContext();
        var scene = await SeedReadyAuthorizedTeacherAsync(db);
        var service = CreateService(db);
        var start = SystemClock.Instance.GetCurrentInstant().Plus(Duration.FromDays(1));

        var result = await service.PublishSlotAsync(scene.TeacherId, scene.TeacherUserId, scene.CountryId, scene.CourseId,
            scene.LevelId, scene.AgeGroupId, start, 60, "Asia/Amman", "17:00", SessionType.Group, CancellationToken.None);

        Assert.Equal(PublishSlotOutcome.Published, result.Outcome);
        var session = await db.ClassSessions.FirstAsync(s => s.Id == result.SessionId);
        Assert.Equal(4, session.Capacity); // derived from SessionType.Group, never supplied
        Assert.Equal(ClassSessionStatus.Scheduled, session.Status);
    }

    [Fact]
    public async Task A_private_slot_gets_capacity_one()
    {
        await using var db = _fixture.CreateContext();
        var scene = await SeedReadyAuthorizedTeacherAsync(db);
        var service = CreateService(db);
        var start = SystemClock.Instance.GetCurrentInstant().Plus(Duration.FromDays(1));

        var result = await service.PublishSlotAsync(scene.TeacherId, scene.TeacherUserId, scene.CountryId, scene.CourseId,
            scene.LevelId, scene.AgeGroupId, start, 60, "Asia/Amman", "17:00", SessionType.Private, CancellationToken.None);

        var session = await db.ClassSessions.FirstAsync(s => s.Id == result.SessionId);
        Assert.Equal(1, session.Capacity);
    }

    [Fact]
    public async Task A_teacher_cannot_publish_a_slot_as_another_teacher()
    {
        await using var db = _fixture.CreateContext();
        var scene = await SeedReadyAuthorizedTeacherAsync(db);
        var impostorUserId = await CreateUserAsync(db);
        var service = CreateService(db);

        var result = await service.PublishSlotAsync(scene.TeacherId, impostorUserId, scene.CountryId, scene.CourseId,
            scene.LevelId, scene.AgeGroupId, SystemClock.Instance.GetCurrentInstant().Plus(Duration.FromDays(1)),
            60, "Asia/Amman", "17:00", SessionType.Group, CancellationToken.None);

        Assert.Equal(PublishSlotOutcome.Unauthorized, result.Outcome);
        Assert.False(await db.ClassSessions.AnyAsync(s => s.TeacherId == scene.TeacherId));
    }

    [Fact]
    public async Task A_teacher_with_no_connected_video_account_cannot_publish()
    {
        await using var db = _fixture.CreateContext();
        var scene = await SeedReadyAuthorizedTeacherAsync(db, connected: false);
        var service = CreateService(db);

        var result = await service.PublishSlotAsync(scene.TeacherId, scene.TeacherUserId, scene.CountryId, scene.CourseId,
            scene.LevelId, scene.AgeGroupId, SystemClock.Instance.GetCurrentInstant().Plus(Duration.FromDays(1)),
            60, "Asia/Amman", "17:00", SessionType.Group, CancellationToken.None);

        Assert.Equal(PublishSlotOutcome.TeacherNotReadyForOnlineSessions, result.Outcome);
    }

    /// <summary>Rule 5 + rule 7: "A teacher must not publish a session for an
    /// unauthorized level."</summary>
    [Fact]
    public async Task A_teacher_cannot_publish_for_a_level_they_are_not_authorized_for()
    {
        await using var db = _fixture.CreateContext();
        var scene = await SeedReadyAuthorizedTeacherAsync(db, levelGranted: false);
        var service = CreateService(db);

        var result = await service.PublishSlotAsync(scene.TeacherId, scene.TeacherUserId, scene.CountryId, scene.CourseId,
            scene.LevelId, scene.AgeGroupId, SystemClock.Instance.GetCurrentInstant().Plus(Duration.FromDays(1)),
            60, "Asia/Amman", "17:00", SessionType.Group, CancellationToken.None);

        Assert.Equal(PublishSlotOutcome.NotAuthorizedForLevel, result.Outcome);
    }

    /// <summary>Rule 7: "Prevent overlapping active slots for the same teacher."</summary>
    [Fact]
    public async Task Publishing_an_overlapping_slot_for_the_same_teacher_is_refused()
    {
        await using var db = _fixture.CreateContext();
        var scene = await SeedReadyAuthorizedTeacherAsync(db);
        var service = CreateService(db);
        var start = SystemClock.Instance.GetCurrentInstant().Plus(Duration.FromDays(1));
        var first = await service.PublishSlotAsync(scene.TeacherId, scene.TeacherUserId, scene.CountryId, scene.CourseId,
            scene.LevelId, scene.AgeGroupId, start, 60, "Asia/Amman", "17:00", SessionType.Group, CancellationToken.None);
        Assert.Equal(PublishSlotOutcome.Published, first.Outcome);

        // Starts 30 minutes into the first slot — a genuine overlap.
        var overlapping = await service.PublishSlotAsync(scene.TeacherId, scene.TeacherUserId, scene.CountryId, scene.CourseId,
            scene.LevelId, scene.AgeGroupId, start.Plus(Duration.FromMinutes(30)), 60, "Asia/Amman", "17:30", SessionType.Group, CancellationToken.None);

        Assert.Equal(PublishSlotOutcome.Overlapping, overlapping.Outcome);
        Assert.Equal(1, await db.ClassSessions.CountAsync(s => s.TeacherId == scene.TeacherId));
    }

    [Fact]
    public async Task Back_to_back_non_overlapping_slots_are_both_allowed()
    {
        await using var db = _fixture.CreateContext();
        var scene = await SeedReadyAuthorizedTeacherAsync(db);
        var service = CreateService(db);
        var start = SystemClock.Instance.GetCurrentInstant().Plus(Duration.FromDays(1));
        await service.PublishSlotAsync(scene.TeacherId, scene.TeacherUserId, scene.CountryId, scene.CourseId,
            scene.LevelId, scene.AgeGroupId, start, 60, "Asia/Amman", "17:00", SessionType.Group, CancellationToken.None);

        var second = await service.PublishSlotAsync(scene.TeacherId, scene.TeacherUserId, scene.CountryId, scene.CourseId,
            scene.LevelId, scene.AgeGroupId, start.Plus(Duration.FromMinutes(60)), 60, "Asia/Amman", "18:00", SessionType.Group, CancellationToken.None);

        Assert.Equal(PublishSlotOutcome.Published, second.Outcome);
        Assert.Equal(2, await db.ClassSessions.CountAsync(s => s.TeacherId == scene.TeacherId));
    }
}
