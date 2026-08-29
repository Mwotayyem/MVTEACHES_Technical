using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Reports;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Common;
using MVTeaches.Infrastructure.Persistence;
using MVTeaches.Infrastructure.Reports;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace MVTeaches.Tests.Reports;

/// <summary>Owner decision 2026-08-30 rule 9: "manually entered operating
/// expenses" — plain admin-recorded facts, with teacher payroll explicitly
/// barred from entering through this path.</summary>
[Collection(nameof(DatabaseCollection))]
public class OperatingExpenseServiceTests
{
    private readonly TestDatabaseFixture _fixture;
    private static long _idSeed = 98_000_000; // a range distinct from every other test class sharing this DB

    public OperatingExpenseServiceTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static long NextId() => Interlocked.Increment(ref _idSeed);

    private static IOperatingExpenseService CreateService(MvTeachesDbContext db, Instant now) => new OperatingExpenseService(db, new FakeClock(now));

    [Fact]
    public async Task Recording_a_valid_expense_succeeds_and_is_listed_within_its_period()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var service = CreateService(db, now);

        var result = await service.RecordAsync(1, "Rent", new Money(200m, "JOD"),
            new LocalDate(2026, 9, 5), "September rent", NextId(), CancellationToken.None);

        Assert.Equal(RecordExpenseOutcome.Recorded, result.Outcome);

        var listed = await service.ListAsync(new LocalDate(2026, 9, 1), new LocalDate(2026, 9, 30), CancellationToken.None);
        Assert.Contains(listed, e => e.Id == result.ExpenseId);
    }

    /// <summary>Owner decision 2026-08-30 rule 9: "Teacher payroll must
    /// never be entered/counted again as a manual expense."</summary>
    [Theory]
    [InlineData("Payroll")]
    [InlineData("payroll")]
    [InlineData(" PAYROLL ")]
    public async Task The_reserved_payroll_category_is_refused(string category)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var service = CreateService(db, now);

        var countBefore = await db.OperatingExpenses.CountAsync();
        var result = await service.RecordAsync(1, category, new Money(100m, "JOD"),
            new LocalDate(2026, 9, 5), null, NextId(), CancellationToken.None);

        Assert.Equal(RecordExpenseOutcome.PayrollCategoryNotAllowed, result.Outcome);
        Assert.Equal(countBefore, await db.OperatingExpenses.CountAsync()); // refused outright — nothing written
    }

    [Fact]
    public async Task A_non_positive_amount_is_refused()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var service = CreateService(db, now);

        var result = await service.RecordAsync(1, "Rent", new Money(0m, "JOD"),
            new LocalDate(2026, 9, 5), null, NextId(), CancellationToken.None);

        Assert.Equal(RecordExpenseOutcome.InvalidAmount, result.Outcome);
    }

    [Fact]
    public async Task ListAsync_excludes_expenses_outside_the_requested_period()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var service = CreateService(db, now);

        await service.RecordAsync(1, "Marketing", new Money(10m, "JOD"), new LocalDate(2026, 1, 15), null, NextId(), CancellationToken.None);
        var inRange = await service.RecordAsync(1, "Marketing", new Money(20m, "JOD"), new LocalDate(2026, 2, 15), null, NextId(), CancellationToken.None);

        var listed = await service.ListAsync(new LocalDate(2026, 2, 1), new LocalDate(2026, 2, 28), CancellationToken.None);

        Assert.Single(listed);
        Assert.Equal(inRange.ExpenseId, listed[0].Id);
    }
}
