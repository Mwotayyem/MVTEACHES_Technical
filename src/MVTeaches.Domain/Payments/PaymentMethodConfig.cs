namespace MVTeaches.Domain.Payments;

/// <summary>
/// Owner decision 2026-08-30 (manual payment methods): admin-configurable
/// beneficiary details for each real, manual transfer channel MVTeaches
/// actually accepts — CliQ, a local bank account, an international wire
/// (IBAN/SWIFT), or cash collected in person. Nothing here is a payment
/// gateway; no money moves through this application at all. A method with
/// incomplete required fields, or one that is not <see cref="IsActive"/>,
/// must never be offered to a payer as available.
///
/// Editing is never in place once a method has ever been shown to a payer —
/// <see cref="Deactivate"/> closes this row and a brand-new row replaces it,
/// exactly like <c>PricingPlan.CloseEffectiveness</c> already does for
/// prices — because <see cref="Payments.Payment.PaymentMethodConfigId"/>
/// snapshots which row a historical payment was told to use, and that
/// snapshot must never be able to silently change meaning after the fact.
/// </summary>
public class PaymentMethodConfig
{
    public long Id { get; private set; }

    public PaymentMethod Type { get; private set; }

    /// <summary>The beneficiary name exactly as it appears at the bank/CliQ —
    /// required for every method except <see cref="PaymentMethod.Cash"/>.</summary>
    public string BeneficiaryName { get; private set; } = string.Empty;

    public string? CliqAlias { get; private set; }
    public string? Iban { get; private set; }
    public string? BankName { get; private set; }
    public string? SwiftBic { get; private set; }
    public string? CountryName { get; private set; }
    public string? Instructions { get; private set; }

    /// <summary>Comma-separated ISO 4217 codes this specific method accepts
    /// (e.g. "JOD" for a local CliQ alias, "USD,JOD" for an international
    /// wire) — never invented or assumed; admin-entered per method.</summary>
    public string AcceptedCurrenciesCsv { get; private set; } = string.Empty;

    public bool IsActive { get; private set; } = true;

    public long CreatedByUserId { get; private set; }
    public NodaTime.Instant CreatedAtUtc { get; private set; }
    public long? DeactivatedByUserId { get; private set; }
    public NodaTime.Instant? DeactivatedAtUtc { get; private set; }

    private PaymentMethodConfig() { }

    public PaymentMethodConfig(PaymentMethod type, string beneficiaryName, string? cliqAlias, string? iban,
        string? bankName, string? swiftBic, string? countryName, string? instructions, string acceptedCurrenciesCsv,
        long createdByUserId, NodaTime.Instant createdAtUtc)
    {
        if (type != PaymentMethod.Cash && string.IsNullOrWhiteSpace(beneficiaryName))
        {
            throw new ArgumentException("A beneficiary name is required for every method except cash.", nameof(beneficiaryName));
        }

        if (string.IsNullOrWhiteSpace(acceptedCurrenciesCsv))
        {
            throw new ArgumentException("At least one accepted currency is required.", nameof(acceptedCurrenciesCsv));
        }

        switch (type)
        {
            case PaymentMethod.CliQ when string.IsNullOrWhiteSpace(cliqAlias):
                throw new ArgumentException("A CliQ alias/number is required for a CliQ method.", nameof(cliqAlias));
            case PaymentMethod.BankTransfer or PaymentMethod.InternationalBankTransfer when string.IsNullOrWhiteSpace(iban):
                throw new ArgumentException("An IBAN is required for a bank transfer method.", nameof(iban));
            case PaymentMethod.InternationalBankTransfer when string.IsNullOrWhiteSpace(swiftBic):
                throw new ArgumentException("A SWIFT/BIC code is required for an international transfer method.", nameof(swiftBic));
        }

        Type = type;
        BeneficiaryName = beneficiaryName;
        CliqAlias = cliqAlias;
        Iban = iban;
        BankName = bankName;
        SwiftBic = swiftBic;
        CountryName = countryName;
        Instructions = instructions;
        AcceptedCurrenciesCsv = acceptedCurrenciesCsv;
        CreatedByUserId = createdByUserId;
        CreatedAtUtc = createdAtUtc;
    }

    public IReadOnlyList<string> AcceptedCurrencies =>
        AcceptedCurrenciesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Never a real delete — a method that was ever shown to a
    /// payer must remain readable forever for historical payments that
    /// snapshot it, it just stops being offered as available going forward.</summary>
    public void Deactivate(long deactivatedByUserId, NodaTime.Instant nowUtc)
    {
        IsActive = false;
        DeactivatedByUserId = deactivatedByUserId;
        DeactivatedAtUtc = nowUtc;
    }
}
