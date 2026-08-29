using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Settings;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.People;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Domain.Settings;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using MVTeaches.Infrastructure.Scheduling;
using MVTeaches.Infrastructure.Settings;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace MVTeaches.Tests.Scheduling;

/// <summary>
/// Technical Study §15.3 — the nightly/manual generator. Tested against a
/// real PostgreSQL 16 database because the collision path relies on the
/// actual no_teacher_overlap EXCLUDE constraint (§14.2), not a re-implemented
/// in-memory check.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class ScheduleGenerationServiceTests
{
    private readonly TestDatabaseFixture _fixture;
    private static long _idSeed = 13_000_000; // a range distinct from every other test class sharing this DB

    public ScheduleGenerationServiceTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static long NextId() => Interlocked.Increment(ref _idSeed);

    private static string TwoLetterCode(long seed)
    {
        var n = (int)(seed % 676);
        return string.Concat((char)('A' + n / 26), (char)('A' + n % 26));
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

    private async Task SeedHorizonSettingAsync(int weeks)
    {
        await using var db = _fixture.CreateContext();
        var setting = await db.Settings.FirstOrDefaultAsync(s => s.Key == SettingKey.ScheduleGenerationHorizonWeeks);
        if (setting is null)
        {
            db.Settings.Add(new Domain.Settings.Setting(SettingKey.ScheduleGenerationHorizonWeeks, weeks.ToString()));
        }
        else
        {
            setting.UpdateValue(weeks.ToString(), updatedByUserId: 0, SystemClock.Instance.GetCurrentInstant());
        }

        await db.SaveChangesAsync();
    }

    private async Task<(int CountryId, long CourseId, int LevelId, int AgeGroupId, long TeacherId)> SeedCatalogAndTeacherAsync(
        MvTeachesDbContext db, string teacherTimeZone = "Asia/Amman")
    {
        var countryId = (int)NextId();
        var courseId = NextId();
        var levelId = (int)NextId();
        var ageGroupId = (int)NextId();
        var teacherUserId = await CreateUserAsync(db, "teacher");

        db.Countries.Add(new Country(countryId, TwoLetterCode(countryId), "دولة", "Country", "JOD", "+962", "Asia/Amman"));
        db.Courses.Add(new Course("C" + courseId, "دورة", "Course"));
        db.Levels.Add(new Level(levelId, "L" + levelId, "مستوى", "Level", levelId));
        db.AgeGroups.Add(new AgeGroup(ageGroupId, "A" + ageGroupId, 5, 12, true));
        var teacher = new Teacher(teacherUserId, "Teacher", teacherTimeZone);
        db.Teachers.Add(teacher);
        await db.SaveChangesAsync();

        return (countryId, courseId, levelId, ageGroupId, teacher.Id);
    }

    [Fact]
    public async Task Generates_one_session_per_matching_day_within_the_horizon()
    {
        await SeedHorizonSettingAsync(2); // 2 weeks — small, deterministic window

        await using var db = _fixture.CreateContext();
        var (countryId, courseId, levelId, ageGroupId, teacherId) = await SeedCatalogAndTeacherAsync(db);

        var now = SystemClock.Instance.GetCurrentInstant();
        var today = now.InZone(DateTimeZoneProviders.Tzdb["Asia/Amman"]).Date;

        var schedule = new RecurringSchedule(countryId, courseId, levelId, ageGroupId, teacherId,
            new[] { today.DayOfWeek }, new LocalTime(18, 0), 60, "Asia/Amman", today, capacity: 4, createdByUserId: 0);
        db.RecurringSchedules.Add(schedule);
        await db.SaveChangesAsync();

        var service = new ScheduleGenerationService(db, new SettingsProvider(db, new FakeClock(now)), new FakeClock(now));
        // NOTE: summary is a whole-database scan result — other tests in this
        // class leave their own Active schedules behind (nothing in the
        // production design ever deactivates a schedule on its own), so every
        // assertion here is scoped to THIS schedule's own rows, never to the
        // raw summary counters.
        await service.GenerateAsync(CancellationToken.None);

        await using var verifyDb = _fixture.CreateContext();
        var generated = verifyDb.ClassSessions.Where(s => s.RecurringScheduleId == schedule.Id).ToList();

        // Exactly the Mondays-or-whatever-matches-today within a 15-day window
        // (today .. today+14) — every 7th day, so either 2 or 3 occurrences.
        Assert.True(generated.Count is 2 or 3, $"Expected 2 or 3 sessions, got {generated.Count}");
        Assert.All(generated, s => Assert.Equal(SessionType.Group, s.SessionType));
        Assert.Equal(0, verifyDb.ScheduleGenerationExceptions.Count(e => e.RecurringScheduleId == schedule.Id));
    }

    [Fact]
    public async Task Running_generation_twice_does_not_create_duplicate_sessions()
    {
        await SeedHorizonSettingAsync(1);

        await using var db = _fixture.CreateContext();
        var (countryId, courseId, levelId, ageGroupId, teacherId) = await SeedCatalogAndTeacherAsync(db);

        var now = SystemClock.Instance.GetCurrentInstant();
        var today = now.InZone(DateTimeZoneProviders.Tzdb["Asia/Amman"]).Date;

        var schedule = new RecurringSchedule(countryId, courseId, levelId, ageGroupId, teacherId,
            new[] { today.DayOfWeek }, new LocalTime(9, 0), 45, "Asia/Amman", today, capacity: 4, createdByUserId: 0);
        db.RecurringSchedules.Add(schedule);
        await db.SaveChangesAsync();

        var settings = new SettingsProvider(db, new FakeClock(now));
        await new ScheduleGenerationService(db, settings, new FakeClock(now)).GenerateAsync(CancellationToken.None);

        await using var afterFirst = _fixture.CreateContext();
        var countAfterFirst = afterFirst.ClassSessions.Count(s => s.RecurringScheduleId == schedule.Id);
        Assert.True(countAfterFirst > 0);

        await using var db2 = _fixture.CreateContext();
        var settings2 = new SettingsProvider(db2, new FakeClock(now));
        await new ScheduleGenerationService(db2, settings2, new FakeClock(now)).GenerateAsync(CancellationToken.None);

        await using var afterSecond = _fixture.CreateContext();
        var countAfterSecond = afterSecond.ClassSessions.Count(s => s.RecurringScheduleId == schedule.Id);

        Assert.Equal(countAfterFirst, countAfterSecond); // fully idempotent re-run — no duplicates for THIS schedule
        Assert.Equal(0, afterSecond.ScheduleGenerationExceptions.Count(e => e.RecurringScheduleId == schedule.Id));
    }

    [Fact]
    public async Task A_teacher_overlap_is_recorded_as_an_exception_and_does_not_throw()
    {
        await SeedHorizonSettingAsync(1);

        await using var db = _fixture.CreateContext();
        var (countryId, courseId, levelId, ageGroupId, teacherId) = await SeedCatalogAndTeacherAsync(db);

        var now = SystemClock.Instance.GetCurrentInstant();
        var zone = DateTimeZoneProviders.Tzdb["Asia/Amman"];
        var today = now.InZone(zone).Date;
        var occurrenceStart = zone.AtLeniently(today.At(new LocalTime(18, 0))).ToInstant();

        // A manually-created ClassSession already occupies this teacher for
        // this exact slot — NOT generated by any RecurringSchedule.
        db.ClassSessions.Add(new ClassSession(countryId, null, courseId, levelId, ageGroupId, teacherId,
            occurrenceStart, occurrenceStart.Plus(Duration.FromMinutes(60)), "Asia/Amman", "18:00",
            SessionType.Group, now));
        await db.SaveChangesAsync();

        var schedule = new RecurringSchedule(countryId, courseId, levelId, ageGroupId, teacherId,
            new[] { today.DayOfWeek }, new LocalTime(18, 0), 60, "Asia/Amman", today, capacity: 4, createdByUserId: 0);
        db.RecurringSchedules.Add(schedule);
        await db.SaveChangesAsync();

        var service = new ScheduleGenerationService(db, new SettingsProvider(db, new FakeClock(now)), new FakeClock(now));
        var summary = await service.GenerateAsync(CancellationToken.None); // must not throw

        Assert.True(summary.ConflictsRecorded >= 1);

        await using var verifyDb = _fixture.CreateContext();
        var exception = verifyDb.ScheduleGenerationExceptions.FirstOrDefault(e => e.RecurringScheduleId == schedule.Id);
        Assert.NotNull(exception);
        Assert.Equal(ScheduleConflictReason.TeacherOverlap, exception!.Reason);
        Assert.False(exception.Resolved);

        // The colliding occurrence itself must never have been created by the generator.
        Assert.Equal(1, verifyDb.ClassSessions.Count(
            s => s.TeacherId == teacherId && s.StartsAtUtc == occurrenceStart));
    }

    [Fact]
    public async Task A_teacher_time_off_window_blocks_generation_and_is_recorded()
    {
        await SeedHorizonSettingAsync(1);

        await using var db = _fixture.CreateContext();
        var (countryId, courseId, levelId, ageGroupId, teacherId) = await SeedCatalogAndTeacherAsync(db);

        var now = SystemClock.Instance.GetCurrentInstant();
        var zone = DateTimeZoneProviders.Tzdb["Asia/Amman"];
        var today = now.InZone(zone).Date;
        var occurrenceStart = zone.AtLeniently(today.At(new LocalTime(10, 0))).ToInstant();

        db.TeacherTimeOffs.Add(new TeacherTimeOff(teacherId,
            occurrenceStart.Minus(Duration.FromHours(1)), occurrenceStart.Plus(Duration.FromHours(2)),
            "On leave", createdByUserId: 0));
        await db.SaveChangesAsync();

        var schedule = new RecurringSchedule(countryId, courseId, levelId, ageGroupId, teacherId,
            new[] { today.DayOfWeek }, new LocalTime(10, 0), 60, "Asia/Amman", today, capacity: 4, createdByUserId: 0);
        db.RecurringSchedules.Add(schedule);
        await db.SaveChangesAsync();

        var service = new ScheduleGenerationService(db, new SettingsProvider(db, new FakeClock(now)), new FakeClock(now));
        var summary = await service.GenerateAsync(CancellationToken.None);

        Assert.True(summary.ConflictsRecorded >= 1);

        await using var verifyDb = _fixture.CreateContext();
        var exception = verifyDb.ScheduleGenerationExceptions.FirstOrDefault(
            e => e.RecurringScheduleId == schedule.Id && e.OccurrenceDate == today);
        Assert.NotNull(exception);
        Assert.Equal(ScheduleConflictReason.TeacherTimeOff, exception!.Reason);

        // The blocked occurrence itself must never have been generated — a
        // 1-week horizon also reaches the SAME weekday 7 days later, which is
        // outside the time-off window and legitimately does get a session, so
        // this asserts on the specific blocked instant, not the whole schedule.
        Assert.Equal(0, verifyDb.ClassSessions.Count(s => s.RecurringScheduleId == schedule.Id && s.StartsAtUtc == occurrenceStart));
    }

    [Fact]
    public async Task A_paused_schedule_generates_nothing()
    {
        await SeedHorizonSettingAsync(2);

        await using var db = _fixture.CreateContext();
        var (countryId, courseId, levelId, ageGroupId, teacherId) = await SeedCatalogAndTeacherAsync(db);

        var now = SystemClock.Instance.GetCurrentInstant();
        var today = now.InZone(DateTimeZoneProviders.Tzdb["Asia/Amman"]).Date;

        var schedule = new RecurringSchedule(countryId, courseId, levelId, ageGroupId, teacherId,
            new[] { today.DayOfWeek }, new LocalTime(15, 0), 60, "Asia/Amman", today, capacity: 4, createdByUserId: 0);
        schedule.Pause();
        db.RecurringSchedules.Add(schedule);
        await db.SaveChangesAsync();

        var service = new ScheduleGenerationService(db, new SettingsProvider(db, new FakeClock(now)), new FakeClock(now));
        await service.GenerateAsync(CancellationToken.None); // must not throw, and must not touch this schedule

        await using var verifyDb = _fixture.CreateContext();
        Assert.Equal(0, verifyDb.ClassSessions.Count(s => s.RecurringScheduleId == schedule.Id));
    }
}
