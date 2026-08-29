using MVTeaches.Domain.Common;
using NodaTime;

namespace MVTeaches.Domain.Finance;

/// <summary>
/// Owner decision 2026-08-30 rule 9: "manually entered operating expenses"
/// on the admin financial dashboard, alongside confirmed revenue and
/// auto-calculated payroll — a plain, admin-entered fact, no different in
/// spirit from a manual payment record. "Teacher payroll must never be
/// entered/counted again as a manual expense" — enforced here by rejecting
/// the reserved "Payroll" category outright (IOperatingExpenseService is the
/// only writer), so payroll cost can never be double-counted through this
/// path alongside PayrollLine's own auto-calculated figure.
/// </summary>
public class OperatingExpense
{
    public long Id { get; private set; }

    public int CountryId { get; private set; }

    /// <summary>Free-text admin-chosen category (e.g. "Rent", "Marketing",
    /// "Software") — never "Payroll" (see the constructor guard).</summary>
    public string Category { get; private set; } = string.Empty;

    public Money Amount { get; private set; } = null!;
    public LocalDate IncurredOn { get; private set; }
    public string? Note { get; private set; }
    public long EnteredByUserId { get; private set; }
    public Instant CreatedAtUtc { get; private set; }

    private OperatingExpense() { }

    public OperatingExpense(int countryId, string category, Money amount, LocalDate incurredOn,
        string? note, long enteredByUserId, Instant createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            throw new ArgumentException("A category is required.", nameof(category));
        }

        if (category.Trim().Equals("Payroll", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Teacher payroll must never be entered as a manual operating expense — it is already counted automatically.",
                nameof(category));
        }

        if (amount.Amount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "An expense amount must be positive.");
        }

        CountryId = countryId;
        Category = category.Trim();
        Amount = amount;
        IncurredOn = incurredOn;
        Note = note;
        EnteredByUserId = enteredByUserId;
        CreatedAtUtc = createdAtUtc;
    }
}
