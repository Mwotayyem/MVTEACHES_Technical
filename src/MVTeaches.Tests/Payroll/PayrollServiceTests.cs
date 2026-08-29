using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Payroll;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Common;
using MVTeaches.Domain.Delivery;
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

namespace MVTeaches.Tests.Payroll;

/// <summary>
/// Technical Study §18.1/§18.2 (D-26) — the declare → verify → aggregate →
/// review → approve → pay cycle, against a real PostgreSQL 16 database (the
/// UNIQUE(period_id, session_id) constraint and the FK graph both matter here).
/// Verify also exercises the §27.2 certificate-progress recompute it triggers —
/// see CertificateServiceTests for that recompute's own dedicated coverage.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class PayrollServiceTests
{
    private readonly TestDatabaseFixture _fixture;
    private static long _idSeed = 17_000_000; // a range distinct from every other test class sharing this DB

    public PayrollServiceTests(TestDatabaseFixture fixture) => _fixture = fixture;

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

    /// <summary>VerifyAsync always triggers a certificate-progress recompute
    /// (§27.2), which reads this setting live — every test that calls Verify
    /// needs it seeded, same upsert pattern as ScheduleGenerationServiceTests.</summary>
    private static async Task SeedCertificateHoursSettingAsync(MvTeachesDbContext db, int hours = 30)
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

    private static PayrollService CreatePayrollService(MvTeachesDbContext db, IClock clock) =>
        new(db, new PayrollRateResolver(db), new CertificateService(db, new SettingsProvider(db, clock), clock), clock);

    private record Fixture(int CountryId, long CourseId, int LevelId, int AgeGroupId, long TeacherId,
        long TeacherUserId, long AdminUserId, long SessionId, ClassSession Session);

    private async Task<Fixture> SeedDeclaredDeliveryAsync(MvTeachesDbContext db, decimal rateAmount = 12m,
        RateUnit rateUnit = RateUnit.PerHour, int durationMinutes = 60, bool declare = true)
    {
        await SeedCertificateHoursSettingAsync(db);

        var countryId = (int)NextId();
        var courseId = NextId();
        var levelId = (int)NextId();
        var ageGroupId = (int)NextId();
        var teacherUserId = await CreateUserAsync(db, "teacher");
        var adminUserId = await CreateUserAsync(db, "admin");

        db.Countries.Add(new Country(countryId, TwoLetterCode(countryId), "دولة", "Country", "JOD", "+962", "Asia/Amman"));
        db.Courses.Add(new Course("C" + courseId, "دورة", "Course"));
        db.Levels.Add(new Level(levelId, "L" + levelId, "مستوى", "Level", levelId));
        db.AgeGroups.Add(new AgeGroup(ageGroupId, "A" + ageGroupId, 5, 12, true));
        var teacher = new Teacher(teacherUserId, "Teacher", "Asia/Amman");
        db.Teachers.Add(teacher);
        await db.SaveChangesAsync();

        db.TeacherRates.Add(new TeacherRate(teacher.Id, null, null, null, new Money(rateAmount, "JOD"),
            rateUnit, new LocalDate(2020, 1, 1), adminUserId));
        await db.SaveChangesAsync();

        var now = SystemClock.Instance.GetCurrentInstant();
        var session = new ClassSession(countryId, null, courseId, levelId, ageGroupId, teacher.Id,
            now.Minus(Duration.FromDays(1)), now.Minus(Duration.FromDays(1)).Plus(Duration.FromMinutes(durationMinutes)),
            "Asia/Amman", "17:00", SessionType.Group, now.Minus(Duration.FromDays(2)));
        db.ClassSessions.Add(session);
        await db.SaveChangesAsync();

        var fx = new Fixture(countryId, courseId, levelId, ageGroupId, teacher.Id, teacherUserId, adminUserId, session.Id, session);

        if (declare)
        {
            var service = CreatePayrollService(db, new FakeClock(now));
            var result = await service.DeclareAsync(session.Id, teacherUserId, durationMinutes, "delivered fine", CancellationToken.None);
            Assert.Equal(DeclareDeliveryOutcome.Declared, result.Outcome);
        }

        return fx;
    }

    [Fact]
    public async Task Declare_then_verify_snapshots_the_hourly_rate_and_the_scheduled_duration()
    {
        await using var db = _fixture.CreateContext();
        var fx = await SeedDeclaredDeliveryAsync(db, rateAmount: 12m, rateUnit: RateUnit.PerHour, durationMinutes: 60);

        var now = SystemClock.Instance.GetCurrentInstant();
        var service = CreatePayrollService(db, new FakeClock(now));
        var result = await service.VerifyAsync(fx.SessionId, fx.AdminUserId, "looks good", CancellationToken.None);

        Assert.Equal(VerifyDeliveryOutcome.Verified, result.Outcome);

        await using var verifyDb = _fixture.CreateContext();
        var delivery = await verifyDb.SessionDeliveries.FirstAsync(d => d.SessionId == fx.SessionId);
        Assert.Equal(DeliveryState.Verified, delivery.State);
        Assert.Equal(60, delivery.VerifiedMinutes);
        Assert.Equal(12m, delivery.PayableAmount); // 60/60 * 12
    }

    [Fact]
    public async Task A_per_session_rate_pays_a_flat_amount_regardless_of_duration()
    {
        await using var db = _fixture.CreateContext();
        var fx = await SeedDeclaredDeliveryAsync(db, rateAmount: 20m, rateUnit: RateUnit.PerSession, durationMinutes: 90);

        var now = SystemClock.Instance.GetCurrentInstant();
        var service = CreatePayrollService(db, new FakeClock(now));
        var result = await service.VerifyAsync(fx.SessionId, fx.AdminUserId, null, CancellationToken.None);

        Assert.Equal(VerifyDeliveryOutcome.Verified, result.Outcome);

        await using var verifyDb = _fixture.CreateContext();
        var delivery = await verifyDb.SessionDeliveries.FirstAsync(d => d.SessionId == fx.SessionId);
        Assert.Equal(90, delivery.VerifiedMinutes); // still the scheduled duration (D-59/D-62)...
        Assert.Equal(20m, delivery.PayableAmount);  // ...but pay is the flat per-session rate, not scaled by it
    }

    [Fact]
    public async Task The_same_person_cannot_declare_and_then_verify_their_own_delivery()
    {
        await using var db = _fixture.CreateContext();
        var fx = await SeedDeclaredDeliveryAsync(db);

        var service = CreatePayrollService(db, new FakeClock(SystemClock.Instance.GetCurrentInstant()));
        var result = await service.VerifyAsync(fx.SessionId, fx.TeacherUserId, null, CancellationToken.None);

        Assert.Equal(VerifyDeliveryOutcome.SameActorAsDeclarer, result.Outcome);

        await using var verifyDb = _fixture.CreateContext();
        Assert.Equal(DeliveryState.Declared, (await verifyDb.SessionDeliveries.FirstAsync(d => d.SessionId == fx.SessionId)).State);
    }

    [Fact]
    public async Task Verifying_without_an_applicable_teacher_rate_is_rejected_before_writing_anything()
    {
        await using var db = _fixture.CreateContext();
        var fx = await SeedDeclaredDeliveryAsync(db);

        // Remove the rate that SeedDeclaredDeliveryAsync just created, so none applies.
        var rate = await db.TeacherRates.FirstAsync(r => r.TeacherId == fx.TeacherId);
        db.TeacherRates.Remove(rate);
        await db.SaveChangesAsync();

        var service = CreatePayrollService(db, new FakeClock(SystemClock.Instance.GetCurrentInstant()));
        var result = await service.VerifyAsync(fx.SessionId, fx.AdminUserId, null, CancellationToken.None);

        Assert.Equal(VerifyDeliveryOutcome.NoApplicableRate, result.Outcome);

        await using var verifyDb = _fixture.CreateContext();
        Assert.Equal(DeliveryState.Declared, (await verifyDb.SessionDeliveries.FirstAsync(d => d.SessionId == fx.SessionId)).State);
    }

    [Fact]
    public async Task A_notdelivered_session_can_never_be_declared()
    {
        await using var db = _fixture.CreateContext();
        var fx = await SeedDeclaredDeliveryAsync(db, declare: false);

        var session = await db.ClassSessions.FirstAsync(s => s.Id == fx.SessionId);
        session.MarkNotDelivered();
        await db.SaveChangesAsync();

        var service = CreatePayrollService(db, new FakeClock(SystemClock.Instance.GetCurrentInstant()));
        var result = await service.DeclareAsync(fx.SessionId, fx.TeacherUserId, 60, null, CancellationToken.None);

        Assert.Equal(DeclareDeliveryOutcome.SessionNotDelivered, result.Outcome);

        await using var verifyDb = _fixture.CreateContext();
        Assert.False(await verifyDb.SessionDeliveries.AnyAsync(d => d.SessionId == fx.SessionId));
    }

    [Fact]
    public async Task Rejecting_a_declared_delivery_moves_it_out_of_the_pipeline()
    {
        await using var db = _fixture.CreateContext();
        var fx = await SeedDeclaredDeliveryAsync(db);

        var service = CreatePayrollService(db, new FakeClock(SystemClock.Instance.GetCurrentInstant()));
        var result = await service.RejectAsync(fx.SessionId, fx.AdminUserId, "teacher no-show, contradicts declaration", CancellationToken.None);

        Assert.Equal(RejectDeliveryOutcome.Rejected, result.Outcome);

        await using var verifyDb = _fixture.CreateContext();
        Assert.Equal(DeliveryState.Rejected, (await verifyDb.SessionDeliveries.FirstAsync(d => d.SessionId == fx.SessionId)).State);
    }

    [Fact]
    public async Task Full_cycle_aggregate_review_approve_pay_produces_exactly_one_line_and_marks_delivery_paid()
    {
        await using var db = _fixture.CreateContext();
        var fx = await SeedDeclaredDeliveryAsync(db, rateAmount: 15m, rateUnit: RateUnit.PerHour, durationMinutes: 60);

        var now = SystemClock.Instance.GetCurrentInstant();
        var service = CreatePayrollService(db, new FakeClock(now));

        var verifyResult = await service.VerifyAsync(fx.SessionId, fx.AdminUserId, null, CancellationToken.None);
        Assert.Equal(VerifyDeliveryOutcome.Verified, verifyResult.Outcome);

        var zone = DateTimeZoneProviders.Tzdb["Asia/Amman"];
        var sessionLocalDate = fx.Session.StartsAtUtc.InZone(zone).Date;
        var period = await service.OpenPeriodAsync(fx.CountryId, sessionLocalDate.PlusDays(-3), sessionLocalDate.PlusDays(3), CancellationToken.None);

        var linesCreated = await service.AggregateVerifiedDeliveriesAsync(period.PeriodId, CancellationToken.None);
        Assert.Equal(1, linesCreated);

        // Re-running aggregation must not create a second line for the same session.
        var secondPass = await service.AggregateVerifiedDeliveriesAsync(period.PeriodId, CancellationToken.None);
        Assert.Equal(0, secondPass);

        await service.MoveToReviewAsync(period.PeriodId, CancellationToken.None);
        await service.ApprovePeriodAsync(period.PeriodId, fx.AdminUserId, CancellationToken.None);
        await service.MarkPeriodPaidAsync(period.PeriodId, CancellationToken.None);
        await service.ClosePeriodAsync(period.PeriodId, CancellationToken.None);

        await using var verifyDb = _fixture.CreateContext();
        var lines = verifyDb.PayrollLines.Where(l => l.PeriodId == period.PeriodId).ToList();
        Assert.Single(lines);
        Assert.Equal(fx.SessionId, lines[0].SessionId);
        Assert.Equal(15m, lines[0].Amount);

        var finalPeriod = await verifyDb.PayrollPeriods.FirstAsync(p => p.Id == period.PeriodId);
        Assert.Equal(PayrollPeriodStatus.Closed, finalPeriod.Status);

        var finalDelivery = await verifyDb.SessionDeliveries.FirstAsync(d => d.SessionId == fx.SessionId);
        Assert.Equal(DeliveryState.Paid, finalDelivery.State);
        Assert.Equal(period.PeriodId, finalDelivery.PayrollPeriodId);
    }

    /// <summary>
    /// Owner decision 2026-08-30, recorded as an explicit regression guard
    /// after the owner-demo simulator was found to generate one payroll line
    /// PER ATTENDING STUDENT. Production has never had that defect — pay is
    /// anchored to <see cref="SessionDelivery"/>, whose primary key IS the
    /// session id — and this test exists so that can never silently change.
    ///
    /// Four students attend one 60-minute session at 8 JOD/hour: the teacher
    /// is paid 8 JOD once, not 32 JOD.
    /// </summary>
    [Fact]
    public async Task A_session_with_many_attending_students_still_produces_exactly_one_payroll_line()
    {
        await using var db = _fixture.CreateContext();
        var fx = await SeedDeclaredDeliveryAsync(db, rateAmount: 8m, rateUnit: RateUnit.PerHour, durationMinutes: 60);

        // Fill every seat — the whole point is that this must not scale the pay.
        for (var i = 0; i < 4; i++)
        {
            var studentUserId = await CreateUserAsync(db, $"student{i}");
            var student = new Student(fx.CountryId, $"Student {i}", new LocalDate(2012, 3, 4), studentUserId);
            db.Students.Add(student);
            await db.SaveChangesAsync();
            db.SessionEnrollments.Add(new SessionEnrollment(fx.SessionId, student.Id, fx.AgeGroupId,
                fx.AdminUserId, SystemClock.Instance.GetCurrentInstant()));
        }

        await db.SaveChangesAsync();
        Assert.Equal(4, await db.SessionEnrollments.CountAsync(e => e.SessionId == fx.SessionId));

        var now = SystemClock.Instance.GetCurrentInstant();
        var service = CreatePayrollService(db, new FakeClock(now));
        await service.VerifyAsync(fx.SessionId, fx.AdminUserId, null, CancellationToken.None);

        var zone = DateTimeZoneProviders.Tzdb["Asia/Amman"];
        var sessionLocalDate = fx.Session.StartsAtUtc.InZone(zone).Date;
        var period = await service.OpenPeriodAsync(fx.CountryId, sessionLocalDate.PlusDays(-3), sessionLocalDate.PlusDays(3), CancellationToken.None);

        var linesCreated = await service.AggregateVerifiedDeliveriesAsync(period.PeriodId, CancellationToken.None);

        Assert.Equal(1, linesCreated);

        await using var verify = _fixture.CreateContext();
        var lines = await verify.PayrollLines.Where(l => l.PeriodId == period.PeriodId).ToListAsync();
        Assert.Single(lines);
        Assert.Equal(8m, lines[0].Amount);   // 8 JOD/hour x 60 min, NOT 8 x 4 students
    }

    /// <summary>The other half of the owner's stated formula: rate x scheduled
    /// minutes / 60, so a 90-minute session at 8 JOD/hour pays 12 JOD.</summary>
    [Fact]
    public async Task A_ninety_minute_session_pays_one_and_a_half_times_the_hourly_rate()
    {
        await using var db = _fixture.CreateContext();
        var fx = await SeedDeclaredDeliveryAsync(db, rateAmount: 8m, rateUnit: RateUnit.PerHour, durationMinutes: 90);

        var now = SystemClock.Instance.GetCurrentInstant();
        var service = CreatePayrollService(db, new FakeClock(now));
        await service.VerifyAsync(fx.SessionId, fx.AdminUserId, null, CancellationToken.None);

        await using var verify = _fixture.CreateContext();
        var delivery = await verify.SessionDeliveries.FirstAsync(d => d.SessionId == fx.SessionId);
        Assert.Equal(90, delivery.VerifiedMinutes);
        Assert.Equal(12m, delivery.PayableAmount);
    }

    /// <summary>
    /// Owner decision 2026-08-30: "Provider interruptions or continuation
    /// meeting segments must not create additional payroll items." A second
    /// provisioned meeting for the same session (what a Zoom Basic cut-off
    /// forces) must leave payroll completely untouched, because payroll is
    /// anchored to the session and never to the meeting.
    /// </summary>
    [Fact]
    public async Task A_second_meeting_segment_for_the_same_session_adds_no_payroll_item()
    {
        await using var db = _fixture.CreateContext();
        var fx = await SeedDeclaredDeliveryAsync(db, rateAmount: 8m, rateUnit: RateUnit.PerHour, durationMinutes: 90);

        var now = SystemClock.Instance.GetCurrentInstant();
        var service = CreatePayrollService(db, new FakeClock(now));
        await service.VerifyAsync(fx.SessionId, fx.AdminUserId, null, CancellationToken.None);

        var zone = DateTimeZoneProviders.Tzdb["Asia/Amman"];
        var sessionLocalDate = fx.Session.StartsAtUtc.InZone(zone).Date;
        var period = await service.OpenPeriodAsync(fx.CountryId, sessionLocalDate.PlusDays(-3), sessionLocalDate.PlusDays(3), CancellationToken.None);
        Assert.Equal(1, await service.AggregateVerifiedDeliveriesAsync(period.PeriodId, CancellationToken.None));

        // Simulate the continuation segment: a second meeting row against the
        // SAME session. Payroll must not notice.
        var connection = new MVTeaches.Domain.Integrations.TeacherMeetingConnection(fx.TeacherId,
            MVTeaches.Domain.Integrations.VideoProviderType.Zoom, "acct-1", null, "enc-a", "enc-r", null, now);
        db.TeacherMeetingConnections.Add(connection);
        await db.SaveChangesAsync();
        db.ProvisionedMeetings.Add(new MVTeaches.Domain.Integrations.ProvisionedMeeting(fx.SessionId,
            connection.Id, MVTeaches.Domain.Integrations.VideoProviderType.Zoom, now));
        await db.SaveChangesAsync();

        var afterSegment = await service.AggregateVerifiedDeliveriesAsync(period.PeriodId, CancellationToken.None);

        Assert.Equal(0, afterSegment);
        await using var verify = _fixture.CreateContext();
        Assert.Single(await verify.PayrollLines.Where(l => l.PeriodId == period.PeriodId).ToListAsync());
    }

    [Fact]
    public async Task A_delivery_outside_the_periods_date_range_is_not_aggregated()
    {
        await using var db = _fixture.CreateContext();
        var fx = await SeedDeclaredDeliveryAsync(db);

        var now = SystemClock.Instance.GetCurrentInstant();
        var service = CreatePayrollService(db, new FakeClock(now));
        await service.VerifyAsync(fx.SessionId, fx.AdminUserId, null, CancellationToken.None);

        var zone = DateTimeZoneProviders.Tzdb["Asia/Amman"];
        var sessionLocalDate = fx.Session.StartsAtUtc.InZone(zone).Date;
        // A period that starts well AFTER the session's own date.
        var period = await service.OpenPeriodAsync(fx.CountryId, sessionLocalDate.PlusDays(10), sessionLocalDate.PlusDays(20), CancellationToken.None);

        var linesCreated = await service.AggregateVerifiedDeliveriesAsync(period.PeriodId, CancellationToken.None);

        Assert.Equal(0, linesCreated);
    }

    [Fact]
    public async Task An_approved_period_cannot_aggregate_more_lines()
    {
        await using var db = _fixture.CreateContext();
        var countryId = (int)NextId();
        db.Countries.Add(new Country(countryId, TwoLetterCode(countryId), "دولة", "Country", "JOD", "+962", "Asia/Amman"));
        await db.SaveChangesAsync();

        var now = SystemClock.Instance.GetCurrentInstant();
        var service = CreatePayrollService(db, new FakeClock(now));
        var today = now.InZone(DateTimeZoneProviders.Tzdb["Asia/Amman"]).Date;

        var period = await service.OpenPeriodAsync(countryId, today.PlusDays(-5), today.PlusDays(5), CancellationToken.None);
        await service.MoveToReviewAsync(period.PeriodId, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AggregateVerifiedDeliveriesAsync(period.PeriodId, CancellationToken.None));
    }
}
