using Microsoft.EntityFrameworkCore;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Common;
using MVTeaches.Domain.Payments;
using MVTeaches.Domain.Payroll;
using MVTeaches.Domain.People;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using MVTeaches.Infrastructure.Reports;
using NodaTime;
using Xunit;

namespace MVTeaches.Tests.Reports;

/// <summary>The owner's own stated MVP scope names "basic financial reports"
/// explicitly — this is real, live-computed aggregation, tested against real
/// PostgreSQL 16.</summary>
[Collection(nameof(DatabaseCollection))]
public class FinancialReportServiceTests
{
    private readonly TestDatabaseFixture _fixture;
    private static long _idSeed = 25_000_000; // a range distinct from every other test class sharing this DB

    public FinancialReportServiceTests(TestDatabaseFixture fixture) => _fixture = fixture;

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

    private async Task<(int CountryId, long StudentId, long StudentUserId)> SeedCountryAndStudentAsync(MvTeachesDbContext db)
    {
        var countryId = (int)NextId();
        var studentUserId = await CreateUserAsync(db, "student");
        db.Countries.Add(new Country(countryId, TwoLetterCode(countryId), "دولة", "Country", "JOD", "+962", "Asia/Amman"));
        var student = new Student(countryId, "Student", new LocalDate(2015, 1, 1), studentUserId);
        db.Students.Add(student);
        await db.SaveChangesAsync();
        return (countryId, student.Id, studentUserId);
    }

    [Fact]
    public async Task Only_confirmed_payments_within_range_count_as_revenue()
    {
        await using var db = _fixture.CreateContext();
        var (_, studentId, payerUserId) = await SeedCountryAndStudentAsync(db);
        var admin = await CreateUserAsync(db, "admin");

        var confirmedInRange = new LocalDate(2026, 3, 15).AtMidnight().InUtc().ToInstant();
        var confirmedOutOfRange = new LocalDate(2026, 4, 15).AtMidnight().InUtc().ToInstant();

        var p1 = new Payment(studentId, null, payerUserId, new Money(50m, "JOD"), PaymentMethod.CliQ, "manual", "MVT-" + NextId(), confirmedInRange.Minus(Duration.FromDays(1)));
        db.Payments.Add(p1);
        var p2 = new Payment(studentId, null, payerUserId, new Money(30m, "JOD"), PaymentMethod.CliQ, "manual", "MVT-" + NextId(), confirmedOutOfRange.Minus(Duration.FromDays(1)));
        db.Payments.Add(p2);
        var p3 = new Payment(studentId, null, payerUserId, new Money(999m, "JOD"), PaymentMethod.CliQ, "manual", "MVT-" + NextId(), confirmedInRange.Minus(Duration.FromDays(1)));
        db.Payments.Add(p3); // stays Pending — never confirmed
        await db.SaveChangesAsync();

        p1.Confirm(admin, confirmedInRange);
        p2.Confirm(admin, confirmedOutOfRange);
        await db.SaveChangesAsync();

        var service = new FinancialReportService(db);
        var report = await service.GenerateAsync(new LocalDate(2026, 3, 1), new LocalDate(2026, 3, 31), CancellationToken.None);

        var jod = Assert.Single(report.RevenueByCurrency);
        Assert.Equal("JOD", jod.Currency);
        Assert.Equal(50m, jod.Amount); // only p1 — p2 is out of range, p3 was never confirmed
    }

    [Fact]
    public async Task Revenue_is_reported_separately_per_currency_never_summed_together()
    {
        await using var db = _fixture.CreateContext();
        var (_, studentId, payerUserId) = await SeedCountryAndStudentAsync(db);
        var admin = await CreateUserAsync(db, "admin");
        var confirmedAt = new LocalDate(2026, 5, 10).AtMidnight().InUtc().ToInstant();

        var jodPayment = new Payment(studentId, null, payerUserId, new Money(40m, "JOD"), PaymentMethod.CliQ, "manual", "MVT-" + NextId(), confirmedAt.Minus(Duration.FromHours(1)));
        var gbpPayment = new Payment(studentId, null, payerUserId, new Money(25m, "GBP"), PaymentMethod.Card, "manual", "MVT-" + NextId(), confirmedAt.Minus(Duration.FromHours(1)));
        db.Payments.AddRange(jodPayment, gbpPayment);
        await db.SaveChangesAsync();

        jodPayment.Confirm(admin, confirmedAt);
        gbpPayment.Confirm(admin, confirmedAt);
        await db.SaveChangesAsync();

        var service = new FinancialReportService(db);
        var report = await service.GenerateAsync(new LocalDate(2026, 5, 1), new LocalDate(2026, 5, 31), CancellationToken.None);

        Assert.Equal(2, report.RevenueByCurrency.Count);
        Assert.Contains(report.RevenueByCurrency, r => r.Currency == "JOD" && r.Amount == 40m);
        Assert.Contains(report.RevenueByCurrency, r => r.Currency == "GBP" && r.Amount == 25m);
    }

    [Fact]
    public async Task Payroll_cost_only_counts_periods_fully_within_the_requested_range()
    {
        await using var db = _fixture.CreateContext();
        var countryId = (int)NextId();
        db.Countries.Add(new Country(countryId, TwoLetterCode(countryId), "دولة", "Country", "JOD", "+962", "Asia/Amman"));
        await db.SaveChangesAsync();

        var periodInRange = new PayrollPeriod(countryId, new LocalDate(2026, 6, 1), new LocalDate(2026, 6, 30));
        var periodOutOfRange = new PayrollPeriod(countryId, new LocalDate(2026, 7, 1), new LocalDate(2026, 7, 31));
        db.PayrollPeriods.AddRange(periodInRange, periodOutOfRange);
        await db.SaveChangesAsync();

        db.PayrollLines.Add(new PayrollLine(periodInRange.Id, NextId(), NextId(), 60, 12m, "JOD", 12m));
        db.PayrollLines.Add(new PayrollLine(periodOutOfRange.Id, NextId(), NextId(), 60, 12m, "JOD", 12m));
        await db.SaveChangesAsync();

        var service = new FinancialReportService(db);
        var report = await service.GenerateAsync(new LocalDate(2026, 6, 1), new LocalDate(2026, 6, 30), CancellationToken.None);

        var jod = Assert.Single(report.PayrollCostByCurrency);
        Assert.Equal(12m, jod.Amount); // only the in-range period's line
    }

    [Fact]
    public async Task Payment_blocked_and_pending_counts_are_current_state_not_date_ranged()
    {
        await using var db = _fixture.CreateContext();
        var (countryId, _, _) = await SeedCountryAndStudentAsync(db);

        var blockedStudentUserId = await CreateUserAsync(db, "blocked");
        var blockedStudent = new Student(countryId, "Blocked Student", new LocalDate(2010, 1, 1), blockedStudentUserId);
        blockedStudent.MarkVerified();
        blockedStudent.MarkLevelAssigned();
        blockedStudent.BlockForPayment();
        db.Students.Add(blockedStudent);
        await db.SaveChangesAsync();

        var payerUserId = await CreateUserAsync(db, "payer");
        db.Payments.Add(new Payment(blockedStudent.Id, null, payerUserId, new Money(10m, "JOD"), PaymentMethod.CliQ,
            "manual", "MVT-" + NextId(), SystemClock.Instance.GetCurrentInstant())); // left Pending
        await db.SaveChangesAsync();

        var service = new FinancialReportService(db);
        // A report window that doesn't even overlap "today" — these two counts must still reflect current state.
        var report = await service.GenerateAsync(new LocalDate(2020, 1, 1), new LocalDate(2020, 1, 31), CancellationToken.None);

        Assert.True(report.PaymentBlockedStudents >= 1);
        Assert.True(report.PendingPayments >= 1);
    }

    [Fact]
    public async Task An_end_date_before_the_start_date_is_rejected()
    {
        await using var db = _fixture.CreateContext();
        var service = new FinancialReportService(db);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.GenerateAsync(new LocalDate(2026, 1, 10), new LocalDate(2026, 1, 1), CancellationToken.None));
    }
}
