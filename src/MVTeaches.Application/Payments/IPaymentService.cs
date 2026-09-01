using MVTeaches.Domain.Common;
using MVTeaches.Domain.Payments;
using NodaTime;

namespace MVTeaches.Application.Payments;

public enum RecordPaymentOutcome { Recorded }

public record RecordPaymentRequest(long StudentId, long? SubscriptionId, long? PayerUserId, Money Amount,
    PaymentMethod Method, long? ProofFileId, long? PaymentMethodConfigId = null, long? SupersedesPaymentId = null);

public record RecordPaymentResult(long PaymentId, string ReferenceCode);

public enum ConfirmPaymentOutcome
{
    Confirmed,
    AlreadyConfirmed,
    NotFound,
    NotPending,

    /// <summary>The subscription needs more than this payment alone —
    /// the payment itself IS confirmed (the money genuinely arrived and was
    /// verified), but the total confirmed toward this subscription is still
    /// short, or is in a different currency than the subscription needs
    /// (never auto-converted). No activation happened; nothing was silently
    /// assumed. See PaymentService.SettleSubscriptionIfFullyPaidAsync.</summary>
    ConfirmedButSubscriptionNotYetFullyFunded,

    /// <summary>Owner report 2026-09-01: an admin typed 20 into "actually
    /// received" for a package that only had 10 left owing, and it was taken.
    /// Confirming more than is owed is now refused outright rather than
    /// absorbed. Nothing is written when this is returned - the payment stays
    /// Pending and can be confirmed again with the right figure.
    ///
    /// Deliberately NOT the same thing as tolerating an overpayment: this
    /// product has no overpayment or credit-balance concept, and inventing one
    /// silently (by activating a package and swallowing the excess) leaves the
    /// centre owing money nothing in the system records. Until there is an
    /// explicit decision about how excess money is held or returned, the
    /// honest answer is to refuse the figure and let a person deal with it.
    ///
    /// <see cref="ConfirmPaymentResult.MaximumAcceptable"/> carries what the
    /// most that could legitimately be confirmed here is, so the screen can
    /// name the number instead of making the admin work it out.</summary>
    ReceivedAmountExceedsWhatIsOwed,
}

public record ConfirmPaymentResult(ConfirmPaymentOutcome Outcome, Money? MaximumAcceptable = null);

public enum AttachTransferDetailsOutcome
{
    Attached,
    NotFound,
    NotPending,
    Unauthorized,

    /// <summary>The (provider, bank reference) pair collided with another
    /// payment's — see PaymentConfiguration's own remarks on why this is a
    /// deliberately coarse, not perfectly bank-scoped, safety net.</summary>
    DuplicateReference,
}

public record AttachTransferDetailsResult(AttachTransferDetailsOutcome Outcome);

public enum RequestOwnPaymentOutcome
{
    Requested,
    Unauthorized,
    SubscriptionNotFound,
    SubscriptionNotDraft,

    /// <summary>This Draft subscription already has a Pending payment request
    /// — a second click (or a resubmission attempt) must never spawn a
    /// duplicate request for the same money; the payer should attach their
    /// transfer details to the existing one instead. This is NOT raised for a
    /// legitimate supplementary request after a prior payment on the same
    /// subscription was already Confirmed-but-short — see
    /// <see cref="RequestOwnPaymentAsync"/>'s own remarks.</summary>
    AlreadyRequested,

    PaymentMethodNotFound,

    /// <summary>Owner decision 2026-08-30 (shortfall/top-up): the subscription's
    /// full price is already covered by confirmed, same-currency payments —
    /// there is nothing left to request. A race (the admin activated it a
    /// different way while this request was in flight) or a stale page are
    /// the only ways this is reached in practice.</summary>
    AlreadyFullyFunded,
}

public record RequestOwnPaymentResult(RequestOwnPaymentOutcome Outcome, long? PaymentId = null, string? ReferenceCode = null,
    Money? RequestedAmount = null);

/// <summary>Owner decision 2026-08-30 (shortfall/top-up policy): the
/// subscription's price is fixed at purchase time (D-38's snapshot); what
/// varies is how much of it has actually been confirmed as received, in the
/// SAME currency the subscription needs — never a different currency,
/// never an invented FX conversion (D-53). <see cref="RemainingOwed"/> is
/// exactly <see cref="Price"/> minus <see cref="ConfirmedReceived"/>, floored
/// at zero; a positive value here is what a supplementary payment/transfer
/// must cover before the package activates.</summary>
public record SubscriptionFundingStatus(Money Price, decimal ConfirmedReceived, Money RemainingOwed, bool IsFullyFunded);

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

    /// <summary>Owner decision 2026-08-30 (self-service purchase &amp; manual
    /// payment methods): the SELF-SERVICE counterpart to
    /// <see cref="RecordManualPaymentAsync"/> — that method stays
    /// Admin/SystemAdmin-only by design (its own remarks explain why: an
    /// arbitrary caller-supplied amount/method must never be trusted). This
    /// method is safe for a student or guardian to call directly because
    /// nothing about the resulting Payment is caller-supplied except WHICH
    /// active <see cref="PaymentMethodConfig"/> they intend to use: the
    /// amount/currency are always read from the Draft subscription's own
    /// price snapshot, never from the request, and the same self-or-active-
    /// guardian IDOR guard <see cref="AttachTransferDetailsAsync"/> already
    /// uses applies here first.</summary>
    /// <summary>Owner decision 2026-08-30 (shortfall/top-up policy): the
    /// requested amount is never simply the subscription's full price —
    /// it is the REMAINING owed amount (price minus already-confirmed,
    /// same-currency receipts), so a payer whose first transfer arrived
    /// short (bank fees on an international wire, for example) can request
    /// and send a genuine supplementary payment for exactly what is still
    /// owed. The first-ever request for a subscription naturally asks for
    /// the full price, since nothing has been confirmed yet.</summary>
    Task<RequestOwnPaymentResult> RequestOwnPaymentAsync(long studentId, long subscriptionId, long paymentMethodConfigId,
        long actingUserId, CancellationToken cancellationToken);

    /// <summary>Read-only funding status for a subscription — what a payer
    /// or admin sees displayed as "price / confirmed / remaining". Never
    /// mutates anything; safe to call from any page that needs to show
    /// where a Draft subscription's funding currently stands.</summary>
    Task<SubscriptionFundingStatus> GetSubscriptionFundingStatusAsync(long subscriptionId, CancellationToken cancellationToken);

    /// <summary>Owner decision 2026-08-30 (manual payment methods): the
    /// payer (or an admin acting on their behalf) reports the transfer they
    /// actually sent — this is the "submitted, awaiting review" transition.
    /// Unless <paramref name="isAdminInitiated"/> is true, <paramref name="actingUserId"/>
    /// must be the payment's own student or one of their active guardians —
    /// re-checked here, never trusted from the caller (the same IDOR guard
    /// SubscriptionService.PurchaseFromPlanAsync already uses for the exact
    /// same "self/guardian, or an admin acting for them" shape).</summary>
    Task<AttachTransferDetailsResult> AttachTransferDetailsAsync(long paymentId, long actingUserId, bool isAdminInitiated,
        string? payerDisplayName, LocalDate? transferDate, string? bankReferenceNumber, long? receiptFileId, CancellationToken cancellationToken);

    /// <summary>Admin confirms a manually-recorded payment. §22.3: if this
    /// settles the student's outstanding balance to zero, the payment block
    /// (D-14) is lifted in the SAME transaction. <paramref name="actuallyReceivedAmount"/>
    /// is supplied ONLY when it differs from what was requested (an
    /// international transfer's fee/shortfall, or a different currency
    /// arriving) — omitted, confirming means exactly what it always meant:
    /// the full requested amount is verified as received, never a policy
    /// decision about tolerating a difference.
    ///
    /// A shortfall is the case this field was built for and still works
    /// exactly as before. An EXCESS is refused
    /// (<see cref="ConfirmPaymentOutcome.ReceivedAmountExceedsWhatIsOwed"/>):
    /// see that member for why absorbing one silently is worse than
    /// refusing it.</summary>
    Task<ConfirmPaymentResult> ConfirmAsync(long paymentId, long confirmedByUserId, CancellationToken cancellationToken,
        Money? actuallyReceivedAmount = null);

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
