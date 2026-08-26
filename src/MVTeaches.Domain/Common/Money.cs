namespace MVTeaches.Domain.Common;

/// <summary>
/// Money = amount + currency (Technical Study §33.1 / D-09).
/// Always <see cref="decimal"/> — never a floating-point type — and never compared
/// or added across different currencies without an explicit, intentional decision
/// by the caller (there is no automatic FX conversion anywhere in this system: D-53).
/// </summary>
public sealed record Money
{
    public decimal Amount { get; }

    /// <summary>ISO 4217 three-letter code (JOD, ILS, USD, ...).</summary>
    public string Currency { get; }

    public Money(decimal amount, string currency)
    {
        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3)
        {
            throw new ArgumentException("Currency must be a 3-letter ISO 4217 code.", nameof(currency));
        }

        Amount = amount;
        Currency = currency.ToUpperInvariant();
    }

    public static Money Zero(string currency) => new(0m, currency);

    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount + other.Amount, Currency);
    }

    public Money Subtract(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount - other.Amount, Currency);
    }

    public static Money operator +(Money a, Money b) => a.Add(b);
    public static Money operator -(Money a, Money b) => a.Subtract(b);

    private void EnsureSameCurrency(Money other)
    {
        if (!string.Equals(Currency, other.Currency, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Cannot combine money in different currencies ({Currency} vs {other.Currency}). " +
                "The repository has no automatic FX conversion (D-53) — this must never happen silently.");
        }
    }

    public override string ToString() => $"{Amount:0.000} {Currency}";
}
