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

    private async Task<(long StudentId, long SubscriptionId, Money Price)> SeedBlockedSubscriptionAsync(MvTeachesDbContext db)
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

        var price = new Money(50m, "JOD");
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
}
