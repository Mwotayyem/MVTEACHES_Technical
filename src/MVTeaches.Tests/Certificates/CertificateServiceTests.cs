using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Certificates;
using MVTeaches.Application.Payroll;
using MVTeaches.Domain.Attendance;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Common;
using MVTeaches.Domain.Payroll;
using MVTeaches.Domain.People;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Domain.Settings;
using MVTeaches.Infrastructure.Certificates;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Payroll;
using MVTeaches.Infrastructure.Persistence;
using MVTeaches.Infrastructure.Settings;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace MVTeaches.Tests.Certificates;

/// <summary>
/// Technical Study §27.1/§27.2 (D-30/D-51/CONF-03) and Q-27 ("الشهادة باعتماد").
/// Progress accumulates on (student, level, course) from every session BOTH
/// attended (D-83) AND delivery-verified (§18) — never on a subscription.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class CertificateServiceTests
{
    private readonly TestDatabaseFixture _fixture;
    private static long _idSeed = 21_000_000; // a range distinct from every other test class sharing this DB

    public CertificateServiceTests(TestDatabaseFixture fixture) => _fixture = fixture;

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

    private static async Task SeedCertificateHoursSettingAsync(MvTeachesDbContext db, int hours)
    {
        var setting = await db.Settings.FirstOrDefaultAsync(s => s.Key == SettingKey.CertificateRequiredHours);
        if (setting is null)
        {
            db.Settings.Add(new Setting(SettingKey.CertificateRequiredHours, hours.ToString()));
        }
        else
        {
            setting.UpdateValue(hours.ToString(), updatedByUserId: 0, SystemClock.Instance.GetCurrentInstant());
        }

        await db.SaveChangesAsync();
    }

    private static ICertificateService CreateCertificateService(MvTeachesDbContext db, IClock clock) =>
        new CertificateService(db, new SettingsProvider(db, clock), clock);

    private static PayrollService CreatePayrollService(MvTeachesDbContext db, IClock clock) =>
        new(db, new PayrollRateResolver(db), CreateCertificateService(db, clock), clock);

    private record Fixture(long StudentId, int LevelId, long CourseId, long TeacherId, long TeacherUserId, long AdminUserId, int CountryId);

    /// <summary>Seeds one (student, level, course) with a teacher rate; the
    /// caller adds however many sessions/attendance/deliveries the test needs.</summary>
    private async Task<Fixture> SeedStudentAndTeacherAsync(MvTeachesDbContext db)
    {
        var countryId = (int)NextId();
        var courseId = NextId();
        var levelId = (int)NextId();
        var ageGroupId = (int)NextId();
        var teacherUserId = await CreateUserAsync(db, "teacher");
        var studentUserId = await CreateUserAsync(db, "student");
        var adminUserId = await CreateUserAsync(db, "admin");

        db.Countries.Add(new Country(countryId, TwoLetterCode(countryId), "دولة", "Country", "JOD", "+962", "Asia/Amman"));
        db.Courses.Add(new Course("C" + courseId, "دورة", "Course"));
        db.Levels.Add(new Level(levelId, "L" + levelId, "مستوى", "Level", levelId));
        db.AgeGroups.Add(new AgeGroup(ageGroupId, "A" + ageGroupId, 5, 12, true));
        var teacher = new Teacher(teacherUserId, "Teacher", "Asia/Amman");
        db.Teachers.Add(teacher);
        var student = new Student(countryId, "Student", new LocalDate(2015, 1, 1), studentUserId);
        db.Students.Add(student);
        await db.SaveChangesAsync();

        db.TeacherRates.Add(new TeacherRate(teacher.Id, null, null, null, new Money(10m, "JOD"),
            RateUnit.PerHour, new LocalDate(2020, 1, 1), adminUserId));
        await db.SaveChangesAsync();

        return new Fixture(student.Id, levelId, courseId, teacher.Id, teacherUserId, adminUserId, countryId);
    }

    /// <summary>Creates a session, marks the student Present (independent of
    /// delivery — D-83), declares and verifies the delivery, which is what
    /// actually triggers the §27.2 recompute under test. Each call needs its
    /// own <paramref name="daysAgo"/> — the same teacher, same instant would
    /// otherwise collide with the real no_teacher_overlap EXCLUDE constraint.</summary>
    private async Task<long> AddAttendedAndVerifiedSessionAsync(MvTeachesDbContext db, Fixture fx, int durationMinutes, Instant now, int daysAgo = 1)
    {
        var start = now.Minus(Duration.FromDays(daysAgo));
        var session = new ClassSession(fx.CountryId, null, fx.CourseId, fx.LevelId, 0, fx.TeacherId,
            start, start.Plus(Duration.FromMinutes(durationMinutes)),
            "Asia/Amman", "17:00", SessionType.Group, 4, now.Minus(Duration.FromDays(daysAgo + 1)));
        db.ClassSessions.Add(session);
        await db.SaveChangesAsync();

        db.AttendanceRecords.Add(new AttendanceRecord(session.Id, fx.StudentId, fx.TeacherUserId, now));
        await db.SaveChangesAsync();

        var payroll = CreatePayrollService(db, new FakeClock(now));
        var declareResult = await payroll.DeclareAsync(session.Id, fx.TeacherUserId, durationMinutes, null, CancellationToken.None);
        Assert.Equal(DeclareDeliveryOutcome.Declared, declareResult.Outcome);
        var verifyResult = await payroll.VerifyAsync(session.Id, fx.AdminUserId, null, CancellationToken.None);
        Assert.Equal(VerifyDeliveryOutcome.Verified, verifyResult.Outcome);

        return session.Id;
    }

    [Fact]
    public async Task Verifying_a_delivery_accumulates_minutes_onto_level_progress()
    {
        await using var db = _fixture.CreateContext();
        await SeedCertificateHoursSettingAsync(db, hours: 30);
        var fx = await SeedStudentAndTeacherAsync(db);
        var now = SystemClock.Instance.GetCurrentInstant();

        await AddAttendedAndVerifiedSessionAsync(db, fx, 60, now);

        await using var verifyDb = _fixture.CreateContext();
        var progress = await verifyDb.LevelProgresses.FirstAsync(
            p => p.StudentId == fx.StudentId && p.LevelId == fx.LevelId && p.CourseId == fx.CourseId);
        Assert.Equal(60, progress.MinutesCompleted);
        Assert.Null(progress.CompletedAtUtc); // below the 30h threshold
    }

    [Fact]
    public async Task Progress_accumulates_across_multiple_sessions_and_crossing_the_threshold_stamps_completed_at()
    {
        await using var db = _fixture.CreateContext();
        await SeedCertificateHoursSettingAsync(db, hours: 1); // 60 minutes — reachable in two sessions
        var fx = await SeedStudentAndTeacherAsync(db);
        var now = SystemClock.Instance.GetCurrentInstant();

        await AddAttendedAndVerifiedSessionAsync(db, fx, 30, now, daysAgo: 1);
        await AddAttendedAndVerifiedSessionAsync(db, fx, 30, now, daysAgo: 2);

        await using var verifyDb = _fixture.CreateContext();
        var progress = await verifyDb.LevelProgresses.FirstAsync(
            p => p.StudentId == fx.StudentId && p.LevelId == fx.LevelId && p.CourseId == fx.CourseId);
        Assert.Equal(60, progress.MinutesCompleted);
        Assert.NotNull(progress.CompletedAtUtc); // now at/over the 60-minute threshold
    }

    [Fact]
    public async Task A_session_the_student_never_joined_contributes_no_minutes()
    {
        await using var db = _fixture.CreateContext();
        await SeedCertificateHoursSettingAsync(db, hours: 30);
        var fx = await SeedStudentAndTeacherAsync(db);
        var now = SystemClock.Instance.GetCurrentInstant();

        // A session with a Verified delivery but NO AttendanceRecord for the student.
        var session = new ClassSession(fx.CountryId, null, fx.CourseId, fx.LevelId, 0, fx.TeacherId,
            now.Minus(Duration.FromDays(1)), now.Minus(Duration.FromDays(1)).Plus(Duration.FromMinutes(60)),
            "Asia/Amman", "17:00", SessionType.Group, 4, now.Minus(Duration.FromDays(2)));
        db.ClassSessions.Add(session);
        await db.SaveChangesAsync();

        var payroll = CreatePayrollService(db, new FakeClock(now));
        await payroll.DeclareAsync(session.Id, fx.TeacherUserId, 60, null, CancellationToken.None);
        await payroll.VerifyAsync(session.Id, fx.AdminUserId, null, CancellationToken.None);

        await using var verifyDb = _fixture.CreateContext();
        // RecomputeLevelProgressForSessionAsync finds no attendee at all, so it
        // never touches LevelProgress for anyone for this session.
        Assert.False(await verifyDb.LevelProgresses.AnyAsync(
            p => p.StudentId == fx.StudentId && p.LevelId == fx.LevelId && p.CourseId == fx.CourseId));
    }

    [Fact]
    public async Task Eligibility_reflects_the_live_setting_not_a_stored_snapshot()
    {
        await using var db = _fixture.CreateContext();
        await SeedCertificateHoursSettingAsync(db, hours: 1);
        var fx = await SeedStudentAndTeacherAsync(db);
        var now = SystemClock.Instance.GetCurrentInstant();

        await AddAttendedAndVerifiedSessionAsync(db, fx, 60, now); // exactly 60 minutes = 1 hour

        var service = CreateCertificateService(db, new FakeClock(now));
        var eligible = await service.GetEligibilityAsync(fx.StudentId, fx.LevelId, fx.CourseId, CancellationToken.None);
        Assert.True(eligible.IsEligible);

        // The admin raises the threshold — the SAME 60 minutes is now insufficient,
        // because eligibility is read live against the setting (D-65), never snapshotted.
        await SeedCertificateHoursSettingAsync(db, hours: 2);
        var afterRaise = await service.GetEligibilityAsync(fx.StudentId, fx.LevelId, fx.CourseId, CancellationToken.None);
        Assert.False(afterRaise.IsEligible);
    }

    [Fact]
    public async Task Issuing_below_the_threshold_is_rejected()
    {
        await using var db = _fixture.CreateContext();
        await SeedCertificateHoursSettingAsync(db, hours: 30);
        var fx = await SeedStudentAndTeacherAsync(db);
        var now = SystemClock.Instance.GetCurrentInstant();

        await AddAttendedAndVerifiedSessionAsync(db, fx, 60, now); // far below 30 hours

        var service = CreateCertificateService(db, new FakeClock(now));
        var result = await service.IssueAsync(fx.StudentId, fx.LevelId, fx.CourseId, fx.AdminUserId, CancellationToken.None);

        Assert.Equal(IssueCertificateOutcome.NotEligible, result.Outcome);

        await using var verifyDb = _fixture.CreateContext();
        Assert.False(await verifyDb.Certificates.AnyAsync(c => c.StudentId == fx.StudentId));
    }

    [Fact]
    public async Task Reaching_the_threshold_does_not_auto_issue_a_certificate()
    {
        // §27.4/Q-27: crossing the threshold is necessary but never sufficient
        // by itself — nothing in the system issues a certificate automatically.
        await using var db = _fixture.CreateContext();
        await SeedCertificateHoursSettingAsync(db, hours: 1);
        var fx = await SeedStudentAndTeacherAsync(db);
        var now = SystemClock.Instance.GetCurrentInstant();

        await AddAttendedAndVerifiedSessionAsync(db, fx, 60, now);

        await using var verifyDb = _fixture.CreateContext();
        Assert.False(await verifyDb.Certificates.AnyAsync(c => c.StudentId == fx.StudentId));
    }

    [Fact]
    public async Task Issuing_at_or_above_the_threshold_creates_exactly_one_certificate_and_a_second_attempt_is_rejected()
    {
        await using var db = _fixture.CreateContext();
        await SeedCertificateHoursSettingAsync(db, hours: 1);
        var fx = await SeedStudentAndTeacherAsync(db);
        var now = SystemClock.Instance.GetCurrentInstant();

        await AddAttendedAndVerifiedSessionAsync(db, fx, 60, now);

        var service = CreateCertificateService(db, new FakeClock(now));
        var first = await service.IssueAsync(fx.StudentId, fx.LevelId, fx.CourseId, fx.AdminUserId, CancellationToken.None);
        Assert.Equal(IssueCertificateOutcome.Issued, first.Outcome);
        Assert.NotNull(first.CertificateNumber);

        var second = await service.IssueAsync(fx.StudentId, fx.LevelId, fx.CourseId, fx.AdminUserId, CancellationToken.None);
        Assert.Equal(IssueCertificateOutcome.AlreadyIssued, second.Outcome);

        await using var verifyDb = _fixture.CreateContext();
        Assert.Equal(1, verifyDb.Certificates.Count(c => c.StudentId == fx.StudentId && c.LevelId == fx.LevelId && c.CourseId == fx.CourseId));
    }

    /// <summary>Release-readiness audit finding: IssueAsync's alreadyIssued
    /// check is a plain SELECT with no ambient transaction — the real backstop
    /// is UNIQUE(student_id, level_id, course_id). Before the fix, the loser
    /// of a genuine race (two concurrent "Issue" clicks on the same eligible
    /// student) crashed with an unhandled DbUpdateException instead of the
    /// same friendly AlreadyIssued outcome the sequential case already
    /// returns.</summary>
    [Fact]
    public async Task Two_concurrent_issue_attempts_still_produce_exactly_one_certificate()
    {
        await using var seedDb = _fixture.CreateContext();
        await SeedCertificateHoursSettingAsync(seedDb, hours: 1);
        var fx = await SeedStudentAndTeacherAsync(seedDb);
        var now = SystemClock.Instance.GetCurrentInstant();
        await AddAttendedAndVerifiedSessionAsync(seedDb, fx, 60, now);

        // Separate DbContexts — a genuine concurrent database race, not two
        // sequential calls sharing one context (same pattern as
        // Two_concurrent_join_requests_still_produce_exactly_one_consumption).
        var service1 = CreateCertificateService(_fixture.CreateContext(), new FakeClock(now));
        var service2 = CreateCertificateService(_fixture.CreateContext(), new FakeClock(now));

        var task1 = service1.IssueAsync(fx.StudentId, fx.LevelId, fx.CourseId, fx.AdminUserId, CancellationToken.None);
        var task2 = service2.IssueAsync(fx.StudentId, fx.LevelId, fx.CourseId, fx.AdminUserId, CancellationToken.None);
        var results = await Task.WhenAll(task1, task2);

        Assert.Contains(results, r => r.Outcome == IssueCertificateOutcome.Issued);
        Assert.Contains(results, r => r.Outcome == IssueCertificateOutcome.AlreadyIssued);

        await using var verifyDb = _fixture.CreateContext();
        Assert.Equal(1, verifyDb.Certificates.Count(c => c.StudentId == fx.StudentId && c.LevelId == fx.LevelId && c.CourseId == fx.CourseId));
    }

    [Fact]
    public async Task An_issued_certificate_is_never_re_evaluated_after_the_threshold_changes()
    {
        await using var db = _fixture.CreateContext();
        await SeedCertificateHoursSettingAsync(db, hours: 1);
        var fx = await SeedStudentAndTeacherAsync(db);
        var now = SystemClock.Instance.GetCurrentInstant();
        await AddAttendedAndVerifiedSessionAsync(db, fx, 60, now);

        var service = CreateCertificateService(db, new FakeClock(now));
        var issued = await service.IssueAsync(fx.StudentId, fx.LevelId, fx.CourseId, fx.AdminUserId, CancellationToken.None);
        Assert.Equal(IssueCertificateOutcome.Issued, issued.Outcome);

        // Raise the threshold well above what this student ever completed.
        await SeedCertificateHoursSettingAsync(db, hours: 100);

        await using var verifyDb = _fixture.CreateContext();
        var certificate = await verifyDb.Certificates.FirstAsync(c => c.Id == issued.CertificateId);
        Assert.Equal(60, certificate.MinutesCompleted); // the snapshot at issuance, untouched
        Assert.Equal(Domain.Certificates.CertificateStatus.Issued, certificate.Status);
    }

    [Fact]
    public async Task Revoking_a_certificate_marks_it_revoked_without_deleting_it()
    {
        await using var db = _fixture.CreateContext();
        await SeedCertificateHoursSettingAsync(db, hours: 1);
        var fx = await SeedStudentAndTeacherAsync(db);
        var now = SystemClock.Instance.GetCurrentInstant();
        await AddAttendedAndVerifiedSessionAsync(db, fx, 60, now);

        var service = CreateCertificateService(db, new FakeClock(now));
        var issued = await service.IssueAsync(fx.StudentId, fx.LevelId, fx.CourseId, fx.AdminUserId, CancellationToken.None);

        await service.RevokeAsync(issued.CertificateId!.Value, CancellationToken.None);

        await using var verifyDb = _fixture.CreateContext();
        var certificate = await verifyDb.Certificates.FirstAsync(c => c.Id == issued.CertificateId);
        Assert.Equal(Domain.Certificates.CertificateStatus.Revoked, certificate.Status);
    }
}
