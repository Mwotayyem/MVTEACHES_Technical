using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Payments;
using MVTeaches.Domain.Audit;
using MVTeaches.Domain.Ledger;
using MVTeaches.Domain.Notifications;
using MVTeaches.Domain.Payments;
using MVTeaches.Domain.People;
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

    public async Task<RecordPaymentResult> RecordManualPaymentAsync(RecordPaymentRequest request, CancellationToken cancellationToken)
    {
        var referenceCode = GenerateReferenceCode();
        var payment = new Payment(request.StudentId, request.SubscriptionId, request.PayerUserId, request.Amount,
            request.Method, providerKey: "manual", referenceCode, _clock.GetCurrentInstant(), request.ProofFileId);

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

    public async Task<ConfirmPaymentResult> ConfirmAsync(long paymentId, long confirmedByUserId, CancellationToken cancellationToken)
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
        payment.Confirm(confirmedByUserId, now);

        // Owner decision 2026-08-30 rule 1: a human confirming money by hand is
        // the audited exception path. A gateway confirmation reaches
        // ProcessProviderConfirmationAsync instead and is attributed to the
        // provider, not to an admin, so the two are distinguishable in review.
        _db.AuditLogEntries.Add(new AuditLogEntry("Payment", payment.ReferenceCode, "ManualPaymentConfirmed",
            confirmedByUserId, reason: $"Admin confirmed {payment.Method} payment of {payment.Amount.Amount} {payment.Amount.Currency}.",
            beforeJson: null, afterJson: null, now));

        await SettleSubscriptionIfFullyPaidAsync(payment, confirmedByUserId, now, cancellationToken);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return new ConfirmPaymentResult(ConfirmPaymentOutcome.Confirmed);
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

            reloaded.Confirm(confirmedByUserId, now);
            await _db.SaveChangesAsync(cancellationToken);
            return new ConfirmPaymentResult(ConfirmPaymentOutcome.Confirmed);
        }
    }

    public async Task RejectAsync(long paymentId, string reason, long rejectedByUserId, CancellationToken cancellationToken)
    {
        var payment = await _db.Payments.FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken)
            ?? throw new InvalidOperationException("Payment not found.");
        payment.Reject(reason);
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

    private async Task SettleSubscriptionIfFullyPaidAsync(Payment payment, long confirmedByUserId, Instant now, CancellationToken ct)
    {
        if (payment.SubscriptionId is null)
        {
            return;
        }

        var subscription = await _db.Subscriptions.FirstAsync(s => s.Id == payment.SubscriptionId, ct);

        var alreadyPosted = await _db.EntitlementLedgerEntries.AnyAsync(
            l => l.SubscriptionId == subscription.Id && l.Reason == LedgerReason.Purchase, ct);
        if (alreadyPosted)
        {
            return; // D-38's ledger write is a one-time event per subscription.
        }

        var confirmedTotal = await _db.Payments
            .Where(p => p.SubscriptionId == subscription.Id && p.Status == PaymentStatus.Confirmed)
            .SumAsync(p => (decimal?)p.Amount.Amount, ct) ?? 0m;
        // Include the payment being confirmed right now (not yet reflected by the query above
        // inside the same unit of work until SaveChanges, so add it explicitly).
        confirmedTotal += payment.Amount.Amount;

        if (confirmedTotal < subscription.Price.Amount)
        {
            return; // D-38: no partial activation — full payment only.
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
    }

    private static string GenerateReferenceCode() =>
        "MVT-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
}
