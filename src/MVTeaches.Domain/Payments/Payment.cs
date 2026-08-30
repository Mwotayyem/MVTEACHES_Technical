using MVTeaches.Domain.Common;
using NodaTime;

namespace MVTeaches.Domain.Payments;

public enum PaymentMethod
{
    Card,
    CliQ,
    BankTransfer,

    /// <summary>Owner decision 2026-08-30: cross-border wire transfer (IBAN/SWIFT),
    /// distinct from a local <see cref="BankTransfer"/> — the received amount can
    /// differ from the requested one (fees/FX), which <see cref="Payment.ReceivedAmount"/>
    /// exists to reconcile against, never silently.</summary>
    InternationalBankTransfer,

    PayPal,
    Cash,
    Migration,
}

public enum PaymentStatus
{
    Pending,
    Confirmed,
    Rejected,
    Failed,

    /// <summary>Modeled but intentionally unused in MVP (D-15: no automated
    /// refunds). Any real use requires a reason and SystemAdmin + audit (§22.4).</summary>
    Refunded,
}

/// <summary>
/// Technical Study §22.1. D-39/D-11: MVP's only real provider is manual
/// (bank transfer/CliQ + uploaded proof + admin confirmation) — <see cref="ProviderKey"/>
/// is "manual" for every payment right now. The MEPS boundary (IPaymentProvider)
/// exists for a future provider to plug into WITHOUT touching this entity;
/// nothing here assumes MEPS's webhook shape, since it is not yet known
/// (D-88 — Waiting for MEPS).
///
/// The outstanding balance is always DERIVED:
///   subscription.Price - SUM(payments.Amount WHERE Status = Confirmed)
/// There is no stored "paid" or "outstanding" column anywhere (§22.1).
/// </summary>
public class Payment
{
    public long Id { get; private set; }

    public long StudentId { get; private set; }
    public long? SubscriptionId { get; private set; }

    /// <summary>May be the guardian, not the student (D-01).</summary>
    public long? PayerUserId { get; private set; }

    public Money Amount { get; private set; } = null!;
    public PaymentMethod Method { get; private set; }

    /// <summary>"manual" for every MVP payment (D-39). A future provider plugs
    /// in behind IPaymentProvider without changing this entity's shape.</summary>
    public string ProviderKey { get; private set; } = "manual";
    public string? ProviderTransactionId { get; private set; }

    /// <summary>Human-facing reference, e.g. "MVT-8FQ2K".</summary>
    public string ReferenceCode { get; private set; } = string.Empty;

    public PaymentStatus Status { get; private set; } = PaymentStatus.Pending;

    public long? ProofFileId { get; private set; }
    public long? ConfirmedByUserId { get; private set; }
    public Instant? ConfirmedAtUtc { get; private set; }
    public string? RejectionReason { get; private set; }

    /// <summary>The transfer sender's real name, as typed by the payer or
    /// entered by an admin on their behalf — deliberately independent of
    /// <see cref="PayerUserId"/>, since the person who actually pressed
    /// "transfer" in their banking app is very often not the account holder
    /// (a guardian paying for a direct-login student, a relative wiring
    /// money). Never assumed to match the student's own name.</summary>
    public string? PayerDisplayName { get; private set; }

    /// <summary>The date the payer says the transfer happened — reported,
    /// not verified; verification is what admin confirmation is for.</summary>
    public LocalDate? TransferDate { get; private set; }

    /// <summary>Snapshots which <c>PaymentMethodConfig</c> (beneficiary
    /// name/IBAN/CliQ id/etc.) was actually shown to the payer at request
    /// time — an admin editing the bank details later must never silently
    /// rewrite what a historical payment record says the payer was told.</summary>
    public long? PaymentMethodConfigId { get; private set; }

    /// <summary>Set only at confirmation time, and only when it differs from
    /// <see cref="Amount"/> — an international wire's bank fees or a partial
    /// transfer mean the amount that actually landed can be less than what
    /// was requested. Never auto-converted across currencies (D-53's own
    /// "no automatic FX" rule, extended here): a <see cref="ReceivedCurrency"/>
    /// that differs from what the subscription actually needs contributes
    /// nothing toward activating it — see PaymentService's own remarks.</summary>
    public decimal? ReceivedAmount { get; private set; }
    public string? ReceivedCurrency { get; private set; }

    /// <summary>Set when this payment is a corrected resubmission after a
    /// prior one was rejected — the prior row is NEVER edited or deleted
    /// (§20.5's append-only discipline, applied here too); this is a pointer
    /// to it, preserving the full correction history for audit.</summary>
    public long? SupersedesPaymentId { get; private set; }

    public Instant CreatedAtUtc { get; private set; }

    private Payment() { }

    public Payment(long studentId, long? subscriptionId, long? payerUserId, Money amount, PaymentMethod method,
        string providerKey, string referenceCode, Instant createdAtUtc, long? proofFileId = null, string? providerTransactionId = null,
        string? payerDisplayName = null, LocalDate? transferDate = null, long? paymentMethodConfigId = null, long? supersedesPaymentId = null)
    {
        if (amount.Amount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount));
        }

        if (string.IsNullOrWhiteSpace(referenceCode))
        {
            throw new ArgumentException("A reference code is required.", nameof(referenceCode));
        }

        StudentId = studentId;
        SubscriptionId = subscriptionId;
        PayerUserId = payerUserId;
        Amount = amount;
        Method = method;
        ProviderKey = providerKey;
        ReferenceCode = referenceCode;
        ProofFileId = proofFileId;
        ProviderTransactionId = providerTransactionId;
        PayerDisplayName = payerDisplayName;
        TransferDate = transferDate;
        PaymentMethodConfigId = paymentMethodConfigId;
        SupersedesPaymentId = supersedesPaymentId;
        CreatedAtUtc = createdAtUtc;
    }

    /// <summary>Fills in what the payer (or an admin acting on their behalf)
    /// reports after actually sending the transfer — this is the "submitted,
    /// awaiting review" transition (a Payment created without ever calling
    /// this stays "awaiting transfer" from the UI's point of view, purely by
    /// having none of these fields set yet — no new PaymentStatus value was
    /// needed for that distinction). Only possible while still Pending.</summary>
    public void AttachTransferDetails(string? payerDisplayName, LocalDate? transferDate, string? bankReferenceNumber, long? receiptDocumentId)
    {
        if (Status != PaymentStatus.Pending)
        {
            throw new InvalidOperationException($"Cannot attach transfer details to a payment already in state {Status}.");
        }

        PayerDisplayName = payerDisplayName;
        TransferDate = transferDate;
        if (!string.IsNullOrWhiteSpace(bankReferenceNumber))
        {
            ProviderTransactionId = bankReferenceNumber;
        }
        if (receiptDocumentId is not null)
        {
            ProofFileId = receiptDocumentId;
        }
    }

    /// <summary>True once the payer has actually reported sending the
    /// transfer (a name, a date, a reference, or a receipt) — the UI's
    /// "submitted, awaiting review" state, distinct from "still waiting for
    /// the payer to transfer at all". Deliberately computed, never stored.</summary>
    public bool HasSubmittedTransferDetails =>
        !string.IsNullOrWhiteSpace(PayerDisplayName) || TransferDate is not null
        || !string.IsNullOrWhiteSpace(ProviderTransactionId) || ProofFileId is not null;

    /// <summary>
    /// Idempotency guard: a browser success page or a webhook replay must never
    /// be the sole source of truth (§21.6/D-88's forward-looking rule). The
    /// caller MUST rely on the database's UNIQUE(ProviderKey, ProviderTransactionId)
    /// and UNIQUE(ReferenceCode) constraints in addition to this state check —
    /// this method alone does not protect against a concurrent duplicate.
    ///
    /// <paramref name="receivedAmount"/>/<paramref name="receivedCurrency"/> are
    /// only ever supplied when they differ from what was requested (an
    /// international transfer's fees/shortfall) — omitted, they default to
    /// exactly what was requested, which is what every domestic method
    /// (CliQ/local bank/cash/card) always means: confirming is verifying the
    /// full requested amount actually arrived, never a policy decision about
    /// tolerating a difference.
    /// </summary>
    public void Confirm(long confirmedByUserId, Instant nowUtc, decimal? receivedAmount = null, string? receivedCurrency = null)
    {
        if (Status != PaymentStatus.Pending)
        {
            throw new InvalidOperationException($"Cannot confirm a payment already in state {Status}.");
        }

        Status = PaymentStatus.Confirmed;
        ConfirmedByUserId = confirmedByUserId;
        ConfirmedAtUtc = nowUtc;
        ReceivedAmount = receivedAmount ?? Amount.Amount;
        ReceivedCurrency = receivedCurrency ?? Amount.Currency;
    }

    public void Reject(string reason)
    {
        if (Status != PaymentStatus.Pending)
        {
            throw new InvalidOperationException($"Cannot reject a payment already in state {Status}.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A rejection requires a reason.", nameof(reason));
        }

        Status = PaymentStatus.Rejected;
        RejectionReason = reason;
    }
}

/// <summary>Technical Study §22.4 (D-15): documentation only — there is no
/// automated refund flow. Every request is recorded and, in MVP, always
/// resolved as "Rejected — Policy".</summary>
public class RefundRequest
{
    public long Id { get; private set; }
    public long PaymentId { get; private set; }
    public long RequestedByUserId { get; private set; }
    public Instant RequestedAtUtc { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public string Status { get; private set; } = "Rejected-Policy";
    public long? ResolvedByUserId { get; private set; }
    public Instant? ResolvedAtUtc { get; private set; }

    private RefundRequest() { }

    public RefundRequest(long paymentId, long requestedByUserId, string reason, Instant requestedAtUtc)
    {
        PaymentId = paymentId;
        RequestedByUserId = requestedByUserId;
        Reason = reason;
        RequestedAtUtc = requestedAtUtc;
    }

    public void Resolve(long resolvedByUserId, Instant nowUtc)
    {
        ResolvedByUserId = resolvedByUserId;
        ResolvedAtUtc = nowUtc;
    }
}
