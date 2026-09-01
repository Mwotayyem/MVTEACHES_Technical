using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Ledger;
using MVTeaches.Application.Subscriptions;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Common;
using MVTeaches.Domain.Ledger;
using MVTeaches.Domain.People;
using MVTeaches.Domain.Placement;
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
///
/// Owner decision 2026-08-30 rules 1/4 added the level/session-type gating and
/// removed the trust gap where a caller could supply an arbitrary levelId —
/// those cases are covered here too.
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

    private static async Task<long> CreateUserAsync(MvTeachesDbContext db, string label)
    {
        var user = new MVTeaches.Infrastructure.Identity.ApplicationUser
        {
            UserName = $"{label}-{Guid.NewGuid():N}",
            NormalizedUserName = $"{label}-{Guid.NewGuid():N}".ToUpperInvariant(),
            Email = $"{Guid.NewGuid():N}@test.mvteaches.local",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<(int CountryId, long CourseId, int LevelId, long StudentId, long StudentUserId)> SeedCatalogAndStudentAsync(
        MvTeachesDbContext db, bool assignLevel = true)
    {
        var courseId = NextId();
        var levelId = (int)NextId();
        var studentUserId = await CreateUserAsync(db, "student");
        var countryId = await SeedCountryAsync(db);
        db.Courses.Add(new Course("C" + courseId, "دورة", "Course"));
        db.Levels.Add(new Level(levelId, "L" + levelId, "مستوى", "Level", levelId));
        var student = new Student(countryId, "Student", new LocalDate(2010, 1, 1), studentUserId);
        db.Students.Add(student);
        await db.SaveChangesAsync();

        if (assignLevel)
        {
            db.StudentLevels.Add(new StudentLevel(student.Id, levelId, studentUserId, AssignedByRole.Admin,
                LevelAssignmentSource.AdminOverride, null, "seed", SystemClock.Instance.GetCurrentInstant()));
            await db.SaveChangesAsync();
        }

        return (countryId, courseId, levelId, student.Id, studentUserId);
    }

    /// <summary>
    /// Same reason as SessionFinalizationServiceTests.GetOrSeedCountryAsync
    /// and PaymentServiceTests.SeedCountryAsync: the 2-letter country-code
    /// space is 676 wide and shared by every test class in the run, each
    /// deriving codes from its own NextId() range through identical
    /// arithmetic. Adding a single test to ANY class shifts one class's
    /// residues onto another's, which is exactly how this started failing.
    /// Catching the real unique violation and retrying with a fresh id is
    /// self-correcting; hand-picking non-overlapping ranges is not, because
    /// the next test added breaks it again.
    /// </summary>
    private static async Task<int> SeedCountryAsync(MvTeachesDbContext db)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var countryId = (int)NextId();
            db.Countries.Add(new Country(countryId, TwoLetterCode(countryId), "دولة", "Country", "JOD", "+962", "Asia/Amman"));
            try
            {
                await db.SaveChangesAsync();
                return countryId;
            }
            catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
            {
                db.ChangeTracker.Clear();
            }
        }

        throw new InvalidOperationException("Could not find a free 2-letter country code after 10 attempts.");
    }

    private static ISubscriptionService CreateService(MvTeachesDbContext db, Instant now) =>
        new SubscriptionService(db, new FakeClock(now));

    private static IEntitlementBalanceQuery CreateBalanceQuery(MvTeachesDbContext db) =>
        new EntitlementBalanceQuery(db);

    [Fact]
    public async Task Creating_a_pricing_plan_persists_the_snapshot_fields()
    {
        await using var db = _fixture.CreateContext();
        var (countryId, courseId, levelId, _, _) = await SeedCatalogAndStudentAsync(db);
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
    public async Task Purchasing_from_a_plan_creates_a_draft_subscription_snapshotting_the_plan_and_its_session_type()
    {
        await using var db = _fixture.CreateContext();
        var (countryId, courseId, levelId, studentId, studentUserId) = await SeedCatalogAndStudentAsync(db);
        var planService = CreateService(db, SystemClock.Instance.GetCurrentInstant());
        var plan = await planService.CreatePricingPlanAsync(countryId, courseId, levelId, null, SessionType.Private,
            8, 480, new Money(60m, "JOD"), 60, new LocalDate(2026, 1, 1), createdByUserId: NextId(), CancellationToken.None);

        var result = await planService.PurchaseFromPlanAsync(studentId, plan.PricingPlanId, studentUserId,
            SubscriptionOrigin.SelfPurchase, isAdminInitiated: false, CancellationToken.None);

        Assert.Equal(PurchaseFromPlanOutcome.Purchased, result.Outcome);
        var subscription = await db.Subscriptions.FirstAsync(s => s.Id == result.SubscriptionId);
        Assert.Equal(SubscriptionStatus.Draft, subscription.Status);
        Assert.Equal(8, subscription.SessionsCount);
        Assert.Equal(480, subscription.MinutesTotal);
        Assert.Equal(60m, subscription.Price.Amount);
        Assert.Equal(SubscriptionOrigin.SelfPurchase, subscription.Origin);
        Assert.Equal(plan.PricingPlanId, subscription.PricingPlanId);
        Assert.Equal(levelId, subscription.LevelId); // derived from the plan/student, never a caller-supplied value
        Assert.Equal(SessionType.Private, subscription.SessionType); // carried from the plan

        // Draft — no payment yet, so no ledger entry should exist for it.
        var balance = await CreateBalanceQuery(db).GetSubscriptionBalanceAsync(subscription.Id, CancellationToken.None);
        Assert.Equal(0, balance);
    }

    [Fact]
    public async Task A_guardian_may_purchase_on_behalf_of_their_child()
    {
        await using var db = _fixture.CreateContext();
        var (countryId, courseId, levelId, studentId, _) = await SeedCatalogAndStudentAsync(db);
        var guardianUserId = await CreateUserAsync(db, "guardian");
        var guardian = new Guardian(guardianUserId, "Guardian");
        db.Guardians.Add(guardian);
        await db.SaveChangesAsync();
        db.Guardianships.Add(new Guardianship(guardian.Id, studentId, GuardianRelationship.Parent, isPrimary: true, guardianUserId));
        await db.SaveChangesAsync();

        var service = CreateService(db, SystemClock.Instance.GetCurrentInstant());
        var plan = await service.CreatePricingPlanAsync(countryId, courseId, levelId, null, SessionType.Group,
            8, 480, new Money(60m, "JOD"), 60, new LocalDate(2026, 1, 1), createdByUserId: NextId(), CancellationToken.None);

        var result = await service.PurchaseFromPlanAsync(studentId, plan.PricingPlanId, guardianUserId,
            SubscriptionOrigin.GuardianPurchase, isAdminInitiated: false, CancellationToken.None);

        Assert.Equal(PurchaseFromPlanOutcome.Purchased, result.Outcome);
    }

    [Fact]
    public async Task An_unrelated_user_cannot_purchase_on_behalf_of_a_student_they_do_not_own()
    {
        await using var db = _fixture.CreateContext();
        var (countryId, courseId, levelId, studentId, _) = await SeedCatalogAndStudentAsync(db);
        var strangerUserId = await CreateUserAsync(db, "stranger");
        var service = CreateService(db, SystemClock.Instance.GetCurrentInstant());
        var plan = await service.CreatePricingPlanAsync(countryId, courseId, levelId, null, SessionType.Group,
            8, 480, new Money(60m, "JOD"), 60, new LocalDate(2026, 1, 1), createdByUserId: NextId(), CancellationToken.None);

        var result = await service.PurchaseFromPlanAsync(studentId, plan.PricingPlanId, strangerUserId,
            SubscriptionOrigin.SelfPurchase, isAdminInitiated: false, CancellationToken.None);

        Assert.Equal(PurchaseFromPlanOutcome.Unauthorized, result.Outcome);
        Assert.False(await db.Subscriptions.AnyAsync(s => s.StudentId == studentId));
    }

    /// <summary>Owner decision 2026-08-30 rule 1: "Until a placement result
    /// exists, the student must not purchase a package."</summary>
    [Fact]
    public async Task A_student_with_no_assigned_level_cannot_purchase_anything()
    {
        await using var db = _fixture.CreateContext();
        var (countryId, courseId, levelId, studentId, studentUserId) = await SeedCatalogAndStudentAsync(db, assignLevel: false);
        var service = CreateService(db, SystemClock.Instance.GetCurrentInstant());
        var plan = await service.CreatePricingPlanAsync(countryId, courseId, levelId, null, SessionType.Group,
            8, 480, new Money(60m, "JOD"), 60, new LocalDate(2026, 1, 1), createdByUserId: NextId(), CancellationToken.None);

        var result = await service.PurchaseFromPlanAsync(studentId, plan.PricingPlanId, studentUserId,
            SubscriptionOrigin.SelfPurchase, isAdminInitiated: false, CancellationToken.None);

        Assert.Equal(PurchaseFromPlanOutcome.StudentHasNoAssignedLevel, result.Outcome);
    }

    /// <summary>Rule 4: "A student may purchase only a package whose level
    /// matches their current assigned level" — even when the caller is an
    /// admin (D-94: manual payments use the same restrictions).</summary>
    [Fact]
    public async Task A_package_for_a_different_level_is_refused_even_for_an_admin_initiated_purchase()
    {
        await using var db = _fixture.CreateContext();
        var (countryId, courseId, levelId, studentId, _) = await SeedCatalogAndStudentAsync(db);
        var otherLevelId = (int)NextId();
        db.Levels.Add(new Level(otherLevelId, "L" + otherLevelId, "مستوى", "Other Level", otherLevelId));
        await db.SaveChangesAsync();

        var service = CreateService(db, SystemClock.Instance.GetCurrentInstant());
        var plan = await service.CreatePricingPlanAsync(countryId, courseId, otherLevelId, null, SessionType.Group,
            8, 480, new Money(60m, "JOD"), 60, new LocalDate(2026, 1, 1), createdByUserId: NextId(), CancellationToken.None);

        var result = await service.PurchaseFromPlanAsync(studentId, plan.PricingPlanId, NextId(),
            SubscriptionOrigin.GuardianPurchase, isAdminInitiated: true, CancellationToken.None);

        Assert.Equal(PurchaseFromPlanOutcome.LevelMismatch, result.Outcome);
        Assert.False(await db.Subscriptions.AnyAsync(s => s.StudentId == studentId));
    }

    /// <summary>A plan with no specific level ("applies to every level") can
    /// never be sold under the new rule — every published package is tied to
    /// exactly one level.</summary>
    [Fact]
    public async Task A_plan_with_no_specific_level_cannot_be_purchased()
    {
        await using var db = _fixture.CreateContext();
        var (countryId, courseId, _, studentId, studentUserId) = await SeedCatalogAndStudentAsync(db);
        var service = CreateService(db, SystemClock.Instance.GetCurrentInstant());
        var plan = await service.CreatePricingPlanAsync(countryId, courseId, null, null, SessionType.Group,
            8, 480, new Money(60m, "JOD"), 60, new LocalDate(2026, 1, 1), createdByUserId: NextId(), CancellationToken.None);

        var result = await service.PurchaseFromPlanAsync(studentId, plan.PricingPlanId, studentUserId,
            SubscriptionOrigin.SelfPurchase, isAdminInitiated: false, CancellationToken.None);

        Assert.Equal(PurchaseFromPlanOutcome.PlanNotPublishedForAnyLevel, result.Outcome);
    }

    [Fact]
    public async Task Granting_a_free_subscription_activates_immediately_and_posts_the_admin_grant_ledger_entry()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var (countryId, courseId, levelId, studentId, _) = await SeedCatalogAndStudentAsync(db);
        var service = CreateService(db, now);

        var result = await service.GrantAdminSubscriptionAsync(studentId, countryId, courseId, levelId, SessionType.Group,
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
        var (countryId, courseId, levelId, studentId, _) = await SeedCatalogAndStudentAsync(db);
        var service = CreateService(db, SystemClock.Instance.GetCurrentInstant());

        // D-13: an admin-created subscription requires a reason — enforced by
        // Subscription's own constructor, exercised here through the service.
        await Assert.ThrowsAsync<ArgumentException>(() => service.GrantAdminSubscriptionAsync(
            studentId, countryId, courseId, levelId, SessionType.Group, 5, 300, 30, NextId(), reason: "", CancellationToken.None));
    }

    [Fact]
    public async Task Entitlement_balance_sums_every_ledger_entry_for_the_subscription()
    {
        await using var db = _fixture.CreateContext();
        var (countryId, courseId, levelId, studentId, _) = await SeedCatalogAndStudentAsync(db);
        var subscription = new MVTeaches.Domain.Subscriptions.Subscription(studentId, countryId, courseId, levelId,
            SessionType.Group, new Money(0m, "JOD"), null, 5, 300, new LocalDate(2026, 1, 1), 30, SubscriptionOrigin.AdminCreated,
            NextId(), "seed");
        subscription.Activate();
        db.Subscriptions.Add(subscription);
        await db.SaveChangesAsync();

        var now = SystemClock.Instance.GetCurrentInstant();
        db.EntitlementLedgerEntries.Add(EntitlementLedgerEntry.ForAdminGrant(studentId, subscription.Id, courseId, levelId, SessionType.Group, 300, NextId(), "seed", now));
        db.EntitlementLedgerEntries.Add(EntitlementLedgerEntry.ForConsumption(studentId, subscription.Id, courseId, levelId, SessionType.Group, 60, NextId(), NextId(), now));
        await db.SaveChangesAsync();

        var balance = await CreateBalanceQuery(db).GetSubscriptionBalanceAsync(subscription.Id, CancellationToken.None);
        Assert.Equal(240, balance);
    }
}
