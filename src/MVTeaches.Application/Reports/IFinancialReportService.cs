using NodaTime;

namespace MVTeaches.Application.Reports;

/// <summary>A currency is never summed across another currency — money isn't
/// fungible across them, so a report always reports a LIST of these, one per
/// currency actually seen, rather than one misleading combined total.</summary>
public record CurrencyAmount(string Currency, decimal Amount);

public record FinancialReport(
    LocalDate PeriodStart,
    LocalDate PeriodEnd,
    IReadOnlyList<CurrencyAmount> RevenueByCurrency,
    IReadOnlyList<CurrencyAmount> PayrollCostByCurrency,
    int PaymentBlockedStudents,
    int PendingPayments);

/// <summary>
/// The owner's own stated MVP scope names "تقارير مالية أساسية" (basic
/// financial reports) explicitly — this is that, and deliberately no more:
/// three plain, real numbers computed live from Payments/PayrollLines/
/// Students, no dashboards-within-dashboards, no forecasting, no invented
/// metrics. Every number here is read live; nothing is cached or precomputed.
/// </summary>
public interface IFinancialReportService
{
    /// <summary>Revenue = confirmed payments whose ConfirmedAtUtc falls within
    /// the period (cash-basis, the simplest honest definition). Payroll cost =
    /// PayrollLines belonging to a period fully within the requested range.
    /// PaymentBlockedStudents/PendingPayments are current-state counts, not
    /// date-ranged — they answer "what needs attention right now."</summary>
    Task<FinancialReport> GenerateAsync(LocalDate periodStart, LocalDate periodEnd, CancellationToken cancellationToken);
}
