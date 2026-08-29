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
    int PendingPayments,
    /// <summary>Owner decision 2026-08-30 rule 9 addition. Sum of
    /// ClassSession.DurationMinutes for every session scheduled to start
    /// within the period, regardless of currency (a duration, not money) —
    /// "sessions/scheduled teaching hours".</summary>
    int ScheduledTeachingMinutes,
    /// <summary>Owner decision 2026-08-30 rule 9 addition: admin-entered
    /// OperatingExpense rows for the period — see IOperatingExpenseService.
    /// Never includes teacher payroll (rejected at entry time).</summary>
    IReadOnlyList<CurrencyAmount> ExpensesByCurrency,
    /// <summary>Owner decision 2026-08-30 rule 9 addition: Revenue - Payroll -
    /// Expenses, per currency — only for a currency that appears in at least
    /// one of those three lists (never a currency with nothing to report).
    /// No FX conversion, matching D-53/Money's own rule.</summary>
    IReadOnlyList<CurrencyAmount> NetProfitByCurrency);

/// <summary>
/// The owner's own stated MVP scope names "تقارير مالية أساسية" (basic
/// financial reports) explicitly — the original three numbers here
/// (revenue, payroll cost, payment-blocked/pending counts) were built to
/// that letter, deliberately no more. Owner decision 2026-08-30 rule 9 is a
/// later, explicit, dated instruction that names five additional figures
/// by name (scheduled teaching hours, manually entered operating expenses,
/// net profit, month-over-month comparison, per-currency totals) — this is
/// that extension, not a reversal of the original discipline: every added
/// number is still plain and live-computed, no forecasting, no invented
/// metric beyond what rule 9 itself names. Month-over-month comparison is
/// deliberately NOT a new field here — the caller (the admin page) gets it
/// by calling GenerateAsync twice, once per period, rather than this
/// service inventing a new "comparison" abstraction.
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
