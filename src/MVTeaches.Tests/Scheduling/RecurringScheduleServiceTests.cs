using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Scheduling;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.People;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using MVTeaches.Infrastructure.Scheduling;
using NodaTime;
using Xunit;

namespace MVTeaches.Tests.Scheduling;

/// <summary>
/// §15.2 (D-23) — before this service existed there was no way, anywhere in
/// the application, to create a RecurringSchedule; every downstream feature
/// (attendance, payroll, certificates) depends on the ClassSession rows this
/// eventually produces via ScheduleGenerationService (tested separately).
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class RecurringScheduleServiceTests
{
    private readonly TestDatabaseFixture _fixture;
    private static long _idSeed = 41_000_000; // a range distinct from every other test class sharing this DB

    public RecurringScheduleServiceTests(TestDatabaseFixture fixture) => _fixture = fixture;

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
            UserName = $"u-{Guid.NewGuid():N}",
            NormalizedUserName = $"U-{Guid.NewGuid():N}",
            Email = $"{Guid.NewGuid():N}@test.mvteaches.local",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<(int CountryId, long CourseId, int LevelId, int AgeGroupId, long TeacherId)> SeedCatalogAndTeacherAsync(MvTeachesDbContext db)
    {
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

        // Owner clarification (2026-08-29): a teacher with no connected
        // Zoom/Google Meet account is "not ready for online sessions" and
        // cannot be assigned any — so every schedule-creating test now needs
        // a real connection row. Its own rejection case is asserted by
        // Creating_a_schedule_for_a_teacher_with_no_video_connection_is_refused.
        db.TeacherMeetingConnections.Add(new MVTeaches.Domain.Integrations.TeacherMeetingConnection(
            teacher.Id, MVTeaches.Domain.Integrations.VideoProviderType.GoogleMeet, "acct-" + teacher.Id,
            "t@example.test", "enc::access", "enc::refresh", null, SystemClock.Instance.GetCurrentInstant()));

        // Owner decision 2026-08-30 rule 5: a teacher may only publish for a
        // level an admin granted them, so every schedule-creating test now
        // needs the grant too. Its own rejection case is asserted by
        // Creating_a_schedule_for_a_level_the_teacher_is_not_authorized_for_is_refused.
        db.TeacherLevelAssignments.Add(new MVTeaches.Domain.People.TeacherLevelAssignment(
            teacher.Id, levelId, grantedByUserId: teacherUserId, SystemClock.Instance.GetCurrentInstant()));
        await db.SaveChangesAsync();

        return (countryId, courseId, levelId, ageGroupId, teacher.Id);
    }

    /// <summary>Owner decision 2026-08-30 rule 5: "A teacher must not publish a
    /// session for an unauthorized level."</summary>
    [Fact]
    public async Task Creating_a_schedule_for_a_level_the_teacher_is_not_authorized_for_is_refused()
    {
        await using var db = _fixture.CreateContext();
        var (countryId, courseId, _, ageGroupId, teacherId) = await SeedCatalogAndTeacherAsync(db);

        // A second level the teacher was never granted.
        var otherLevelId = (int)NextId();
        db.Levels.Add(new Level(otherLevelId, "L" + otherLevelId, "مستوى", "Other Level", otherLevelId));
        await db.SaveChangesAsync();

        var service = new RecurringScheduleService(db);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(
            countryId, courseId, otherLevelId, ageGroupId, teacherId,
            new[] { IsoDayOfWeek.Monday }, new LocalTime(17, 0), 60, "Asia/Amman",
            new LocalDate(2026, 9, 1), 4, NextId(), CancellationToken.None));

        Assert.Contains("not authorized to teach this level", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(await db.RecurringSchedules.AnyAsync(s => s.LevelId == otherLevelId));
    }

    [Fact]
    public async Task Creating_a_schedule_for_a_teacher_with_no_video_connection_is_refused()
    {
        await using var db = _fixture.CreateContext();
        var (countryId, courseId, levelId, ageGroupId, _) = await SeedCatalogAndTeacherAsync(db);

        var unconnectedUserId = await CreateUserAsync(db);
        var unconnected = new Teacher(unconnectedUserId, "Unconnected Teacher", "Asia/Amman");
        db.Teachers.Add(unconnected);
        await db.SaveChangesAsync();

        var service = new RecurringScheduleService(db);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(
            countryId, courseId, levelId, ageGroupId, unconnected.Id,
            new[] { IsoDayOfWeek.Monday }, new LocalTime(17, 0), 60, "Asia/Amman",
            new LocalDate(2026, 9, 1), 4, unconnectedUserId, CancellationToken.None));

        Assert.Contains("not ready for online sessions", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(await db.RecurringSchedules.AnyAsync(s => s.TeacherId == unconnected.Id));
    }

    [Fact]
    public async Task Creating_a_schedule_persists_every_field()
    {
        await using var db = _fixture.CreateContext();
        var (countryId, courseId, levelId, ageGroupId, teacherId) = await SeedCatalogAndTeacherAsync(db);
        var service = new RecurringScheduleService(db);

        var result = await service.CreateAsync(countryId, courseId, levelId, ageGroupId, teacherId,
            new[] { IsoDayOfWeek.Monday, IsoDayOfWeek.Wednesday }, new LocalTime(17, 0), 60, "Asia/Amman",
            new LocalDate(2026, 9, 1), capacity: 4, createdByUserId: NextId(), CancellationToken.None);

        var schedule = await db.RecurringSchedules.FirstAsync(s => s.Id == result.RecurringScheduleId);
        Assert.Equal(RecurringScheduleStatus.Active, schedule.Status);
        Assert.Equal(2, schedule.DaysOfWeek.Count);
        Assert.Contains(IsoDayOfWeek.Monday, schedule.DaysOfWeek);
        Assert.Equal(60, schedule.DurationMinutes);
        Assert.Equal(4, schedule.Capacity);
    }

    [Fact]
    public async Task Pausing_and_resuming_a_schedule_toggles_its_status()
    {
        await using var db = _fixture.CreateContext();
        var (countryId, courseId, levelId, ageGroupId, teacherId) = await SeedCatalogAndTeacherAsync(db);
        var service = new RecurringScheduleService(db);

        var result = await service.CreateAsync(countryId, courseId, levelId, ageGroupId, teacherId,
            new[] { IsoDayOfWeek.Tuesday }, new LocalTime(10, 0), 60, "Asia/Amman", new LocalDate(2026, 9, 1),
            capacity: 4, createdByUserId: NextId(), CancellationToken.None);

        await service.PauseAsync(result.RecurringScheduleId, CancellationToken.None);
        var afterPause = await db.RecurringSchedules.AsNoTracking().FirstAsync(s => s.Id == result.RecurringScheduleId);
        Assert.Equal(RecurringScheduleStatus.Paused, afterPause.Status);

        await service.ResumeAsync(result.RecurringScheduleId, CancellationToken.None);
        var afterResume = await db.RecurringSchedules.AsNoTracking().FirstAsync(s => s.Id == result.RecurringScheduleId);
        Assert.Equal(RecurringScheduleStatus.Active, afterResume.Status);
    }

    [Fact]
    public async Task Ending_a_schedule_records_the_end_date()
    {
        await using var db = _fixture.CreateContext();
        var (countryId, courseId, levelId, ageGroupId, teacherId) = await SeedCatalogAndTeacherAsync(db);
        var service = new RecurringScheduleService(db);

        var result = await service.CreateAsync(countryId, courseId, levelId, ageGroupId, teacherId,
            new[] { IsoDayOfWeek.Thursday }, new LocalTime(10, 0), 60, "Asia/Amman", new LocalDate(2026, 9, 1),
            capacity: 4, createdByUserId: NextId(), CancellationToken.None);

        var endsOn = new LocalDate(2026, 12, 31);
        await service.EndAsync(result.RecurringScheduleId, endsOn, CancellationToken.None);

        var schedule = await db.RecurringSchedules.AsNoTracking().FirstAsync(s => s.Id == result.RecurringScheduleId);
        Assert.Equal(RecurringScheduleStatus.Ended, schedule.Status);
        Assert.Equal(endsOn, schedule.EndsOn);
    }
}
