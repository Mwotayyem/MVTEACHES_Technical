using MVTeaches.Domain.Payments;

namespace MVTeaches.Application.Payments;

public record CreatePaymentMethodResult(long Id);

/// <summary>
/// Owner decision 2026-08-30 (manual payment methods): admin-configurable
/// beneficiary details for the real, manual channels MVTeaches accepts —
/// see PaymentMethodConfig's own remarks for why editing is never in place.
/// </summary>
public interface IPaymentMethodConfigService
{
    Task<CreatePaymentMethodResult> CreateAsync(PaymentMethod type, string beneficiaryName, string? cliqAlias,
        string? iban, string? bankName, string? swiftBic, string? countryName, string? instructions,
        IReadOnlyList<string> acceptedCurrencies, long createdByUserId, CancellationToken cancellationToken);

    /// <summary>Never a delete — a method that was ever shown to a payer
    /// must remain readable forever for the historical payments that
    /// snapshot it (Payment.PaymentMethodConfigId); this only stops it from
    /// being offered going forward.</summary>
    Task DeactivateAsync(long id, long deactivatedByUserId, CancellationToken cancellationToken);

    /// <summary>What a payer is actually offered — the only methods a
    /// purchase/transfer page may ever show.</summary>
    Task<IReadOnlyList<PaymentMethodConfig>> ListActiveAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<PaymentMethodConfig>> ListAllAsync(CancellationToken cancellationToken);
}
