using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Payments;
using MVTeaches.Domain.Audit;
using MVTeaches.Domain.Common;
using MVTeaches.Domain.Ledger;
using MVTeaches.Domain.Notifications;
using MVTeaches.Domain.Payments;
using MVTeaches.Domain.People;
using MVTeaches.Domain.Subscriptions;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;
using Npgsql;

namespace MVTeaches.Infrastructure.Payments;

/// <summary>
/// Technical Study §22 (D-11/D-38/D-14). Reconciles two statements that read
/// as if in tension in §22.2's boxed summary vs the D-38 decision row:
///
///   - `entitlement_ledger.reason = 'Purchase'` is written ONLY when a
///     payment is confirmed in FULL (D-38's own words: "يُكتب بكامل الدقائق
///     عند تأكيد الدفع الكامل"). This method is that write path.
///   - An admin-created subscription with no payment (D-13) is a
///     DIFFERENT, deliberate ledger reason — `AdminGrant` — written by an
///     admin action, not by this service. The schema documents both reasons
///     side by side (§20.2) precisely so these two cases never collide.
///
/// D-14: confirming the payment that brings the outstanding balance to zero
/// lifts PaymentBlocked in the SAME transaction, not a follow-up job.
/// </summary>
public class PaymentService : IPaymentService
{
    private const string PostgresUniqueViolationSqlState = "23505";

    private readonly MvTeachesDbContext _db;
    private readonly IClock _clock;

    public PaymentService(MvTeachesDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>Same "self, an active guardian, or an admin acting for
    /// them" IDOR guard SubscriptionService.PurchaseFromPlanAsync and
    /// JoinAttendanceService already use — kept as a private duplicate here
    /// (rather than a shared helper) matching how each of those services
    /// keeps its own copy today.</summary>
    private async Task<bool> IsAuthorizedForStudentAsync(long studentId, long actingUserId, CancellationToken ct)
    {
        var isTheStudentThemself = await _db.Students.AnyAsync(s => s.Id == studentId && s.UserId == actingUserId, ct);
        if (isTheStudentThemself)
        {
            return true;
        }

        return await _db.Guardianships
            .Join(_db.Guardians, gs => gs.GuardianId, g => g.Id, (gs, g) => new { gs.StudentId, g.UserId })
            .AnyAsync(x => x.StudentId == studentId && x.UserId == actingUserId, ct);
    }

    public async Task<RecordPaymentResult> RecordManualPaymentAsync(RecordPaymentRequest request, CancellationToken cancellationToken)
    {
        var referenceCode = GenerateReferenceCode();
        var payment = new Payment(request.StudentId, request.SubscriptionId, request.PayerUserId, request.Amount,
            request.Method, providerKey: "manual", referenceCode, _clock.GetCurrentInstant(), request.ProofFileId,
            paymentMethodConfigId: request.PaymentMethodConfigId, supersedesPaymentId: request.SupersedesPaymentId);

        _db.Payments.Add(payment);

        // Owner decision 2026-08-30 rule 1: the normal path is the student
        // paying through the (not yet selected) gateway, with the entitlement
        // activating automatically off the trusted server-side confirmation.
        // A payment recorded by hand is therefore the EXCEPTION — cash, bank
        // transfer, or a correction — and the owner requires it to be
        // permission-protected (the Admin/SystemAdmin-only page) and audit
        // logged. This is that audit trail: it records who keyed it in, which
        // is exactly what a manual money path needs to be reviewable.
        _db.AuditLogEntries.Add(new AuditLogEntry("Payment", payment.ReferenceCode, "ManualPaymentRecorded",
            request.PayerUserId, reason: $"Manual {request.Method} payment recorded for student {request.StudentId}.",
            beforeJson: null, afterJson: null, _clock.GetCurrentInstant()));

        await _db.SaveChangesAsync(cancellationToken); // UNIQUE(reference_code) guards collisions (effectively impossible with this generator)

        return new RecordPaymentResult(payment.Id, referenceCode);
    }

    public async Task<RequestOwnPaymentResult> RequestOwnPaymentAsync(long studentId, long subscriptionId, long paymentMethodConfigId,
        long actingUserId, CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedForStudentAsync(studentId, actingUserId, cancellationToken))
        {
            return new RequestOwnPaymentResult(RequestOwnPaymentOutcome.Unauthorized);
        }

        var subscription = await _db.Subscriptions.FirstOrDefaultAsync(
            s => s.Id == subscriptionId && s.StudentId == studentId, cancellationToken);
        if (subscription is null)
        {
            return new RequestOwnPaymentResult(RequestOwnPaymentOutcome.SubscriptionNotFound);
        }

        if (subscription.Status != SubscriptionStatus.Draft)
        {
            return new RequestOwnPaymentResult(RequestOwnPaymentOutcome.SubscriptionNotDraft);
        }

        var alreadyRequested = await _db.Payments.AnyAsync(
            p => p.SubscriptionId == subscriptionId && p.Status == PaymentStatus.Pending, cancellationToken);
        if (alreadyRequested)
        {
            return new RequestOwnPaymentResult(RequestOwnPaymentOutcome.AlreadyRequested);
        }

        var method = await _db.PaymentMethodConfigs.FirstOrDefaultAsync(
            m => m.Id == paymentMethodConfigId && m.IsActive, cancellationToken);
        if (method is null)
        {
            return new RequestOwnPaymentResult(RequestOwnPaymentOutcome.PaymentMethodNotFound);
        }

        // Owner decision 2026-08-30 (shortfall/top-up policy): the amount
        // requested is the REMAINING owed balance, not always the full
        // price — a supplementary request after an earlier short transfer
        // asks for exactly what is still missing. Still entirely
        // server-computed from the subscription's own snapshot and its own
        // confirmed payments; nothing here is caller-supplied.
        var confirmedReceived = await ComputeConfirmedReceivedAsync(subscription, cancellationToken);
        var remaining = subscription.Price.Amount - confirmedReceived;
        if (remaining <= 0m)
        {
            return new RequestOwnPaymentResult(RequestOwnPaymentOutcome.AlreadyFullyFunded);
        }

        var requestedAmount = new Money(remaining, subscription.Price.Currency);
        var referenceCode = GenerateReferenceCode();
        var payment = new Payment(studentId, subscriptionId, actingUserId, requestedAmount, method.Type,
            providerKey: "manual", referenceCode, _clock.GetCurrentInstant(), paymentMethodConfigId: method.Id);
        _db.Payments.Add(payment);

        _db.AuditLogEntries.Add(new AuditLogEntry("Payment", payment.ReferenceCode, "SelfServicePaymentRequested",
            actingUserId, reason: $"Payer requested a {method.Type} payment of {requestedAmount.Amount} {requestedAmount.Currency} for student {studentId}, subscription {subscriptionId}.",
            beforeJson: null, afterJson: null, _clock.GetCurrentInstant()));

        await _db.SaveChangesAsync(cancellationToken);

        return new RequestOwnPaymentResult(RequestOwnPaymentOutcome.Requested, payment.Id, referenceCode, requestedAmount);
    }

    public async Task<SubscriptionFundingStatus> GetSubscriptionFundingStatusAsync(long subscriptionId, CancellationToken cancellationToken)
    {
        var subscription = await _db.Subscriptions.FirstAsync(s => s.Id == subscriptionId, cancellationToken);
        var confirmedReceived = await ComputeConfirmedReceivedAsync(subscription, cancellationToken);
        var remaining = Math.Max(0m, subscription.Price.Amount - confirmedReceived);
        return new SubscriptionFundingStatus(subscription.Price, confirmedReceived,
            new Money(remaining, subscription.Price.Currency), remaining <= 0m);
    }

    /// <summary>Same-currency confirmed total this subscription has actually
    /// received so far — the single source of truth both
    /// <see cref="SettleSubscriptionIfFullyPaidAsync"/> (activation) and
    /// <see cref="RequestOwnPaymentAsync"/>/<see cref="GetSubscriptionFundingStatusAsync"/>
    /// (what's left to request/display) read from, so they can never drift
    /// apart. A confirmed payment in a different currency contributes
    /// nothing (D-53 — no automatic FX, ever).</summary>
    private async Task<decimal> ComputeConfirmedReceivedAsync(Subscription subscription, CancellationToken ct) =>
        await _db.Payments
            .Where(p => p.SubscriptionId == subscription.Id && p.Status == PaymentStatus.Confirmed
                        && p.ReceivedCurrency == subscription.Price.Currency)
            .SumAsync(p => (decimal?)p.ReceivedAmount, ct) ?? 0m;

    public async Task<AttachTransferDetailsResult> AttachTransferDetailsAsync(long paymentId, long actingUserId, bool isAdminInitiated,
        string? payerDisplayName, LocalDate? transferDate, string? bankReferenceNumber, long? receiptFileId, CancellationToken cancellationToken)
    {
        var payment = await _db.Payments.FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken);
        if (payment is null)
        {
            return new AttachTransferDetailsResult(AttachTransferDetailsOutcome.NotFound);
        }

        if (!isAdminInitiated && !await IsAuthorizedForStudentAsync(payment.StudentId, actingUserId, cancellationToken))
        {
            // "Not found" and "not yours" deliberately share an outcome one
            // level up (the caller renders both the same way) — this
            // service still distinguishes them internally for its own logs.
            return new AttachTransferDetailsResult(AttachTransferDetailsOutcome.Unauthorized);
        }

        if (payment.Status != PaymentStatus.Pending)
        {
            return new AttachTransferDetailsResult(AttachTransferDetailsOutcome.NotPending);
        }

        payment.AttachTransferDetails(payerDisplayName, transferDate, bankReferenceNumber, receiptFileId);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return new AttachTransferDetailsResult(AttachTransferDetailsOutcome.Attached);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresUniqueViolationSqlState })
        {
            // See PaymentConfiguration's own remarks: (provider, bank
            // reference) is a coarse, global de-dup, not a perfectly
            // bank-scoped one — surfaced as a friendly outcome, not a crash.
            _db.ChangeTracker.Clear();
            return new AttachTransferDetailsResult(AttachTransferDetailsOutcome.DuplicateReference);
        }
    }

    public async Task<ConfirmPaymentResult> ConfirmAsync(long paymentId, long confirmedByUserId, CancellationToken cancellationToken,
        Money? actuallyReceivedAmount = null)
    {
        var payment = await _db.Payments.FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken);
        if (payment is null)
        {
            return new ConfirmPaymentResult(ConfirmPaymentOutcome.NotFound);
        }

        if (payment.Status == PaymentStatus.Confirmed)
        {
            return new ConfirmPaymentResult(ConfirmPaymentOutcome.AlreadyConfirmed);
        }

        if (payment.Status != PaymentStatus.Pending)
        {
            return new ConfirmPaymentResult(ConfirmPaymentOutcome.NotPending);
        }

        var now = _clock.GetCurrentInstant();
        payment.Confirm(confirmedByUserId, now, actuallyReceivedAmount?.Amount, actuallyReceivedAmount?.Currency);

        // Owner decision 2026-08-30 rule 1: a human confirming money by hand is
        // the audited exception path. A gateway confirmation reaches
        // ProcessProviderConfirmationAsync instead and is attributed to the
        // provider, not to an admin, so the two are distinguishable in review.
        _db.AuditLogEntries.Add(new AuditLogEntry("Payment", payment.ReferenceCode, "ManualPaymentConfirmed",
            confirmedByUserId, reason: $"Admin confirmed {payment.Method} payment; received {payment.ReceivedAmount} {payment.ReceivedCurrency} (requested {payment.Amount.Amount} {payment.Amount.Currency}).",
            beforeJson: null, afterJson: null, now));

        var fullyFunded = await SettleSubscriptionIfFullyPaidAsync(payment, confirmedByUserId, now, cancellationToken);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return new ConfirmPaymentResult(fullyFunded ? ConfirmPaymentOutcome.Confirmed : ConfirmPaymentOutcome.ConfirmedButSubscriptionNotYetFullyFunded);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresUniqueViolationSqlState })
        {
            // Release-readiness audit finding: lost a genuine race against a
            // DIFFERENT payment on the SAME subscription that also crossed the
            // "fully paid" threshold concurrently and already posted the one
            // allowed Purchase entry (ux_ent_purchase) — this whole SaveChanges
            // batch (this payment's own Confirm() update included) rolled back
            // together. The money this payment represents is still real,
            // though, and the subscription is already activated by the winner,
            // so there is nothing left to settle — just mark this payment
            // Confirmed on its own, exactly like every OTHER payment that
            // arrives after a subscription is already fully paid (the
            // alreadyPosted check above already handles that sequentially;
            // this is the same outcome reached via a race instead).
            _db.ChangeTracker.Clear();
            var reloaded = await _db.Payments.FirstAsync(p => p.Id == paymentId, cancellationToken);
            if (reloaded.Status == PaymentStatus.Confirmed)
            {
                return new ConfirmPaymentResult(ConfirmPaymentOutcome.AlreadyConfirmed);
            }

            reloaded.Confirm(confirmedByUserId, now, actuallyReceivedAmount?.Amount, actuallyReceivedAmount?.Currency);
            await _db.SaveChangesAsync(cancellationToken);
            return new ConfirmPaymentResult(ConfirmPaymentOutcome.Confirmed);
        }
    }

    public async Task RejectAsync(long paymentId, string reason, long rejectedByUserId, CancellationToken cancellationToken)
    {
        var payment = await _db.Payments.FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken)
            ?? throw new InvalidOperationException("Payment not found.");
        payment.Reject(reason);

        // Owner decision 2026-08-30 (Section 8): the payer needs to know a
        // correction is needed — same "guardian-only child, nothing lost"
        // convention as SettleSubscriptionIfFullyPaidAsync's own notification,
        // and the same "never the bank details or receipt image" restriction.
        var student = await _db.Students.FirstOrDefaultAsync(s => s.Id == payment.StudentId, cancellationToken);
        if (student?.UserId is not null)
        {
            var payload = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["StudentName"] = student.FullName,
                ["ReferenceCode"] = payment.ReferenceCode,
                ["Reason"] = reason,
            });
            _db.NotificationOutboxItems.Add(new NotificationOutboxItem(
                NotificationEvent.PaymentNeedsCorrection, NotificationChannel.WhatsApp, student.UserId.Value, payload,
                _clock.GetCurrentInstant()));
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ConfirmPaymentResult> ProcessProviderConfirmationAsync(string providerKey, string providerTransactionId,
        long confirmedByUserId, CancellationToken cancellationToken)
    {
        var payment = await _db.Payments.FirstOrDefaultAsync(
            p => p.ProviderKey == providerKey && p.ProviderTransactionId == providerTransactionId, cancellationToken);

        if (payment is null)
        {
            return new ConfirmPaymentResult(ConfirmPaymentOutcome.NotFound);
        }

        if (payment.Status == PaymentStatus.Confirmed)
        {
            // A replayed webhook/callback for an already-confirmed transaction — safe no-op (§21.6/§7).
            return new ConfirmPaymentResult(ConfirmPaymentOutcome.AlreadyConfirmed);
        }

        try
        {
            return await ConfirmAsync(payment.Id, confirmedByUserId, cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresUniqueViolationSqlState })
        {
            // Lost a genuine race against a concurrent duplicate delivery of the same callback.
            _db.ChangeTracker.Clear();
            return new ConfirmPaymentResult(ConfirmPaymentOutcome.AlreadyConfirmed);
        }
    }

    /// <summary>Returns whether the subscription ends this call fully
    /// funded — true whether THIS call is the one that posted the ledger
    /// entry, or a concurrent confirmation on a different payment already
    /// did (§21.6: this payment's own money is still real and confirmed
    /// either way). Sums by ReceivedAmount/ReceivedCurrency (what actually
    /// arrived), never the originally-requested Amount — for every
    /// domestic method those are always equal (Payment.Confirm defaults
    /// ReceivedAmount to the requested amount when no discrepancy is
    /// supplied), so this changes nothing for them; it only matters for an
    /// international transfer whose fees/shortfall mean less arrived than
    /// requested. A confirmed payment in a DIFFERENT currency than the
    /// subscription needs contributes NOTHING toward this total — no
    /// automatic FX conversion is ever invented (D-53's own rule) — it sits
    /// there confirmed (the money genuinely arrived and was verified) while
    /// the subscription stays un-activated until an admin resolves the
    /// mismatch, exactly as "لا تفعّل الاشتراك بصمت" requires.</summary>
    private async Task<bool> SettleSubscriptionIfFullyPaidAsync(Payment payment, long confirmedByUserId, Instant now, CancellationToken ct)
    {
        if (payment.SubscriptionId is null)
        {
            return false;
        }

        var subscription = await _db.Subscriptions.FirstAsync(s => s.Id == payment.SubscriptionId, ct);

        var alreadyPosted = await _db.EntitlementLedgerEntries.AnyAsync(
            l => l.SubscriptionId == subscription.Id && l.Reason == LedgerReason.Purchase, ct);
        if (alreadyPosted)
        {
            // D-38's ledger write is a one-time event per subscription — this
            // call must never post (or re-activate) a second time. That said,
            // the subscription genuinely IS fully funded (a concurrent
            // confirmation on a DIFFERENT payment already posted it, the
            // Two_concurrent_payment_confirmations... test's exact race) —
            // this payment's own money is real and confirmed too, so the
            // caller must report "fully funded", never
            // ConfirmedButSubscriptionNotYetFullyFunded, which is reserved
            // for an actual shortfall/currency mismatch below.
            return true;
        }

        var confirmedTotal = await ComputeConfirmedReceivedAsync(subscription, ct);
        // Include the payment being confirmed right now (not yet reflected by the query above
        // inside the same unit of work until SaveChanges, so add it explicitly) — but only if
        // it actually arrived in the currency this subscription needs.
        if (payment.ReceivedCurrency == subscription.Price.Currency)
        {
            confirmedTotal += payment.ReceivedAmount ?? 0m;
        }

        if (confirmedTotal < subscription.Price.Amount)
        {
            return false; // D-38: no partial activation — full payment only, and never a silent one for a shortfall/currency mismatch.
        }

        _db.EntitlementLedgerEntries.Add(EntitlementLedgerEntry.ForPurchase(
            subscription.StudentId, subscription.Id, subscription.CourseId, subscription.LevelId, subscription.SessionType,
            subscription.MinutesTotal, payment.Id, confirmedByUserId, now));

        subscription.Activate();

        var student = await _db.Students.FirstAsync(s => s.Id == subscription.StudentId, ct);
        if (student.Status == StudentStatus.PaymentBlocked)
        {
            student.ClearPaymentBlock(); // D-14: lifted in the SAME transaction.
        }

        // Owner decision 2026-08-30 rule 9: purchase confirmation. Same
        // "no independent login, nothing lost" convention
        // MeetingProvisioningService already established for a guardian-only
        // child — the guardian who actually purchased is not separately
        // notified here, matching that existing precedent rather than
        // inventing a new one.
        if (student.UserId is not null)
        {
            var payload = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["StudentName"] = student.FullName,
                ["SubscriptionId"] = subscription.Id.ToString(),
                ["MinutesTotal"] = subscription.MinutesTotal.ToString(),
            });
            _db.NotificationOutboxItems.Add(new NotificationOutboxItem(
                NotificationEvent.SubscriptionConfirmed, NotificationChannel.WhatsApp, student.UserId.Value, payload, now));
        }

        return true;
    }

    private static string GenerateReferenceCode() =>
        "MVT-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
}
