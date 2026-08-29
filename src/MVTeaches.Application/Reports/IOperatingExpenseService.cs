using MVTeaches.Domain.Common;
using MVTeaches.Domain.Finance;
using NodaTime;

namespace MVTeaches.Application.Reports;

public enum RecordExpenseOutcome
{
    Recorded,

    /// <summary>Owner decision 2026-08-30 rule 9: "Teacher payroll must
    /// never be entered/counted again as a manual expense" — the reserved
    /// "Payroll" category is refused outright, not merely discouraged.</summary>
    PayrollCategoryNotAllowed,

    InvalidAmount,
}

public record RecordExpenseResult(RecordExpenseOutcome Outcome, long? ExpenseId = null);

/// <summary>
/// Owner decision 2026-08-30 rule 9: the "manually entered operating
/// expenses" half of the financial dashboard extension. Every entry is a
/// plain, admin-recorded fact — no categories or amounts are invented here.
/// </summary>
public interface IOperatingExpenseService
{
    Task<RecordExpenseResult> RecordAsync(int countryId, string category, Money amount, LocalDate incurredOn,
        string? note, long enteredByUserId, CancellationToken cancellationToken);

    Task<IReadOnlyList<OperatingExpense>> ListAsync(LocalDate periodStart, LocalDate periodEnd, CancellationToken cancellationToken);
}
