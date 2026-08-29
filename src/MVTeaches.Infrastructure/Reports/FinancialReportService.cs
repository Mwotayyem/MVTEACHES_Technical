using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Reports;
using MVTeaches.Domain.Payments;
using MVTeaches.Domain.People;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;

namespace MVTeaches.Infrastructure.Reports;

/// <summary>See IFinancialReportService's remarks.</summary>
public class FinancialReportService : IFinancialReportService
{
    private readonly MvTeachesDbContext _db;

    public FinancialReportService(MvTeachesDbContext db) => _db = db;

    public async Task<FinancialReport> GenerateAsync(LocalDate periodStart, LocalDate periodEnd, CancellationToken cancellationToken)
    {
        if (periodEnd < periodStart)
        {
            throw new ArgumentException("periodEnd must not be before periodStart.");
        }

        // UTC calendar days — the same simplification as the Dashboard's
        // "sessions today" (a true per-country-timezone report is future work).
        var startInstant = periodStart.AtMidnight().InUtc().ToInstant();
        var endInstant = periodEnd.PlusDays(1).AtMidnight().InUtc().ToInstant();

        var revenueByCurrency = await _db.Payments
            .Where(p => p.Status == PaymentStatus.Confirmed && p.ConfirmedAtUtc >= startInstant && p.ConfirmedAtUtc < endInstant)
            .GroupBy(p => p.Amount.Currency)
            .Select(g => new CurrencyAmount(g.Key, g.Sum(p => p.Amount.Amount)))
            .ToListAsync(cancellationToken);

        var payrollCostByCurrency = await _db.PayrollLines
            .Join(_db.PayrollPeriods, line => line.PeriodId, period => period.Id, (line, period) => new { line, period })
            .Where(x => x.period.PeriodStart >= periodStart && x.period.PeriodEnd <= periodEnd)
            .GroupBy(x => x.line.RateCurrency)
            .Select(g => new CurrencyAmount(g.Key, g.Sum(x => x.line.Amount)))
            .ToListAsync(cancellationToken);

        var paymentBlockedStudents = await _db.Students.CountAsync(s => s.Status == StudentStatus.PaymentBlocked, cancellationToken);
        var pendingPayments = await _db.Payments.CountAsync(p => p.Status == PaymentStatus.Pending, cancellationToken);

        // Owner decision 2026-08-30 rule 9: "sessions/scheduled teaching
        // hours" — every session SCHEDULED to start in the period, regardless
        // of whether it has actually happened yet or how it was later
        // resolved (Cancelled sessions are excluded — they were never
        // actually taught).
        var scheduledTeachingMinutes = await _db.ClassSessions
            .Where(s => s.StartsAtUtc >= startInstant && s.StartsAtUtc < endInstant && s.Status != ClassSessionStatus.Cancelled)
            .SumAsync(s => (int?)s.DurationMinutes, cancellationToken) ?? 0;

        var expensesByCurrency = await _db.OperatingExpenses
            .Where(e => e.IncurredOn >= periodStart && e.IncurredOn <= periodEnd)
            .GroupBy(e => e.Amount.Currency)
            .Select(g => new CurrencyAmount(g.Key, g.Sum(e => e.Amount.Amount)))
            .ToListAsync(cancellationToken);

        var netProfitByCurrency = revenueByCurrency
            .Select(c => c.Currency)
            .Concat(payrollCostByCurrency.Select(c => c.Currency))
            .Concat(expensesByCurrency.Select(c => c.Currency))
            .Distinct()
            .Select(currency =>
            {
                var revenue = revenueByCurrency.FirstOrDefault(c => c.Currency == currency)?.Amount ?? 0m;
                var payroll = payrollCostByCurrency.FirstOrDefault(c => c.Currency == currency)?.Amount ?? 0m;
                var expenses = expensesByCurrency.FirstOrDefault(c => c.Currency == currency)?.Amount ?? 0m;
                return new CurrencyAmount(currency, revenue - payroll - expenses);
            })
            .ToList();

        return new FinancialReport(periodStart, periodEnd, revenueByCurrency, payrollCostByCurrency,
            paymentBlockedStudents, pendingPayments, scheduledTeachingMinutes, expensesByCurrency, netProfitByCurrency);
    }
}
