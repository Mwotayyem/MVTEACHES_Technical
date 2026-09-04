using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Payroll;
using MVTeaches.Domain.Common;
using MVTeaches.Domain.Payroll;
using MVTeaches.Domain.People;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Payroll;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;
using Xunit;

namespace MVTeaches.Tests.Payroll;

/// <summary>
/// §9.2 (D-27) — before this service existed there was no way to create a
/// TeacherRate anywhere in the application, which meant
/// IPayrollService.VerifyAsync could never actually succeed against a real
/// teacher (IPayrollRateResolver, already tested, always found an empty table).
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class TeacherRateServiceTests
{
    private readonly TestDatabaseFixture _fixture;
    private static long _idSeed = 43_000_000; // a range distinct from every other test class sharing this DB

    public TeacherRateServiceTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static long NextId() => Interlocked.Increment(ref _idSeed);

    private static async Task<long> SeedTeacherAsync(MvTeachesDbContext db)
    {
        var user = new ApplicationUser
        {
            UserName = $"u-{Guid.NewGuid():N}",
            NormalizedUserName = $"U-{Guid.NewGuid():N}",
            Email = $"{Guid.NewGuid():N}@test.mvteaches.local",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var teacher = new Teacher(user.Id, "Teacher", "Asia/Amman");
        db.Teachers.Add(teacher);
        await db.SaveChangesAsync();
        return teacher.Id;
    }

    [Fact]
    public async Task Creating_a_rate_persists_its_specificity_dimensions_and_amount()
    {
        await using var db = _fixture.CreateContext();
        var teacherId = await SeedTeacherAsync(db);
        var service = new TeacherRateService(db);

        var result = await service.CreateRateAsync(teacherId, courseId: null, levelId: null, ageGroupId: null,
            new Money(15m, "JOD"), RateUnit.PerHour, new LocalDate(2026, 1, 1), createdByUserId: NextId(), CancellationToken.None);

        var rate = await db.TeacherRates.FirstAsync(r => r.Id == result.TeacherRateId);
        Assert.Equal(teacherId, rate.TeacherId);
        Assert.Null(rate.CourseId);
        Assert.Equal(15m, rate.Rate.Amount);
        Assert.Equal("JOD", rate.Rate.Currency);
        Assert.Equal(RateUnit.PerHour, rate.Unit);
        Assert.Equal(0, rate.Specificity); // the teacher's own default — least specific
    }

    [Fact]
    public async Task A_negative_rate_is_rejected()
    {
        await using var db = _fixture.CreateContext();
        var teacherId = await SeedTeacherAsync(db);
        var service = new TeacherRateService(db);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.CreateRateAsync(
            teacherId, null, null, null, new Money(-5m, "JOD"), RateUnit.PerHour, new LocalDate(2026, 1, 1), NextId(), CancellationToken.None));
    }

    /// <summary>
    /// Owner decision 2026-09-04. Before this, CreateRateAsync was a bare
    /// INSERT and <c>TeacherRate.Close</c> was dead code — never called
    /// anywhere in the application. Giving a teacher a raise therefore left
    /// TWO open rates for the same combination, and PayrollRateResolver's
    /// <c>OrderByDescending(Specificity).FirstOrDefault()</c> had nothing to
    /// break the tie between them: which figure the teacher was actually paid
    /// depended on the order the database returned rows in.
    /// </summary>
    [Fact]
    public async Task Raising_a_rate_closes_the_previous_one_for_the_same_combination()
    {
        await using var db = _fixture.CreateContext();
        var teacherId = await SeedTeacherAsync(db);
        var service = new TeacherRateService(db);
        var courseId = NextId();
        var levelId = (int)NextId();
        var ageGroupId = (int)NextId();

        var first = await service.CreateRateAsync(teacherId, courseId, levelId, ageGroupId,
            new Money(10m, "JOD"), RateUnit.PerHour, new LocalDate(2026, 1, 1), NextId(), CancellationToken.None);
        var second = await service.CreateRateAsync(teacherId, courseId, levelId, ageGroupId,
            new Money(12m, "JOD"), RateUnit.PerHour, new LocalDate(2026, 3, 1), NextId(), CancellationToken.None);

        Assert.Equal(CreateTeacherRateOutcome.Created, first.Outcome);
        Assert.Equal(CreateTeacherRateOutcome.Created, second.Outcome);

        var closed = await db.TeacherRates.AsNoTracking().FirstAsync(r => r.Id == first.TeacherRateId);
        var open = await db.TeacherRates.AsNoTracking().FirstAsync(r => r.Id == second.TeacherRateId);

        // The old rate ends exactly where the new one starts — no gap, no overlap.
        Assert.Equal(new LocalDate(2026, 3, 1), closed.EffectiveTo);
        Assert.Null(open.EffectiveTo);

        var stillOpen = await db.TeacherRates.AsNoTracking()
            .Where(r => r.TeacherId == teacherId && r.EffectiveTo == null)
            .ToListAsync();
        Assert.Single(stillOpen);
    }

    /// <summary>The owner's own acceptance condition: after the new rate's
    /// date, the new rate is the one used. Asserted through the real resolver
    /// rather than by reading the rows back, because the resolver is what
    /// payroll actually asks.</summary>
    [Fact]
    public async Task The_newer_rate_is_the_one_payroll_resolves_after_its_start_date()
    {
        await using var db = _fixture.CreateContext();
        var teacherId = await SeedTeacherAsync(db);
        var service = new TeacherRateService(db);
        var resolver = new PayrollRateResolver(db);
        var courseId = NextId();
        var levelId = (int)NextId();
        var ageGroupId = (int)NextId();

        await service.CreateRateAsync(teacherId, courseId, levelId, ageGroupId,
            new Money(10m, "JOD"), RateUnit.PerHour, new LocalDate(2026, 1, 1), NextId(), CancellationToken.None);
        await service.CreateRateAsync(teacherId, courseId, levelId, ageGroupId,
            new Money(12m, "JOD"), RateUnit.PerHour, new LocalDate(2026, 3, 1), NextId(), CancellationToken.None);

        // Before the raise: the old figure, because past pay must not change.
        var before = await resolver.ResolveAsync(teacherId, courseId, levelId, ageGroupId,
            new LocalDate(2026, 2, 15), CancellationToken.None);
        Assert.Equal(10m, before!.Rate.Amount);

        // On the day it takes effect, and after it.
        var onTheDay = await resolver.ResolveAsync(teacherId, courseId, levelId, ageGroupId,
            new LocalDate(2026, 3, 1), CancellationToken.None);
        Assert.Equal(12m, onTheDay!.Rate.Amount);

        var after = await resolver.ResolveAsync(teacherId, courseId, levelId, ageGroupId,
            new LocalDate(2026, 6, 30), CancellationToken.None);
        Assert.Equal(12m, after!.Rate.Amount);
    }

    /// <summary>"Same combination" means all three dimensions. A rate for a
    /// different course is a different job and must be left alone — this is
    /// exactly the shape Local Staging already holds (one teacher, two
    /// courses, both open), so closing on teacher alone would have quietly
    /// ended a rate that is still in force.</summary>
    [Fact]
    public async Task A_rate_for_a_different_course_is_not_closed()
    {
        await using var db = _fixture.CreateContext();
        var teacherId = await SeedTeacherAsync(db);
        var service = new TeacherRateService(db);
        var levelId = (int)NextId();
        var ageGroupId = (int)NextId();

        var otherCourse = await service.CreateRateAsync(teacherId, NextId(), levelId, ageGroupId,
            new Money(10m, "JOD"), RateUnit.PerHour, new LocalDate(2026, 1, 1), NextId(), CancellationToken.None);
        await service.CreateRateAsync(teacherId, NextId(), levelId, ageGroupId,
            new Money(20m, "JOD"), RateUnit.PerHour, new LocalDate(2026, 3, 1), NextId(), CancellationToken.None);

        var untouched = await db.TeacherRates.AsNoTracking().FirstAsync(r => r.Id == otherCourse.TeacherRateId);
        Assert.Null(untouched.EffectiveTo);
    }

    /// <summary>The wildcard dimensions are part of the identity too: a
    /// teacher-wide default (all nulls) is a different combination from a
    /// course-specific rate, and must not close it.</summary>
    [Fact]
    public async Task A_teacher_wide_default_does_not_close_a_course_specific_rate()
    {
        await using var db = _fixture.CreateContext();
        var teacherId = await SeedTeacherAsync(db);
        var service = new TeacherRateService(db);

        var specific = await service.CreateRateAsync(teacherId, NextId(), (int)NextId(), (int)NextId(),
            new Money(18m, "JOD"), RateUnit.PerHour, new LocalDate(2026, 1, 1), NextId(), CancellationToken.None);
        var fallback = await service.CreateRateAsync(teacherId, null, null, null,
            new Money(9m, "JOD"), RateUnit.PerHour, new LocalDate(2026, 3, 1), NextId(), CancellationToken.None);

        Assert.Equal(CreateTeacherRateOutcome.Created, fallback.Outcome);
        var stillOpen = await db.TeacherRates.AsNoTracking().FirstAsync(r => r.Id == specific.TeacherRateId);
        Assert.Null(stillOpen.EffectiveTo);
    }

    /// <summary>A rate cannot both start and be replaced on the same day: the
    /// period would be zero-length, which <c>TeacherRate.Close</c> and the
    /// database's own ck_rate_effective_range both refuse. Rather than leave
    /// two open rates — the exact ambiguity this rule exists to prevent — the
    /// service refuses and writes nothing at all.</summary>
    [Fact]
    public async Task A_second_rate_starting_the_same_day_is_refused_and_writes_nothing()
    {
        await using var db = _fixture.CreateContext();
        var teacherId = await SeedTeacherAsync(db);
        var service = new TeacherRateService(db);
        var courseId = NextId();
        var levelId = (int)NextId();
        var ageGroupId = (int)NextId();
        var sameDay = new LocalDate(2026, 5, 1);

        await service.CreateRateAsync(teacherId, courseId, levelId, ageGroupId,
            new Money(10m, "JOD"), RateUnit.PerHour, sameDay, NextId(), CancellationToken.None);
        var second = await service.CreateRateAsync(teacherId, courseId, levelId, ageGroupId,
            new Money(14m, "JOD"), RateUnit.PerHour, sameDay, NextId(), CancellationToken.None);

        Assert.Equal(CreateTeacherRateOutcome.DuplicateStartDate, second.Outcome);
        Assert.Null(second.TeacherRateId);
        Assert.Equal(sameDay, second.ExistingEffectiveFrom);

        // Nothing written: still one rate, still open, still the original figure.
        var rates = await db.TeacherRates.AsNoTracking().Where(r => r.TeacherId == teacherId).ToListAsync();
        var only = Assert.Single(rates);
        Assert.Equal(10m, only.Rate.Amount);
        Assert.Null(only.EffectiveTo);
    }

    /// <summary>Backdating is refused for the same reason from the other
    /// direction: closing the existing rate at an earlier date would end it
    /// before it began.</summary>
    [Fact]
    public async Task A_rate_starting_before_an_existing_one_is_refused_and_writes_nothing()
    {
        await using var db = _fixture.CreateContext();
        var teacherId = await SeedTeacherAsync(db);
        var service = new TeacherRateService(db);
        var courseId = NextId();
        var levelId = (int)NextId();
        var ageGroupId = (int)NextId();

        await service.CreateRateAsync(teacherId, courseId, levelId, ageGroupId,
            new Money(10m, "JOD"), RateUnit.PerHour, new LocalDate(2026, 6, 1), NextId(), CancellationToken.None);
        var backdated = await service.CreateRateAsync(teacherId, courseId, levelId, ageGroupId,
            new Money(14m, "JOD"), RateUnit.PerHour, new LocalDate(2026, 2, 1), NextId(), CancellationToken.None);

        Assert.Equal(CreateTeacherRateOutcome.StartsBeforeExistingRate, backdated.Outcome);
        Assert.Null(backdated.TeacherRateId);
        Assert.Single(await db.TeacherRates.AsNoTracking().Where(r => r.TeacherId == teacherId).ToListAsync());
    }

    /// <summary>Defence in depth for data written before the rule above
    /// existed: two equally-specific open rates used to resolve arbitrarily.
    /// Inserted directly, bypassing the service, precisely because the service
    /// will no longer produce this state.</summary>
    [Fact]
    public async Task Two_legacy_open_rates_resolve_to_the_later_one_rather_than_arbitrarily()
    {
        await using var db = _fixture.CreateContext();
        var teacherId = await SeedTeacherAsync(db);
        var courseId = NextId();
        var levelId = (int)NextId();
        var ageGroupId = (int)NextId();

        db.TeacherRates.Add(new TeacherRate(teacherId, courseId, levelId, ageGroupId,
            new Money(10m, "JOD"), RateUnit.PerHour, new LocalDate(2026, 1, 1), NextId()));
        db.TeacherRates.Add(new TeacherRate(teacherId, courseId, levelId, ageGroupId,
            new Money(12m, "JOD"), RateUnit.PerHour, new LocalDate(2026, 3, 1), NextId()));
        await db.SaveChangesAsync();

        var resolved = await new PayrollRateResolver(db).ResolveAsync(teacherId, courseId, levelId, ageGroupId,
            new LocalDate(2026, 9, 1), CancellationToken.None);

        Assert.Equal(12m, resolved!.Rate.Amount);
    }
}
