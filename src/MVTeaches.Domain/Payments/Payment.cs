using MVTeaches.Domain.Common;
using NodaTime;

namespace MVTeaches.Domain.Payments;

public enum PaymentMethod
{
    Card,
    CliQ,
    BankTransfer,
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

    public Instant CreatedAtUtc { get; private set; }

    private Payment() { }

    public Payment(long studentId, long? subscriptionId, long? payerUserId, Money amount, PaymentMethod method,
        string providerKey, string referenceCode, Instant createdAtUtc, long? proofFileId = null, string? providerTransactionId = null)
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
        CreatedAtUtc = createdAtUtc;
    }

    /// <summary>
    /// Idempotency guard: a browser success page or a webhook replay must never
    /// be the sole source of truth (§21.6/D-88's forward-looking rule). The
    /// caller MUST rely on the database's UNIQUE(ProviderKey, ProviderTransactionId)
    /// and UNIQUE(ReferenceCode) constraints in addition to this state check —
    /// this method alone does not protect against a concurrent duplicate.
    /// </summary>
    public void Confirm(long confirmedByUserId, Instant nowUtc)
    {
        if (Status != PaymentStatus.Pending)
        {
            throw new InvalidOperationException($"Cannot confirm a payment already in state {Status}.");
        }

        Status = PaymentStatus.Confirmed;
        ConfirmedByUserId = confirmedByUserId;
        ConfirmedAtUtc = nowUtc;
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
