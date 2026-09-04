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
        // Course.Id is database-generated, so the real id has to be read back
        // after SaveChanges rather than assumed from the NextId() seed. That
        // distinction did not matter while nothing had a foreign key to
        // courses; StudentLevel.CourseId (2026-09-04) does, and a fabricated
        // id now fails loudly instead of quietly pointing at nothing.
        var courseSeed = NextId();
        var levelId = (int)NextId();
        var studentUserId = await CreateUserAsync(db, "student");
        var countryId = await SeedCountryAsync(db);
        var course = new Course("C" + courseSeed, "دورة", "Course");
        db.Courses.Add(course);
        db.Levels.Add(new Level(levelId, "L" + levelId, "مستوى", "Level", levelId));
        var student = new Student(countryId, "Student", new LocalDate(2010, 1, 1), studentUserId);
        db.Students.Add(student);
        await db.SaveChangesAsync();
        var courseId = course.Id;

        if (assignLevel)
        {
            db.StudentLevels.Add(new StudentLevel(student.Id, courseId, levelId, studentUserId, AssignedByRole.Admin,
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
        new SubscriptionService(db, new FakeClock(now), CreateBalanceQuery(db));

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

    // =================================================================
    // Duplicate-purchase guard (owner decision 2026-09-04, Review Required
    // — Payment). Staging showed one student holding FOUR separately-paid
    // subscriptions for the identical plan — 200 JOD for a 50 JOD package —
    // bought partly from their own login and partly from their guardian's.
    // Nothing in the purchase path stopped it. These cover the rule the
    // owner set: a Draft awaiting payment, or a live package with hours
    // still on it, blocks buying the same plan again; a used-up or expired
    // one does not.
    // =================================================================

    /// <summary>Activates a subscription and posts the entitlement it bought,
    /// the same shape IPaymentService.ConfirmAsync produces once a package is
    /// paid in full — without driving the whole payment flow for tests whose
    /// subject is the purchase guard, not the payment.</summary>
    private static async Task ActivateWithEntitlementAsync(MvTeachesDbContext db, long subscriptionId,
        long studentId, long courseId, int levelId, int purchasedMinutes, int consumedMinutes)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        var subscription = await db.Subscriptions.FirstAsync(s => s.Id == subscriptionId);
        subscription.Activate();
        db.EntitlementLedgerEntries.Add(EntitlementLedgerEntry.ForAdminGrant(studentId, subscriptionId,
            courseId, levelId, subscription.SessionType, purchasedMinutes, NextId(), "seed", now));
        if (consumedMinutes > 0)
        {
            db.EntitlementLedgerEntries.Add(EntitlementLedgerEntry.ForConsumption(studentId, subscriptionId,
                courseId, levelId, subscription.SessionType, consumedMinutes, NextId(), NextId(), now));
        }

        await db.SaveChangesAsync();
    }

    private async Task<(ISubscriptionService Service, long PlanId, long StudentId, long StudentUserId, long CourseId, int LevelId)>
        SeedPurchasablePlanAsync(MvTeachesDbContext db)
    {
        var (countryId, courseId, levelId, studentId, studentUserId) = await SeedCatalogAndStudentAsync(db);
        var service = CreateService(db, SystemClock.Instance.GetCurrentInstant());
        var plan = await service.CreatePricingPlanAsync(countryId, courseId, levelId, null, SessionType.Group,
            10, 600, new Money(50m, "JOD"), 90, new LocalDate(2026, 1, 1), createdByUserId: NextId(), CancellationToken.None);
        return (service, plan.PricingPlanId, studentId, studentUserId, courseId, levelId);
    }

    /// <summary>The plain double-click: pressing "buy" twice must leave one
    /// subscription, not two, and the second press must hand back the FIRST
    /// one's id so the payer is sent to finish paying it.</summary>
    [Fact]
    public async Task Purchasing_the_same_plan_twice_creates_only_one_subscription()
    {
        await using var db = _fixture.CreateContext();
        var (service, planId, studentId, studentUserId, _, _) = await SeedPurchasablePlanAsync(db);

        var first = await service.PurchaseFromPlanAsync(studentId, planId, studentUserId,
            SubscriptionOrigin.SelfPurchase, isAdminInitiated: false, CancellationToken.None);
        var second = await service.PurchaseFromPlanAsync(studentId, planId, studentUserId,
            SubscriptionOrigin.SelfPurchase, isAdminInitiated: false, CancellationToken.None);

        Assert.Equal(PurchaseFromPlanOutcome.Purchased, first.Outcome);
        Assert.Equal(PurchaseFromPlanOutcome.DraftAlreadyAwaitingPayment, second.Outcome);
        // The refusal names the request already waiting, so the caller can
        // point at it rather than leaving the payer to press again.
        Assert.Equal(first.SubscriptionId, second.SubscriptionId);
        Assert.Equal(50m, second.Price!.Amount);
        Assert.Equal(1, await db.Subscriptions.CountAsync(s => s.StudentId == studentId && s.PricingPlanId == planId));
    }

    /// <summary>The student's own login is blocked by their own live package.</summary>
    [Fact]
    public async Task A_student_cannot_buy_a_package_they_already_hold_with_hours_left()
    {
        await using var db = _fixture.CreateContext();
        var (service, planId, studentId, studentUserId, courseId, levelId) = await SeedPurchasablePlanAsync(db);

        var first = await service.PurchaseFromPlanAsync(studentId, planId, studentUserId,
            SubscriptionOrigin.SelfPurchase, isAdminInitiated: false, CancellationToken.None);
        await ActivateWithEntitlementAsync(db, first.SubscriptionId!.Value, studentId, courseId, levelId,
            purchasedMinutes: 600, consumedMinutes: 60); // 540 minutes still unused

        var second = await service.PurchaseFromPlanAsync(studentId, planId, studentUserId,
            SubscriptionOrigin.SelfPurchase, isAdminInitiated: false, CancellationToken.None);

        Assert.Equal(PurchaseFromPlanOutcome.ActivePackageStillHasBalance, second.Outcome);
        Assert.Equal(first.SubscriptionId, second.SubscriptionId);
        Assert.Equal(1, await db.Subscriptions.CountAsync(s => s.StudentId == studentId && s.PricingPlanId == planId));
    }

    /// <summary>The guard is keyed on the STUDENT, so the acting account does
    /// not change the answer — a guardian is refused by their own child's
    /// existing package exactly as the student would be.
    /// <para>This used to open with the student buying from their own login,
    /// which was the staging incident's literal shape. Owner decision
    /// 2026-09-04 (guardian responsibility) now blocks that first step outright
    /// for any student who has a guardian — see
    /// A_student_with_a_guardian_cannot_buy_a_package_from_their_own_login —
    /// so the purchase here comes from the guardian, which is the only way this
    /// student can hold a package at all.</para></summary>
    [Fact]
    public async Task A_guardian_cannot_buy_a_package_their_child_already_holds_with_hours_left()
    {
        await using var db = _fixture.CreateContext();
        var (service, planId, studentId, studentUserId, courseId, levelId) = await SeedPurchasablePlanAsync(db);

        var guardianUserId = await CreateUserAsync(db, "guardian");
        var guardian = new Guardian(guardianUserId, "Guardian");
        db.Guardians.Add(guardian);
        await db.SaveChangesAsync();
        db.Guardianships.Add(new Guardianship(guardian.Id, studentId, GuardianRelationship.Parent, isPrimary: true, guardianUserId));
        await db.SaveChangesAsync();

        // The guardian buys it for their child and it is paid and activated.
        var bought = await service.PurchaseFromPlanAsync(studentId, planId, guardianUserId,
            SubscriptionOrigin.GuardianPurchase, isAdminInitiated: false, CancellationToken.None);
        Assert.Equal(PurchaseFromPlanOutcome.Purchased, bought.Outcome);
        await ActivateWithEntitlementAsync(db, bought.SubscriptionId!.Value, studentId, courseId, levelId,
            purchasedMinutes: 600, consumedMinutes: 0);

        // The guardian tries to buy the same thing a second time.
        var guardianAttempt = await service.PurchaseFromPlanAsync(studentId, planId, guardianUserId,
            SubscriptionOrigin.GuardianPurchase, isAdminInitiated: false, CancellationToken.None);

        Assert.Equal(PurchaseFromPlanOutcome.ActivePackageStillHasBalance, guardianAttempt.Outcome);
        Assert.Equal(1, await db.Subscriptions.CountAsync(s => s.StudentId == studentId && s.PricingPlanId == planId));
    }

    /// <summary>Once the hours are gone the package has done its job — buying
    /// the same one again is exactly what should happen next.</summary>
    [Fact]
    public async Task A_used_up_active_package_does_not_block_buying_the_same_plan_again()
    {
        await using var db = _fixture.CreateContext();
        var (service, planId, studentId, studentUserId, courseId, levelId) = await SeedPurchasablePlanAsync(db);

        var first = await service.PurchaseFromPlanAsync(studentId, planId, studentUserId,
            SubscriptionOrigin.SelfPurchase, isAdminInitiated: false, CancellationToken.None);
        await ActivateWithEntitlementAsync(db, first.SubscriptionId!.Value, studentId, courseId, levelId,
            purchasedMinutes: 600, consumedMinutes: 600); // fully spent

        var second = await service.PurchaseFromPlanAsync(studentId, planId, studentUserId,
            SubscriptionOrigin.SelfPurchase, isAdminInitiated: false, CancellationToken.None);

        Assert.Equal(PurchaseFromPlanOutcome.Purchased, second.Outcome);
        Assert.NotEqual(first.SubscriptionId, second.SubscriptionId);
        Assert.Equal(2, await db.Subscriptions.CountAsync(s => s.StudentId == studentId && s.PricingPlanId == planId));
    }

    /// <summary>An Expired package never blocks, whatever is left on it —
    /// the owner's rule names expiry as its own release condition, separate
    /// from the balance.</summary>
    [Fact]
    public async Task An_expired_package_does_not_block_buying_the_same_plan_again()
    {
        await using var db = _fixture.CreateContext();
        var (service, planId, studentId, studentUserId, courseId, levelId) = await SeedPurchasablePlanAsync(db);

        var first = await service.PurchaseFromPlanAsync(studentId, planId, studentUserId,
            SubscriptionOrigin.SelfPurchase, isAdminInitiated: false, CancellationToken.None);
        await ActivateWithEntitlementAsync(db, first.SubscriptionId!.Value, studentId, courseId, levelId,
            purchasedMinutes: 600, consumedMinutes: 0); // deliberately still has minutes on it

        var expired = await db.Subscriptions.FirstAsync(s => s.Id == first.SubscriptionId!.Value);
        expired.MarkExpired();
        await db.SaveChangesAsync();

        var second = await service.PurchaseFromPlanAsync(studentId, planId, studentUserId,
            SubscriptionOrigin.SelfPurchase, isAdminInitiated: false, CancellationToken.None);

        Assert.Equal(PurchaseFromPlanOutcome.Purchased, second.Outcome);
        Assert.Equal(2, await db.Subscriptions.CountAsync(s => s.StudentId == studentId && s.PricingPlanId == planId));
    }

    /// <summary>The one interaction that mattered most to get right: the
    /// shortfall/top-up path (pay 40 of 50, then the remaining 10) must still
    /// activate the package exactly as before — the guard blocks a second
    /// SUBSCRIPTION, never a second PAYMENT against the same one. Runs the
    /// real payment service end to end, and checks the guard is live
    /// throughout rather than assuming it.</summary>
    [Fact]
    public async Task Paying_a_shortfall_then_the_remainder_still_activates_while_the_duplicate_guard_holds()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var (service, planId, studentId, studentUserId, _, _) = await SeedPurchasablePlanAsync(db);

        var purchase = await service.PurchaseFromPlanAsync(studentId, planId, studentUserId,
            SubscriptionOrigin.SelfPurchase, isAdminInitiated: false, CancellationToken.None);
        var subscriptionId = purchase.SubscriptionId!.Value;

        var payments = new MVTeaches.Infrastructure.Payments.PaymentService(db, new FakeClock(now));

        // 40 of 50 — short, so nothing activates yet.
        var first = await payments.RecordManualPaymentAsync(
            new MVTeaches.Application.Payments.RecordPaymentRequest(studentId, subscriptionId, PayerUserId: null,
                new Money(50m, "JOD"), MVTeaches.Domain.Payments.PaymentMethod.BankTransfer, null),
            CancellationToken.None);
        await payments.ConfirmAsync(first.PaymentId, confirmedByUserId: NextId(), CancellationToken.None,
            actuallyReceivedAmount: new Money(40m, "JOD"));

        Assert.Equal(SubscriptionStatus.Draft, (await db.Subscriptions.FirstAsync(s => s.Id == subscriptionId)).Status);

        // Still short and still Draft — and still refused as a duplicate,
        // pointed at the request already open rather than a new one.
        var whileShort = await service.PurchaseFromPlanAsync(studentId, planId, studentUserId,
            SubscriptionOrigin.SelfPurchase, isAdminInitiated: false, CancellationToken.None);
        Assert.Equal(PurchaseFromPlanOutcome.DraftAlreadyAwaitingPayment, whileShort.Outcome);
        Assert.Equal(subscriptionId, whileShort.SubscriptionId);

        // The remaining 10 arrives on its own payment row against the SAME
        // subscription — the guard never touched this path.
        var second = await payments.RecordManualPaymentAsync(
            new MVTeaches.Application.Payments.RecordPaymentRequest(studentId, subscriptionId, PayerUserId: null,
                new Money(10m, "JOD"), MVTeaches.Domain.Payments.PaymentMethod.BankTransfer, null),
            CancellationToken.None);
        var confirmed = await payments.ConfirmAsync(second.PaymentId, confirmedByUserId: NextId(), CancellationToken.None);

        Assert.Equal(MVTeaches.Application.Payments.ConfirmPaymentOutcome.Confirmed, confirmed.Outcome);
        var activated = await db.Subscriptions.FirstAsync(s => s.Id == subscriptionId);
        Assert.Equal(SubscriptionStatus.Active, activated.Status);

        // Exactly one subscription, two payments, one Purchase ledger entry.
        Assert.Equal(1, await db.Subscriptions.CountAsync(s => s.StudentId == studentId && s.PricingPlanId == planId));
        Assert.Equal(2, await db.Payments.CountAsync(p => p.SubscriptionId == subscriptionId));
        var entries = await db.EntitlementLedgerEntries.Where(l => l.SubscriptionId == subscriptionId).ToListAsync();
        Assert.Single(entries);
        Assert.Equal(activated.MinutesTotal, entries[0].DeltaMinutes);

        // And now that it is Active with its full balance, the same package
        // is refused for the reason that actually applies.
        var afterActivation = await service.PurchaseFromPlanAsync(studentId, planId, studentUserId,
            SubscriptionOrigin.SelfPurchase, isAdminInitiated: false, CancellationToken.None);
        Assert.Equal(PurchaseFromPlanOutcome.ActivePackageStillHasBalance, afterActivation.Outcome);
    }

    // =================================================================
    // Owner decision 2026-09-04: a student who has a guardian linked to them
    // does not buy for themself — the guardian or an admin buys for them.
    // The criterion is the LINK, never an age: the owner explicitly ruled out
    // inventing an age threshold. Everything the rule must NOT break (the
    // linked guardian, the admin, the student with no guardian) is asserted
    // here alongside what it must block.
    // =================================================================

    /// <summary>Links a fresh guardian to a student and returns that guardian's
    /// user id — the acting account a guardian purchase comes from.</summary>
    private static async Task<long> LinkAGuardianAsync(MvTeachesDbContext db, long studentId)
    {
        var guardianUserId = await CreateUserAsync(db, "guardian");
        var guardian = new Guardian(guardianUserId, "Guardian");
        db.Guardians.Add(guardian);
        await db.SaveChangesAsync();
        db.Guardianships.Add(new Guardianship(guardian.Id, studentId, GuardianRelationship.Parent, isPrimary: true, guardianUserId));
        await db.SaveChangesAsync();
        return guardianUserId;
    }

    /// <summary>The rule itself, and its exact boundary in one test: the SAME
    /// student, the SAME login, the SAME plan — refused once a guardian is
    /// linked, allowed before that. Nothing but the link changes between the
    /// two attempts, which is what makes the link the criterion rather than
    /// anything about the student.</summary>
    [Fact]
    public async Task A_student_with_a_guardian_cannot_buy_a_package_from_their_own_login()
    {
        await using var db = _fixture.CreateContext();
        var (service, planId, studentId, studentUserId, _, _) = await SeedPurchasablePlanAsync(db);

        // No guardian yet — an ordinary self-purchase, which must keep working.
        var beforeAnyGuardian = await service.PurchaseFromPlanAsync(studentId, planId, studentUserId,
            SubscriptionOrigin.SelfPurchase, isAdminInitiated: false, CancellationToken.None);
        Assert.Equal(PurchaseFromPlanOutcome.Purchased, beforeAnyGuardian.Outcome);

        // Clear the way so the refusal below can only be the guardian rule and
        // never the duplicate guard shadowing it.
        var draft = await db.Subscriptions.FirstAsync(s => s.Id == beforeAnyGuardian.SubscriptionId!.Value);
        draft.Cancel();
        await db.SaveChangesAsync();

        await LinkAGuardianAsync(db, studentId);

        var afterLinking = await service.PurchaseFromPlanAsync(studentId, planId, studentUserId,
            SubscriptionOrigin.SelfPurchase, isAdminInitiated: false, CancellationToken.None);

        Assert.Equal(PurchaseFromPlanOutcome.StudentIsUnderGuardianCare, afterLinking.Outcome);
        // Refused means refused: no second row was written.
        Assert.Equal(1, await db.Subscriptions.CountAsync(s => s.StudentId == studentId && s.PricingPlanId == planId));
    }

    /// <summary>The guardian half of the same rule — the person the purchase
    /// was handed TO must still be able to make it.</summary>
    [Fact]
    public async Task The_linked_guardian_can_still_buy_the_package_the_student_may_not()
    {
        await using var db = _fixture.CreateContext();
        var (service, planId, studentId, studentUserId, _, _) = await SeedPurchasablePlanAsync(db);
        var guardianUserId = await LinkAGuardianAsync(db, studentId);

        var studentAttempt = await service.PurchaseFromPlanAsync(studentId, planId, studentUserId,
            SubscriptionOrigin.SelfPurchase, isAdminInitiated: false, CancellationToken.None);
        Assert.Equal(PurchaseFromPlanOutcome.StudentIsUnderGuardianCare, studentAttempt.Outcome);

        var guardianPurchase = await service.PurchaseFromPlanAsync(studentId, planId, guardianUserId,
            SubscriptionOrigin.GuardianPurchase, isAdminInitiated: false, CancellationToken.None);

        Assert.Equal(PurchaseFromPlanOutcome.Purchased, guardianPurchase.Outcome);
        var subscription = await db.Subscriptions.FirstAsync(s => s.Id == guardianPurchase.SubscriptionId);
        Assert.Equal(SubscriptionOrigin.GuardianPurchase, subscription.Origin);
    }

    /// <summary>"Guardian" is not a role that grants access to children in
    /// general — a real guardian, of a real but DIFFERENT student, is as
    /// unauthorized here as a stranger. Refused as Unauthorized rather than as
    /// the guardian-care rule, because this is a failed identity check.</summary>
    [Fact]
    public async Task A_guardian_of_a_different_student_cannot_buy_for_this_one()
    {
        await using var db = _fixture.CreateContext();
        var (service, planId, studentId, _, _, _) = await SeedPurchasablePlanAsync(db);
        await LinkAGuardianAsync(db, studentId); // this student's own guardian

        // A guardian in good standing — of somebody else's child.
        var (_, _, _, otherStudentId, _) = await SeedCatalogAndStudentAsync(db);
        var outsiderGuardianUserId = await LinkAGuardianAsync(db, otherStudentId);

        var result = await service.PurchaseFromPlanAsync(studentId, planId, outsiderGuardianUserId,
            SubscriptionOrigin.GuardianPurchase, isAdminInitiated: false, CancellationToken.None);

        Assert.Equal(PurchaseFromPlanOutcome.Unauthorized, result.Outcome);
        Assert.Equal(0, await db.Subscriptions.CountAsync(s => s.StudentId == studentId));
    }

    /// <summary>The admin screen records payments for families who pay in
    /// person, so it must keep working for exactly the students this rule
    /// covers — otherwise the rule strands the child it was meant to
    /// protect.</summary>
    [Fact]
    public async Task An_admin_can_still_buy_for_a_student_who_has_a_guardian()
    {
        await using var db = _fixture.CreateContext();
        var (service, planId, studentId, _, _, _) = await SeedPurchasablePlanAsync(db);
        await LinkAGuardianAsync(db, studentId);

        var result = await service.PurchaseFromPlanAsync(studentId, planId, actingUserId: NextId(),
            SubscriptionOrigin.GuardianPurchase, isAdminInitiated: true, CancellationToken.None); // the origin /Admin/AssistedRegistration itself records

        Assert.Equal(PurchaseFromPlanOutcome.Purchased, result.Outcome);
        Assert.Equal(1, await db.Subscriptions.CountAsync(s => s.StudentId == studentId && s.PricingPlanId == planId));
    }

    /// <summary>The adult learner: nobody is registered as responsible for
    /// them, so nothing changes — they buy, pay, and are then held by the
    /// duplicate guard alone, not by this one.</summary>
    [Fact]
    public async Task A_student_with_no_guardian_still_buys_for_themself()
    {
        await using var db = _fixture.CreateContext();
        var (service, planId, studentId, studentUserId, courseId, levelId) = await SeedPurchasablePlanAsync(db);

        var result = await service.PurchaseFromPlanAsync(studentId, planId, studentUserId,
            SubscriptionOrigin.SelfPurchase, isAdminInitiated: false, CancellationToken.None);
        Assert.Equal(PurchaseFromPlanOutcome.Purchased, result.Outcome);

        await ActivateWithEntitlementAsync(db, result.SubscriptionId!.Value, studentId, courseId, levelId,
            purchasedMinutes: 600, consumedMinutes: 0);

        // Blocked now — but by the duplicate rule, which is the only one that
        // should ever apply to a student with no guardian.
        var again = await service.PurchaseFromPlanAsync(studentId, planId, studentUserId,
            SubscriptionOrigin.SelfPurchase, isAdminInitiated: false, CancellationToken.None);
        Assert.Equal(PurchaseFromPlanOutcome.ActivePackageStillHasBalance, again.Outcome);
    }
}
