using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Payments;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Common;
using MVTeaches.Domain.Ledger;
using MVTeaches.Domain.Payments;
using MVTeaches.Domain.People;
using MVTeaches.Domain.Subscriptions;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Payments;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace MVTeaches.Tests.Payments;

/// <summary>Master engineering prompt §28 items 10-11 (D-11/D-14/D-38).</summary>
[Collection(nameof(DatabaseCollection))]
public class PaymentServiceTests
{
    private readonly TestDatabaseFixture _fixture;
    private static long _idSeed = 9_000_000;

    public PaymentServiceTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static long NextId() => Interlocked.Increment(ref _idSeed);

    private static string TwoLetterCode(long seed)
    {
        var n = (int)(seed % 676);
        return string.Concat((char)('A' + n / 26), (char)('A' + n % 26));
    }

    private async Task<long> CreateUserAsync(MvTeachesDbContext db)
    {
        var user = new ApplicationUser
        {
            UserName = $"u-{Guid.NewGuid():N}",
            NormalizedUserName = $"U-{Guid.NewGuid():N}",
            Email = $"{Guid.NewGuid():N}@test.mvteaches.local",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task<(long StudentId, long SubscriptionId, Money Price)> SeedBlockedSubscriptionAsync(MvTeachesDbContext db) =>
        await SeedBlockedSubscriptionAsync(db, new Money(50m, "JOD"));

    private async Task<(long StudentId, long SubscriptionId, Money Price)> SeedBlockedSubscriptionAsync(MvTeachesDbContext db, Money price)
    {
        var countryId = (int)NextId();
        var courseId = NextId();
        var levelId = (int)NextId();
        var studentUserId = await CreateUserAsync(db);

        db.Countries.Add(new Country(countryId, TwoLetterCode(countryId), "دولة", "Country", "JOD", "+962", "Asia/Amman"));
        db.Courses.Add(new Course("C" + courseId, "دورة", "Course"));
        db.Levels.Add(new Level(levelId, "L" + levelId, "مستوى", "Level", levelId));

        var student = new Student(countryId, "Student", new LocalDate(2010, 1, 1), studentUserId);
        student.MarkVerified();
        student.BlockForPayment(); // D-14: created with an outstanding balance
        db.Students.Add(student);
        await db.SaveChangesAsync();

        var subscription = new Subscription(student.Id, countryId, courseId, levelId, SessionType.Group, price, null,
            10, 600, new LocalDate(2026, 1, 1), 90, SubscriptionOrigin.SelfPurchase, studentUserId, null);
        db.Subscriptions.Add(subscription);
        await db.SaveChangesAsync();

        return (student.Id, subscription.Id, price);
    }

    private IPaymentService CreateService(MvTeachesDbContext db, Instant now) => new PaymentService(db, new FakeClock(now));

    [Fact]
    public async Task Confirming_full_payment_posts_the_ledger_once_and_lifts_the_payment_block()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var (studentId, subscriptionId, price) = await SeedBlockedSubscriptionAsync(db);

        var service = CreateService(db, now);
        var recorded = await service.RecordManualPaymentAsync(
            new RecordPaymentRequest(studentId, subscriptionId, PayerUserId: null, price, PaymentMethod.BankTransfer, ProofFileId: null),
            CancellationToken.None);

        var confirmResult = await service.ConfirmAsync(recorded.PaymentId, confirmedByUserId: NextId(), CancellationToken.None);
        Assert.Equal(ConfirmPaymentOutcome.Confirmed, confirmResult.Outcome);

        await using var verify = _fixture.CreateContext();
        var student = verify.Students.Single(s => s.Id == studentId);
        Assert.Equal(StudentStatus.Active, student.Status); // D-14: block lifted in the same transaction

        var ledgerCount = verify.EntitlementLedgerEntries.Count(
            l => l.SubscriptionId == subscriptionId && l.Reason == LedgerReason.Purchase);
        Assert.Equal(1, ledgerCount);

        // Owner decision 2026-08-30 rule 9: purchase confirmation.
        Assert.True(verify.NotificationOutboxItems.Any(
            n => n.Event == MVTeaches.Domain.Notifications.NotificationEvent.SubscriptionConfirmed && n.RecipientUserId == student.UserId!.Value));
    }

    /// <summary>
    /// Owner decision 2026-08-30 rule 1: the routine path is a student paying
    /// through the gateway with the entitlement activating off the trusted
    /// server-side confirmation; a hand-keyed payment is the exception and
    /// "must be permission-protected and audit-logged". The permission half is
    /// the Admin/SystemAdmin-only page (covered in AuthorizationTests); this is
    /// the audit half.
    /// </summary>
    [Fact]
    public async Task Recording_and_confirming_a_manual_payment_both_leave_an_audit_trail()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var (studentId, subscriptionId, price) = await SeedBlockedSubscriptionAsync(db);
        var adminUserId = NextId();

        var service = CreateService(db, now);
        var recorded = await service.RecordManualPaymentAsync(
            new RecordPaymentRequest(studentId, subscriptionId, PayerUserId: null, price, PaymentMethod.Cash, ProofFileId: null),
            CancellationToken.None);
        await service.ConfirmAsync(recorded.PaymentId, adminUserId, CancellationToken.None);

        await using var verify = _fixture.CreateContext();
        var entries = verify.AuditLogEntries
            .Where(a => a.EntityType == "Payment" && a.EntityId == recorded.ReferenceCode)
            .ToList();

        Assert.Contains(entries, a => a.Action == "ManualPaymentRecorded");
        var confirmEntry = Assert.Single(entries, a => a.Action == "ManualPaymentConfirmed");
        Assert.Equal(adminUserId, confirmEntry.PerformedByUserId); // who confirmed the money, by name
    }

    [Fact]
    public async Task A_payment_cannot_be_confirmed_twice()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var (studentId, subscriptionId, price) = await SeedBlockedSubscriptionAsync(db);

        var service = CreateService(db, now);
        var recorded = await service.RecordManualPaymentAsync(
            new RecordPaymentRequest(studentId, subscriptionId, null, price, PaymentMethod.BankTransfer, null),
            CancellationToken.None);

        var first = await service.ConfirmAsync(recorded.PaymentId, NextId(), CancellationToken.None);
        var second = await service.ConfirmAsync(recorded.PaymentId, NextId(), CancellationToken.None);

        Assert.Equal(ConfirmPaymentOutcome.Confirmed, first.Outcome);
        Assert.Equal(ConfirmPaymentOutcome.AlreadyConfirmed, second.Outcome);

        await using var verify = _fixture.CreateContext();
        Assert.Equal(1, verify.EntitlementLedgerEntries.Count(
            l => l.SubscriptionId == subscriptionId && l.Reason == LedgerReason.Purchase));
    }

    [Fact]
    public async Task Provider_webhook_replay_for_the_same_transaction_is_safe()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var (studentId, subscriptionId, price) = await SeedBlockedSubscriptionAsync(db);

        var payment = new Payment(studentId, subscriptionId, null, price, PaymentMethod.Card,
            providerKey: "future-gateway", referenceCode: "MVT-" + Guid.NewGuid().ToString("N")[..8],
            now, providerTransactionId: "TXN-REPLAY-TEST");
        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        var service = CreateService(db, now);
        var first = await service.ProcessProviderConfirmationAsync("future-gateway", "TXN-REPLAY-TEST", NextId(), CancellationToken.None);
        var replay = await service.ProcessProviderConfirmationAsync("future-gateway", "TXN-REPLAY-TEST", NextId(), CancellationToken.None);

        Assert.Equal(ConfirmPaymentOutcome.Confirmed, first.Outcome);
        Assert.Equal(ConfirmPaymentOutcome.AlreadyConfirmed, replay.Outcome);

        await using var verify = _fixture.CreateContext();
        Assert.Equal(1, verify.EntitlementLedgerEntries.Count(
            l => l.SubscriptionId == subscriptionId && l.Reason == LedgerReason.Purchase));
    }

    /// <summary>Release-readiness audit finding: unlike ux_ent_consumption
    /// (D-83), there was no database-level backstop against TWO different,
    /// legitimately-distinct Payment rows for the SAME subscription both
    /// crossing the "fully paid" threshold concurrently — e.g. two admins
    /// confirming two separate manual payments for the same subscription at
    /// the same moment. Both would independently observe "no Purchase entry
    /// posted yet" and both attempt to post one, double-crediting the
    /// subscription's minutes. Proves exactly one Purchase entry survives and
    /// BOTH payments still end up Confirmed (the second confirmation is real
    /// money too — it just doesn't get to post a second, redundant ledger
    /// entry, the same rule already applied to a THIRD sequential overpayment
    /// today).</summary>
    [Fact]
    public async Task Two_concurrent_payment_confirmations_on_the_same_subscription_post_the_ledger_exactly_once()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var seedDb = _fixture.CreateContext();
        var (studentId, subscriptionId, price) = await SeedBlockedSubscriptionAsync(seedDb);

        // Two independent, fully-priced payments on the same subscription —
        // an overpayment/duplicate-entry scenario, not a double-submit of the
        // same payment row (that path is already covered by
        // A_payment_cannot_be_confirmed_twice).
        // ⚠️ Each Payment needs its OWN Money instance (see
        // Duplicate_reference_codes_are_impossible_at_the_database_level's
        // remarks on this owned-type gotcha) — sharing `price` here throws.
        var recorded1 = await new PaymentService(seedDb, new FakeClock(now)).RecordManualPaymentAsync(
            new RecordPaymentRequest(studentId, subscriptionId, null, new Money(price.Amount, price.Currency), PaymentMethod.BankTransfer, null), CancellationToken.None);
        var recorded2 = await new PaymentService(seedDb, new FakeClock(now)).RecordManualPaymentAsync(
            new RecordPaymentRequest(studentId, subscriptionId, null, new Money(price.Amount, price.Currency), PaymentMethod.BankTransfer, null), CancellationToken.None);

        // Separate DbContexts (like Two_concurrent_join_requests_still_produce_exactly_one_consumption)
        // so this is a genuine concurrent database race, not two sequential
        // calls sharing one context.
        var service1 = new PaymentService(_fixture.CreateContext(), new FakeClock(now));
        var service2 = new PaymentService(_fixture.CreateContext(), new FakeClock(now));

        var task1 = service1.ConfirmAsync(recorded1.PaymentId, NextId(), CancellationToken.None);
        var task2 = service2.ConfirmAsync(recorded2.PaymentId, NextId(), CancellationToken.None);
        var results = await Task.WhenAll(task1, task2);

        // Both payments represent real money received — neither should be left
        // stuck Pending or Rejected just because it lost the ledger race.
        Assert.All(results, r => Assert.Equal(ConfirmPaymentOutcome.Confirmed, r.Outcome));

        await using var verify = _fixture.CreateContext();
        Assert.Equal(1, verify.EntitlementLedgerEntries.Count(
            l => l.SubscriptionId == subscriptionId && l.Reason == LedgerReason.Purchase));
        Assert.Equal(2, verify.Payments.Count(p => p.SubscriptionId == subscriptionId && p.Status == PaymentStatus.Confirmed));

        var student = verify.Students.Single(s => s.Id == studentId);
        Assert.Equal(StudentStatus.Active, student.Status);
    }

    [Fact]
    public async Task Duplicate_reference_codes_are_impossible_at_the_database_level()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var (studentId, subscriptionId, price) = await SeedBlockedSubscriptionAsync(db);

        var duplicateCode = "MVT-DUPTEST";
        // ⚠️ Each Payment needs its OWN Money instance — Money is an EF owned
        // type, and sharing one reference-typed value object across two
        // different owner aggregates confuses the change tracker (this is not
        // a database concern, purely an EF Core owned-entity gotcha).
        db.Payments.Add(new Payment(studentId, subscriptionId, null, new Money(price.Amount, price.Currency), PaymentMethod.Cash, "manual", duplicateCode, now));
        await db.SaveChangesAsync();

        db.Payments.Add(new Payment(studentId, subscriptionId, null, new Money(price.Amount, price.Currency), PaymentMethod.Cash, "manual", duplicateCode, now));
        await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(() => db.SaveChangesAsync());
    }

    // ---- Self-service manual payments (owner decision 2026-08-30, Section 3/5/6) ----

    private async Task<long> SeedActivePaymentMethodAsync(MvTeachesDbContext db, Instant now)
    {
        var method = new PaymentMethodConfig(PaymentMethod.BankTransfer, "MVTeaches Center", null, "JO00 TEST 0000",
            "Test Bank", null, "Jordan", null, "JOD", NextId(), now);
        db.PaymentMethodConfigs.Add(method);
        await db.SaveChangesAsync();
        return method.Id;
    }

    /// <summary>Section 3/6: the self-service counterpart to
    /// RecordManualPaymentAsync must be safe to expose directly to a payer —
    /// proves the amount always comes from the subscription's own price
    /// snapshot, never anything the caller could have supplied (there is no
    /// amount parameter on RequestOwnPaymentAsync at all).</summary>
    [Fact]
    public async Task RequestOwnPaymentAsync_by_the_student_themself_creates_a_payment_for_exactly_the_subscription_price()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var (studentId, subscriptionId, price) = await SeedBlockedSubscriptionAsync(db);
        var methodId = await SeedActivePaymentMethodAsync(db, now);
        var studentUserId = await db.Students.Where(s => s.Id == studentId).Select(s => s.UserId!.Value).FirstAsync();

        var service = CreateService(db, now);
        var result = await service.RequestOwnPaymentAsync(studentId, subscriptionId, methodId, studentUserId, CancellationToken.None);

        Assert.Equal(RequestOwnPaymentOutcome.Requested, result.Outcome);
        Assert.NotNull(result.PaymentId);

        var payment = await db.Payments.FirstAsync(p => p.Id == result.PaymentId);
        Assert.Equal(price.Amount, payment.Amount.Amount);
        Assert.Equal(price.Currency, payment.Amount.Currency);
        Assert.Equal(PaymentStatus.Pending, payment.Status);
    }

    /// <summary>The same self-or-active-guardian IDOR guard every other
    /// payment/placement operation uses — an unrelated account must never be
    /// able to spawn a payment request against someone else's subscription.</summary>
    [Fact]
    public async Task RequestOwnPaymentAsync_by_an_unrelated_user_is_refused()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var (studentId, subscriptionId, _) = await SeedBlockedSubscriptionAsync(db);
        var methodId = await SeedActivePaymentMethodAsync(db, now);
        var strangerUserId = await CreateUserAsync(db);

        var service = CreateService(db, now);
        var result = await service.RequestOwnPaymentAsync(studentId, subscriptionId, methodId, strangerUserId, CancellationToken.None);

        Assert.Equal(RequestOwnPaymentOutcome.Unauthorized, result.Outcome);
        Assert.False(await db.Payments.AnyAsync(p => p.SubscriptionId == subscriptionId));
    }

    /// <summary>A second click, or a resubmission attempt, must never spawn a
    /// duplicate payment request for the same Draft subscription.</summary>
    [Fact]
    public async Task RequestOwnPaymentAsync_twice_on_the_same_subscription_is_refused_the_second_time()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var (studentId, subscriptionId, _) = await SeedBlockedSubscriptionAsync(db);
        var methodId = await SeedActivePaymentMethodAsync(db, now);
        var studentUserId = await db.Students.Where(s => s.Id == studentId).Select(s => s.UserId!.Value).FirstAsync();

        var service = CreateService(db, now);
        var first = await service.RequestOwnPaymentAsync(studentId, subscriptionId, methodId, studentUserId, CancellationToken.None);
        var second = await service.RequestOwnPaymentAsync(studentId, subscriptionId, methodId, studentUserId, CancellationToken.None);

        Assert.Equal(RequestOwnPaymentOutcome.Requested, first.Outcome);
        Assert.Equal(RequestOwnPaymentOutcome.AlreadyRequested, second.Outcome);
        Assert.Equal(1, await db.Payments.CountAsync(p => p.SubscriptionId == subscriptionId));
    }

    /// <summary>Section 6: reporting a transfer (or uploading its receipt)
    /// must never, by itself, add minutes or mark the payment paid — only
    /// the admin's explicit confirm action does that.</summary>
    [Fact]
    public async Task AttachTransferDetailsAsync_never_confirms_or_activates_anything_by_itself()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var (studentId, subscriptionId, price) = await SeedBlockedSubscriptionAsync(db);
        var studentUserId = await db.Students.Where(s => s.Id == studentId).Select(s => s.UserId!.Value).FirstAsync();

        var service = CreateService(db, now);
        var recorded = await service.RecordManualPaymentAsync(
            new RecordPaymentRequest(studentId, subscriptionId, PayerUserId: null, price, PaymentMethod.BankTransfer, ProofFileId: null),
            CancellationToken.None);

        var attachResult = await service.AttachTransferDetailsAsync(recorded.PaymentId, studentUserId, isAdminInitiated: false,
            "Payer Name", new LocalDate(2026, 8, 30), "REF-123", receiptFileId: 999_999, CancellationToken.None);

        Assert.Equal(AttachTransferDetailsOutcome.Attached, attachResult.Outcome);

        var payment = await db.Payments.FirstAsync(p => p.Id == recorded.PaymentId);
        Assert.Equal(PaymentStatus.Pending, payment.Status); // still pending — reporting a transfer is not confirming it
        Assert.True(payment.HasSubmittedTransferDetails);

        var subscription = await db.Subscriptions.FirstAsync(s => s.Id == subscriptionId);
        Assert.Equal(SubscriptionStatus.Draft, subscription.Status); // never activated by attaching transfer details alone
        Assert.False(await db.EntitlementLedgerEntries.AnyAsync(l => l.SubscriptionId == subscriptionId));
    }

    /// <summary>The same self-or-active-guardian IDOR guard applies to
    /// reporting a transfer, not just requesting the payment in the first
    /// place — an unrelated account must never be able to attach transfer
    /// details (or a receipt) to someone else's payment.</summary>
    [Fact]
    public async Task AttachTransferDetailsAsync_by_an_unrelated_user_is_refused()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var (studentId, subscriptionId, price) = await SeedBlockedSubscriptionAsync(db);
        var strangerUserId = await CreateUserAsync(db);

        var service = CreateService(db, now);
        var recorded = await service.RecordManualPaymentAsync(
            new RecordPaymentRequest(studentId, subscriptionId, PayerUserId: null, price, PaymentMethod.BankTransfer, ProofFileId: null),
            CancellationToken.None);

        var attachResult = await service.AttachTransferDetailsAsync(recorded.PaymentId, strangerUserId, isAdminInitiated: false,
            "Someone Else", null, null, null, CancellationToken.None);

        Assert.Equal(AttachTransferDetailsOutcome.Unauthorized, attachResult.Outcome);
        var payment = await db.Payments.FirstAsync(p => p.Id == recorded.PaymentId);
        Assert.False(payment.HasSubmittedTransferDetails);
    }

    /// <summary>A Pending payment under review must never be counted toward
    /// the subscription's confirmed total — only a genuinely Confirmed
    /// payment (§22.1's own derivation rule) contributes.</summary>
    [Fact]
    public async Task A_pending_payment_under_review_is_never_counted_as_confirmed_revenue()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var (studentId, subscriptionId, price) = await SeedBlockedSubscriptionAsync(db);
        var studentUserId = await db.Students.Where(s => s.Id == studentId).Select(s => s.UserId!.Value).FirstAsync();

        var service = CreateService(db, now);
        var recorded = await service.RecordManualPaymentAsync(
            new RecordPaymentRequest(studentId, subscriptionId, PayerUserId: null, price, PaymentMethod.BankTransfer, ProofFileId: null),
            CancellationToken.None);
        await service.AttachTransferDetailsAsync(recorded.PaymentId, studentUserId, isAdminInitiated: false,
            "Payer Name", new LocalDate(2026, 8, 30), "REF-456", null, CancellationToken.None);

        var subscription = await db.Subscriptions.FirstAsync(s => s.Id == subscriptionId);
        Assert.Equal(SubscriptionStatus.Draft, subscription.Status);
        Assert.False(await db.EntitlementLedgerEntries.AnyAsync(l => l.SubscriptionId == subscriptionId && l.Reason == LedgerReason.Purchase));
    }

    /// <summary>Section 5/9: a correction (rejection) must never delete the
    /// audit trail — the Payment row survives, carrying its reason, so a
    /// resubmission's history is never silently erased.</summary>
    [Fact]
    public async Task RejectAsync_preserves_the_payment_row_and_records_the_reason()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var (studentId, subscriptionId, price) = await SeedBlockedSubscriptionAsync(db);

        var service = CreateService(db, now);
        var recorded = await service.RecordManualPaymentAsync(
            new RecordPaymentRequest(studentId, subscriptionId, PayerUserId: null, price, PaymentMethod.BankTransfer, ProofFileId: null),
            CancellationToken.None);

        await service.RejectAsync(recorded.PaymentId, "Receipt does not match the requested amount.", rejectedByUserId: NextId(), CancellationToken.None);

        var payment = await db.Payments.FirstAsync(p => p.Id == recorded.PaymentId);
        Assert.Equal(PaymentStatus.Rejected, payment.Status);
        Assert.Equal("Receipt does not match the requested amount.", payment.RejectionReason);

        // Section 8: the payer is told a correction is needed (never the bank
        // details, never the receipt image itself).
        var notification = await db.NotificationOutboxItems.FirstOrDefaultAsync(
            n => n.Event == MVTeaches.Domain.Notifications.NotificationEvent.PaymentNeedsCorrection);
        Assert.NotNull(notification);
        Assert.DoesNotContain("IBAN", notification!.PayloadJson);
    }

    // ---- Shortfall / top-up policy (owner decision 2026-08-30, Part 1) ----
    // "يجب استلام كامل سعر الباقة قبل تفعيلها ... لا نسبة تسامح، ولا إعفاء
    // تلقائي" — a payment confirmed for less than the full price must never
    // activate the subscription, and the shortfall must be recoverable via a
    // genuine, independent supplementary payment against the SAME
    // subscription — never a new subscription, never editing the first row.

    /// <summary>The exact example from the owner's instruction: price 100,
    /// 97 arrives and is confirmed — the package stays inactive, and the
    /// remaining 3 is exactly what the funding status reports.</summary>
    [Fact]
    public async Task Confirming_97_of_a_100_price_never_activates_the_package()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var (studentId, subscriptionId, _) = await SeedBlockedSubscriptionAsync(db, new Money(100m, "JOD"));

        var service = CreateService(db, now);
        var recorded = await service.RecordManualPaymentAsync(
            new RecordPaymentRequest(studentId, subscriptionId, PayerUserId: null, new Money(100m, "JOD"), PaymentMethod.InternationalBankTransfer, null),
            CancellationToken.None);

        var confirmResult = await service.ConfirmAsync(recorded.PaymentId, confirmedByUserId: NextId(), CancellationToken.None,
            actuallyReceivedAmount: new Money(97m, "JOD"));

        Assert.Equal(ConfirmPaymentOutcome.ConfirmedButSubscriptionNotYetFullyFunded, confirmResult.Outcome);

        var subscription = await db.Subscriptions.FirstAsync(s => s.Id == subscriptionId);
        Assert.Equal(SubscriptionStatus.Draft, subscription.Status);
        Assert.False(await db.EntitlementLedgerEntries.AnyAsync(l => l.SubscriptionId == subscriptionId));

        var funding = await service.GetSubscriptionFundingStatusAsync(subscriptionId, CancellationToken.None);
        Assert.Equal(100m, funding.Price.Amount);
        Assert.Equal(97m, funding.ConfirmedReceived);
        Assert.Equal(3m, funding.RemainingOwed.Amount);
        Assert.False(funding.IsFullyFunded);
    }

    /// <summary>The rest of the owner's example: a genuine supplementary
    /// transfer for exactly the remaining 3 is recorded as its OWN
    /// independent Payment row (never editing the first one, never a new
    /// subscription) and, once confirmed, activates the package exactly
    /// once with the full 100's worth of minutes — never double-credited.</summary>
    [Fact]
    public async Task Confirming_a_supplementary_payment_for_the_remaining_balance_activates_the_package_exactly_once()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var (studentId, subscriptionId, _) = await SeedBlockedSubscriptionAsync(db, new Money(100m, "JOD"));

        var service = CreateService(db, now);
        var firstPayment = await service.RecordManualPaymentAsync(
            new RecordPaymentRequest(studentId, subscriptionId, PayerUserId: null, new Money(100m, "JOD"), PaymentMethod.InternationalBankTransfer, null),
            CancellationToken.None);
        await service.ConfirmAsync(firstPayment.PaymentId, confirmedByUserId: NextId(), CancellationToken.None,
            actuallyReceivedAmount: new Money(97m, "JOD"));

        // The guardian sends a second, genuinely independent transfer for
        // exactly the remaining amount — its own Payment row, its own
        // reference code, tied to the SAME subscription.
        var secondPayment = await service.RecordManualPaymentAsync(
            new RecordPaymentRequest(studentId, subscriptionId, PayerUserId: null, new Money(3m, "JOD"), PaymentMethod.InternationalBankTransfer, null),
            CancellationToken.None);
        Assert.NotEqual(firstPayment.PaymentId, secondPayment.PaymentId);

        var confirmResult = await service.ConfirmAsync(secondPayment.PaymentId, confirmedByUserId: NextId(), CancellationToken.None);
        Assert.Equal(ConfirmPaymentOutcome.Confirmed, confirmResult.Outcome);

        var subscription = await db.Subscriptions.FirstAsync(s => s.Id == subscriptionId);
        Assert.Equal(SubscriptionStatus.Active, subscription.Status);

        // Exactly one Purchase ledger entry, for the full minutes — never
        // split across the two payments, never doubled.
        var ledgerEntries = await db.EntitlementLedgerEntries.Where(l => l.SubscriptionId == subscriptionId).ToListAsync();
        Assert.Single(ledgerEntries);
        Assert.Equal(subscription.MinutesTotal, ledgerEntries[0].DeltaMinutes);

        // Both payments survive, independently, as the audit trail — neither
        // was edited to reach the total.
        Assert.Equal(2, await db.Payments.CountAsync(p => p.SubscriptionId == subscriptionId));
        var reloadedFirst = await db.Payments.FirstAsync(p => p.Id == firstPayment.PaymentId);
        Assert.Equal(97m, reloadedFirst.ReceivedAmount);

        var funding = await service.GetSubscriptionFundingStatusAsync(subscriptionId, CancellationToken.None);
        Assert.True(funding.IsFullyFunded);
        Assert.Equal(0m, funding.RemainingOwed.Amount);
    }

    /// <summary>A Pending (awaiting transfer/under review) payment, and a
    /// Rejected one, must never count toward the confirmed funding total —
    /// only a genuinely Confirmed payment does.</summary>
    [Fact]
    public async Task Pending_and_rejected_payments_are_never_counted_toward_confirmed_funding()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var (studentId, subscriptionId, _) = await SeedBlockedSubscriptionAsync(db, new Money(100m, "JOD"));

        var service = CreateService(db, now);

        // A rejected payment for the full amount.
        var rejected = await service.RecordManualPaymentAsync(
            new RecordPaymentRequest(studentId, subscriptionId, PayerUserId: null, new Money(100m, "JOD"), PaymentMethod.BankTransfer, null),
            CancellationToken.None);
        await service.RejectAsync(rejected.PaymentId, "Receipt unreadable.", rejectedByUserId: NextId(), CancellationToken.None);

        // A second payment still Pending (never confirmed).
        await service.RecordManualPaymentAsync(
            new RecordPaymentRequest(studentId, subscriptionId, PayerUserId: null, new Money(100m, "JOD"), PaymentMethod.BankTransfer, null),
            CancellationToken.None);

        var funding = await service.GetSubscriptionFundingStatusAsync(subscriptionId, CancellationToken.None);
        Assert.Equal(0m, funding.ConfirmedReceived);
        Assert.Equal(100m, funding.RemainingOwed.Amount);
        Assert.False(funding.IsFullyFunded);
    }

    /// <summary>Owner instruction: "افحص قيود منع تكرار طلب الدفع الحالية:
    /// يجب أن تمنع الطلبات المكررة، لكن لا تمنع استكمال دفعة ناقصة بحوالة
    /// جديدة فعلية." — RequestOwnPaymentAsync must refuse a second request
    /// while one is still Pending, but must allow a genuine follow-up
    /// request once the first is Confirmed-but-short, and that follow-up
    /// must ask for the REMAINING balance, not the full price again.</summary>
    [Fact]
    public async Task RequestOwnPaymentAsync_after_a_confirmed_shortfall_requests_only_the_remaining_balance()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var (studentId, subscriptionId, _) = await SeedBlockedSubscriptionAsync(db, new Money(100m, "JOD"));
        var methodId = await SeedActivePaymentMethodAsync(db, now);
        var studentUserId = await db.Students.Where(s => s.Id == studentId).Select(s => s.UserId!.Value).FirstAsync();

        var service = CreateService(db, now);
        var first = await service.RequestOwnPaymentAsync(studentId, subscriptionId, methodId, studentUserId, CancellationToken.None);
        Assert.Equal(RequestOwnPaymentOutcome.Requested, first.Outcome);
        Assert.Equal(100m, first.RequestedAmount!.Amount);

        // While the first request is still Pending, a second one is refused.
        var whilePending = await service.RequestOwnPaymentAsync(studentId, subscriptionId, methodId, studentUserId, CancellationToken.None);
        Assert.Equal(RequestOwnPaymentOutcome.AlreadyRequested, whilePending.Outcome);

        await service.ConfirmAsync(first.PaymentId!.Value, confirmedByUserId: NextId(), CancellationToken.None,
            actuallyReceivedAmount: new Money(97m, "JOD"));

        // Now that the first is Confirmed (not Pending), a genuine
        // supplementary request is allowed — and asks for exactly what's left.
        var topUp = await service.RequestOwnPaymentAsync(studentId, subscriptionId, methodId, studentUserId, CancellationToken.None);
        Assert.Equal(RequestOwnPaymentOutcome.Requested, topUp.Outcome);
        Assert.Equal(3m, topUp.RequestedAmount!.Amount);
        Assert.NotEqual(first.PaymentId, topUp.PaymentId);
    }

    /// <summary>Owner clarification 2026-08-30: "100/97/3" was only an
    /// EXAMPLE, never a hardcoded value in the production logic — the
    /// remaining-owed amount must always be (subscription price) minus (the
    /// SUM of every Confirmed, same-currency ReceivedAmount for that
    /// subscription), never a single fixed subtraction. Proven here with
    /// entirely different numbers (price 200, two separate confirmed
    /// payments of 90 and 60 summing to 150) and with TWO prior confirmed
    /// payments (not one), so a single-payment-only subtraction bug would
    /// fail this test even though it might pass the 100/97/3 one.</summary>
    [Fact]
    public async Task Remaining_owed_is_always_price_minus_the_sum_of_every_confirmed_same_currency_payment()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var (studentId, subscriptionId, _) = await SeedBlockedSubscriptionAsync(db, new Money(200m, "JOD"));

        var service = CreateService(db, now);

        var firstPayment = await service.RecordManualPaymentAsync(
            new RecordPaymentRequest(studentId, subscriptionId, PayerUserId: null, new Money(90m, "JOD"), PaymentMethod.BankTransfer, null),
            CancellationToken.None);
        await service.ConfirmAsync(firstPayment.PaymentId, confirmedByUserId: NextId(), CancellationToken.None);

        var secondPayment = await service.RecordManualPaymentAsync(
            new RecordPaymentRequest(studentId, subscriptionId, PayerUserId: null, new Money(60m, "JOD"), PaymentMethod.BankTransfer, null),
            CancellationToken.None);
        await service.ConfirmAsync(secondPayment.PaymentId, confirmedByUserId: NextId(), CancellationToken.None);

        var funding = await service.GetSubscriptionFundingStatusAsync(subscriptionId, CancellationToken.None);
        Assert.Equal(200m, funding.Price.Amount);
        Assert.Equal(150m, funding.ConfirmedReceived); // 90 + 60, a real sum — never a single fixed subtraction
        Assert.Equal(50m, funding.RemainingOwed.Amount);
        Assert.False(funding.IsFullyFunded);

        // A third, unrelated Pending payment for the full price must never
        // change this — only Confirmed payments are ever summed.
        await service.RecordManualPaymentAsync(
            new RecordPaymentRequest(studentId, subscriptionId, PayerUserId: null, new Money(200m, "JOD"), PaymentMethod.BankTransfer, null),
            CancellationToken.None);
        var fundingAfterPendingThird = await service.GetSubscriptionFundingStatusAsync(subscriptionId, CancellationToken.None);
        Assert.Equal(150m, fundingAfterPendingThird.ConfirmedReceived);
        Assert.Equal(50m, fundingAfterPendingThird.RemainingOwed.Amount);

        // Confirming the exact remaining 50 (a THIRD, independent payment)
        // activates the package exactly once, for the full minutes.
        var thirdPayment = await service.RecordManualPaymentAsync(
            new RecordPaymentRequest(studentId, subscriptionId, PayerUserId: null, new Money(50m, "JOD"), PaymentMethod.BankTransfer, null),
            CancellationToken.None);
        var finalConfirm = await service.ConfirmAsync(thirdPayment.PaymentId, confirmedByUserId: NextId(), CancellationToken.None);
        Assert.Equal(ConfirmPaymentOutcome.Confirmed, finalConfirm.Outcome);

        var subscription = await db.Subscriptions.FirstAsync(s => s.Id == subscriptionId);
        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
        Assert.Equal(1, await db.EntitlementLedgerEntries.CountAsync(l => l.SubscriptionId == subscriptionId));
    }

    /// <summary>Concurrency: two concurrent confirmations of a top-up
    /// scenario (the shortfall-confirmed payment already settled, then two
    /// concurrent attempts to confirm the SAME supplementary payment) must
    /// never double-credit the ledger or duplicate the payment's own
    /// Confirmed state — mirrors Two_concurrent_payment_confirmations...'s
    /// own race but specifically for the top-up path.</summary>
    [Fact]
    public async Task Concurrent_confirmations_of_the_same_top_up_payment_never_double_activate()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var seedDb = _fixture.CreateContext();
        var (studentId, subscriptionId, _) = await SeedBlockedSubscriptionAsync(seedDb, new Money(100m, "JOD"));

        var seedService = new PaymentService(seedDb, new FakeClock(now));
        var first = await seedService.RecordManualPaymentAsync(
            new RecordPaymentRequest(studentId, subscriptionId, PayerUserId: null, new Money(100m, "JOD"), PaymentMethod.InternationalBankTransfer, null),
            CancellationToken.None);
        await seedService.ConfirmAsync(first.PaymentId, confirmedByUserId: NextId(), CancellationToken.None,
            actuallyReceivedAmount: new Money(97m, "JOD"));

        var topUp = await seedService.RecordManualPaymentAsync(
            new RecordPaymentRequest(studentId, subscriptionId, PayerUserId: null, new Money(3m, "JOD"), PaymentMethod.InternationalBankTransfer, null),
            CancellationToken.None);

        // Two concurrent admins both click "confirm" on the SAME top-up
        // payment at once — separate DbContexts, a genuine database race.
        var service1 = new PaymentService(_fixture.CreateContext(), new FakeClock(now));
        var service2 = new PaymentService(_fixture.CreateContext(), new FakeClock(now));
        var task1 = service1.ConfirmAsync(topUp.PaymentId, NextId(), CancellationToken.None);
        var task2 = service2.ConfirmAsync(topUp.PaymentId, NextId(), CancellationToken.None);
        var results = await Task.WhenAll(task1, task2);

        Assert.Contains(results, r => r.Outcome == ConfirmPaymentOutcome.Confirmed);
        Assert.Contains(results, r => r.Outcome == ConfirmPaymentOutcome.AlreadyConfirmed);

        await using var verify = _fixture.CreateContext();
        Assert.Equal(1, await verify.EntitlementLedgerEntries.CountAsync(l => l.SubscriptionId == subscriptionId));
        var subscription = await verify.Subscriptions.FirstAsync(s => s.Id == subscriptionId);
        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
    }
}
