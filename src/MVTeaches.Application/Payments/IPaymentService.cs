using MVTeaches.Domain.Common;
using MVTeaches.Domain.Payments;

namespace MVTeaches.Application.Payments;

public enum RecordPaymentOutcome { Recorded }

public record RecordPaymentRequest(long StudentId, long? SubscriptionId, long? PayerUserId, Money Amount,
    PaymentMethod Method, long? ProofFileId);

public record RecordPaymentResult(long PaymentId, string ReferenceCode);

public enum ConfirmPaymentOutcome
{
    Confirmed,
    AlreadyConfirmed,
    NotFound,
    NotPending,
}

public record ConfirmPaymentResult(ConfirmPaymentOutcome Outcome);

/// <summary>
/// D-11/D-39: MVP's only real channel is manual (bank transfer/CliQ + uploaded
/// proof + admin confirmation) — see ManualVerifiedProvider in Infrastructure.
/// §21.6/§7 of the master engineering prompt: a browser success page or a
/// provider webhook must never be the sole source of financial truth by
/// itself; every provider confirmation path (including a future MEPS
/// integration once D-88 resolves) must go through the SAME idempotent,
/// database-constraint-backed confirmation path as this manual one — see
/// ProcessProviderConfirmationAsync.
/// </summary>
public interface IPaymentService
{
    Task<RecordPaymentResult> RecordManualPaymentAsync(RecordPaymentRequest request, CancellationToken cancellationToken);

    /// <summary>Admin confirms a manually-recorded payment. §22.3: if this
    /// settles the student's outstanding balance to zero, the payment block
    /// (D-14) is lifted in the SAME transaction.</summary>
    Task<ConfirmPaymentResult> ConfirmAsync(long paymentId, long confirmedByUserId, CancellationToken cancellationToken);

    Task RejectAsync(long paymentId, string reason, long rejectedByUserId, CancellationToken cancellationToken);

    /// <summary>
    /// The generic, provider-agnostic confirmation path any FUTURE gateway
    /// (MEPS or otherwise) plugs into. Idempotent by construction: replaying
    /// the same (providerKey, providerTransactionId) pair a second time — a
    /// duplicate webhook delivery, a retried callback — must be a safe no-op,
    /// never a double-confirmed payment. This method does NOT assume any
    /// MEPS-specific payload shape; it only demonstrates the boundary
    /// (D-88 — do not fabricate what has not been documented).
    /// </summary>
    Task<ConfirmPaymentResult> ProcessProviderConfirmationAsync(string providerKey, string providerTransactionId,
        long confirmedByUserId, CancellationToken cancellationToken);
}
