using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Ledger;
using MVTeaches.Application.Subscriptions;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Common;
using MVTeaches.Domain.Ledger;
using MVTeaches.Domain.People;
using MVTeaches.Domain.Subscriptions;
using MVTeaches.Infrastructure.Ledger;
using MVTeaches.Infrastructure.Persistence;
using MVTeaches.Infrastructure.Subscriptions;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace MVTeaches.Tests.Subscriptions;

/// <summary>
/// §23 (pricing plans) + §19.2/§20.2 (subscriptions + the entitlement ledger,
/// D-13/D-38) — closes the "there was no way to create a subscription at all"
/// gap. The payment-confirms-and-activates half of this flow was already
/// tested in PaymentServiceTests; this covers the purchase-creation half and
/// the admin-grant-with-no-payment path.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class SubscriptionServiceTests
{
    private readonly TestDatabaseFixture _fixture;
    private static long _idSeed = 37_000_000; // a range distinct from every other test class sharing this DB

    public SubscriptionServiceTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static long NextId() => Interlocked.Increment(ref _idSeed);

    private static string TwoLetterCode(long seed)
    {
        var n = (int)(seed % 676);
        return string.Concat((char)('A' + n / 26), (char)('A' + n % 26));
    }

    private static async Task<(int CountryId, long CourseId, int LevelId, long StudentId)> SeedCatalogAndStudentAsync(MvTeachesDbContext db)
    {
        var countryId = (int)NextId();
        var courseId = NextId();
        var levelId = (int)NextId();
        db.Countries.Add(new Country(countryId, TwoLetterCode(countryId), "دولة", "Country", "JOD", "+962", "Asia/Amman"));
        db.Courses.Add(new Course("C" + courseId, "دورة", "Course"));
        db.Levels.Add(new Level(levelId, "L" + levelId, "مستوى", "Level", levelId));
        var student = new Student(countryId, "Student", new LocalDate(2010, 1, 1));
        db.Students.Add(student);
        await db.SaveChangesAsync();
        return (countryId, courseId, levelId, student.Id);
    }

    private static ISubscriptionService CreateService(MvTeachesDbContext db, Instant now) =>
        new SubscriptionService(db, new FakeClock(now));

    private static IEntitlementBalanceQuery CreateBalanceQuery(MvTeachesDbContext db) =>
        new EntitlementBalanceQuery(db);

    [Fact]
    public async Task Creating_a_pricing_plan_persists_the_snapshot_fields()
    {
        await using var db = _fixture.CreateContext();
        var (countryId, courseId, levelId, _) = await SeedCatalogAndStudentAsync(db);
        var service = CreateService(db, SystemClock.Instance.GetCurrentInstant());

        var result = await service.CreatePricingPlanAsync(countryId, courseId, levelId, null, SessionType.Group,
            10, 600, new Money(75m, "JOD"), 90, new LocalDate(2026, 1, 1), createdByUserId: NextId(), CancellationToken.None);

        var plan = await db.PricingPlans.FirstAsync(p => p.Id == result.PricingPlanId);
        Assert.Equal(10, plan.SessionsCount);
        Assert.Equal(600, plan.MinutesTotal);
        Assert.Equal(75m, plan.Amount.Amount);
        Assert.Equal("JOD", plan.Amount.Currency);
        Assert.Equal(90, plan.ValidityDays);
        Assert.True(plan.IsActive);
    }

    [Fact]
    public async Task Purchasing_from_a_plan_creates_a_draft_subscription_snapshotting_the_plan()
    {
        await using var db = _fixture.CreateContext();
        var (countryId, courseId, levelId, studentId) = await SeedCatalogAndStudentAsync(db);
        var planService = CreateService(db, SystemClock.Instance.GetCurrentInstant());
        var plan = await planService.CreatePricingPlanAsync(countryId, courseId, levelId, null, SessionType.Group,
            8, 480, new Money(60m, "JOD"), 60, new LocalDate(2026, 1, 1), createdByUserId: NextId(), CancellationToken.None);

        var result = await planService.PurchaseFromPlanAsync(studentId, plan.PricingPlanId, levelId,
            SubscriptionOrigin.GuardianPurchase, createdByUserId: NextId(), CancellationToken.None);

        var subscription = await db.Subscriptions.FirstAsync(s => s.Id == result.SubscriptionId);
        Assert.Equal(SubscriptionStatus.Draft, subscription.Status);
        Assert.Equal(8, subscription.SessionsCount);
        Assert.Equal(480, subscription.MinutesTotal);
        Assert.Equal(60m, subscription.Price.Amount);
        Assert.Equal(SubscriptionOrigin.GuardianPurchase, subscription.Origin);
        Assert.Equal(plan.PricingPlanId, subscription.PricingPlanId);

        // Draft — no payment yet, so no ledger entry should exist for it.
        var balance = await CreateBalanceQuery(db).GetSubscriptionBalanceAsync(subscription.Id, CancellationToken.None);
        Assert.Equal(0, balance);
    }

    [Fact]
    public async Task Granting_a_free_subscription_activates_immediately_and_posts_the_admin_grant_ledger_entry()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var (countryId, courseId, levelId, studentId) = await SeedCatalogAndStudentAsync(db);
        var service = CreateService(db, now);

        var result = await service.GrantAdminSubscriptionAsync(studentId, countryId, courseId, levelId,
            sessionsCount: 5, minutesTotal: 300, validityDays: 30, createdByUserId: NextId(),
            reason: "Goodwill gesture", CancellationToken.None);

        var subscription = await db.Subscriptions.FirstAsync(s => s.Id == result.SubscriptionId);
        Assert.Equal(SubscriptionStatus.Active, subscription.Status); // activated immediately, no payment step
        Assert.Equal(SubscriptionOrigin.AdminCreated, subscription.Origin);
        Assert.Equal(0m, subscription.Price.Amount); // free (D-13)

        var entry = await db.EntitlementLedgerEntries.SingleAsync(l => l.SubscriptionId == subscription.Id);
        Assert.Equal(LedgerReason.AdminGrant, entry.Reason);
        Assert.Equal(300, entry.DeltaMinutes);

        var balance = await CreateBalanceQuery(db).GetSubscriptionBalanceAsync(subscription.Id, CancellationToken.None);
        Assert.Equal(300, balance);
    }

    [Fact]
    public async Task Granting_a_free_subscription_without_a_reason_is_rejected()
    {
        await using var db = _fixture.CreateContext();
        var (countryId, courseId, levelId, studentId) = await SeedCatalogAndStudentAsync(db);
        var service = CreateService(db, SystemClock.Instance.GetCurrentInstant());

        // D-13: an admin-created subscription requires a reason — enforced by
        // Subscription's own constructor, exercised here through the service.
        await Assert.ThrowsAsync<ArgumentException>(() => service.GrantAdminSubscriptionAsync(
            studentId, countryId, courseId, levelId, 5, 300, 30, NextId(), reason: "", CancellationToken.None));
    }

    [Fact]
    public async Task Entitlement_balance_sums_every_ledger_entry_for_the_subscription()
    {
        await using var db = _fixture.CreateContext();
        var (countryId, courseId, levelId, studentId) = await SeedCatalogAndStudentAsync(db);
        var subscription = new MVTeaches.Domain.Subscriptions.Subscription(studentId, countryId, courseId, levelId,
            new Money(0m, "JOD"), null, 5, 300, new LocalDate(2026, 1, 1), 30, SubscriptionOrigin.AdminCreated,
            NextId(), "seed");
        subscription.Activate();
        db.Subscriptions.Add(subscription);
        await db.SaveChangesAsync();

        var now = SystemClock.Instance.GetCurrentInstant();
        db.EntitlementLedgerEntries.Add(EntitlementLedgerEntry.ForAdminGrant(studentId, subscription.Id, courseId, levelId, 300, NextId(), "seed", now));
        db.EntitlementLedgerEntries.Add(EntitlementLedgerEntry.ForConsumption(studentId, subscription.Id, courseId, levelId, 60, NextId(), NextId(), now));
        await db.SaveChangesAsync();

        var balance = await CreateBalanceQuery(db).GetSubscriptionBalanceAsync(subscription.Id, CancellationToken.None);
        Assert.Equal(240, balance);
    }
}
