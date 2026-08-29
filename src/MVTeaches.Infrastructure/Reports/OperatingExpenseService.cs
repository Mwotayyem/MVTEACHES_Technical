using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Reports;
using MVTeaches.Domain.Common;
using MVTeaches.Domain.Finance;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;

namespace MVTeaches.Infrastructure.Reports;

/// <inheritdoc cref="IOperatingExpenseService"/>
public class OperatingExpenseService : IOperatingExpenseService
{
    private readonly MvTeachesDbContext _db;
    private readonly IClock _clock;

    public OperatingExpenseService(MvTeachesDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<RecordExpenseResult> RecordAsync(int countryId, string category, Money amount,
        LocalDate incurredOn, string? note, long enteredByUserId, CancellationToken cancellationToken)
    {
        if (amount.Amount <= 0)
        {
            return new RecordExpenseResult(RecordExpenseOutcome.InvalidAmount);
        }

        if (category.Trim().Equals("Payroll", StringComparison.OrdinalIgnoreCase))
        {
            return new RecordExpenseResult(RecordExpenseOutcome.PayrollCategoryNotAllowed);
        }

        var expense = new OperatingExpense(countryId, category, amount, incurredOn, note, enteredByUserId, _clock.GetCurrentInstant());
        _db.OperatingExpenses.Add(expense);
        await _db.SaveChangesAsync(cancellationToken);

        return new RecordExpenseResult(RecordExpenseOutcome.Recorded, expense.Id);
    }

    public async Task<IReadOnlyList<OperatingExpense>> ListAsync(LocalDate periodStart, LocalDate periodEnd, CancellationToken cancellationToken) =>
        await _db.OperatingExpenses
            .Where(e => e.IncurredOn >= periodStart && e.IncurredOn <= periodEnd)
            .OrderByDescending(e => e.IncurredOn)
            .ToListAsync(cancellationToken);
}
