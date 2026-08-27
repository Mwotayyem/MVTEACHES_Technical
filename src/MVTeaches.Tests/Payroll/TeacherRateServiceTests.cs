using Microsoft.EntityFrameworkCore;
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
}
