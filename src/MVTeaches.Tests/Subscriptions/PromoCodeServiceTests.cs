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
/// Owner decision 2026-09-05: discount codes for packages.
///
/// <para>The rule these tests exist to hold down is that <b>the browser never
/// prices anything</b>. It sends six characters; every figure that reaches a
/// subscription is computed server-side from the plan's own amount and the
/// code's own stored percentage. The tests below therefore go through the
/// SERVICES, passing only what a request could carry, and assert on what was
/// written — not on what a page displayed.</para>
///
/// <para>The other rule is the snapshot: what a family paid must not change
/// when an admin edits the code afterwards.</para>
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class PromoCodeServiceTests
{
    private readonly TestDatabaseFixture _fixture;
    private static long _idSeed = 770_000_000;

    public PromoCodeServiceTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static long NextId() => Interlocked.Increment(ref _idSeed);

    private static readonly Instant Now = Instant.FromUtc(2026, 9, 5, 9, 0);

    private static IPromoCodeService CreatePromoCodes(MvTeachesDbContext db, Instant? now = null) =>
        new PromoCodeService(db, new FakeClock(now ?? Now));

    private static ISubscriptionService CreateSubscriptions(MvTeachesDbContext db, Instant? now = null) =>
        new SubscriptionService(db, new FakeClock(now ?? Now), new EntitlementBalanceQuery(db),
            CreatePromoCodes(db, now));

    private static string TwoLetterCode(long seed)
    {
        var n = (int)(seed % 676);
        return string.Concat((char)('A' + n / 26), (char)('A' + n % 26));
    }

    private static async Task<int> SeedCountryAsync(MvTeachesDbContext db)
    {
        var existing = await db.Countries.OrderBy(c => c.Id).Select(c => (int?)c.Id).FirstOrDefaultAsync();
        if (existing is not null)
        {
            return existing.Value;
        }

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

        throw new InvalidOperationException("Could not seed a country.");
    }

    private static async Task<int> SeedLevelAsync(MvTeachesDbContext db)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var levelId = (int)NextId();
            db.Levels.Add(new Level(levelId, $"PC{levelId}", "مستوى", "Level", levelId));
            try
            {
                await db.SaveChangesAsync();
                return levelId;
            }
            catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
            {
                db.ChangeTracker.Clear();
            }
        }

        throw new InvalidOperationException("Could not seed a level.");
    }

    private sealed record Fixture(long StudentId, long ActingUserId, long CourseId, int LevelId, long PlanId, decimal PlanPrice);

    /// <summary>A student placed in one course, and a 50 JOD package published
    /// for exactly that (course, level).</summary>
    private static async Task<Fixture> SeedPurchasableAsync(MvTeachesDbContext db, decimal price = 50m)
    {
        var countryId = await SeedCountryAsync(db);
        var course = new Course($"PROMO-{NextId()}", "دورة", "Promo Course");
        db.Courses.Add(course);
        await db.SaveChangesAsync();
        var levelId = await SeedLevelAsync(db);

        var user = new MVTeaches.Infrastructure.Identity.ApplicationUser
        {
            UserName = $"promo-{Guid.NewGuid():N}",
            Email = $"promo-{Guid.NewGuid():N}@test.mvteaches.local",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var student = new Student(countryId, "Promo Student", new LocalDate(2010, 1, 1), user.Id);
        db.Students.Add(student);
        await db.SaveChangesAsync();

        db.StudentLevels.Add(new StudentLevel(student.Id, course.Id, levelId, user.Id, AssignedByRole.Admin,
            LevelAssignmentSource.AdminOverride, null, "seed", Now));
        await db.SaveChangesAsync();

        var plan = await CreateSubscriptions(db).CreatePricingPlanAsync(countryId, course.Id, levelId, null,
            SessionType.Group, 10, 600, new Money(price, "JOD"), 30, Now.InUtc().Date, user.Id, CancellationToken.None);

        return new Fixture(student.Id, user.Id, course.Id, levelId, plan.PricingPlanId, price);
    }

    private static async Task<long> CreateCodeAsync(MvTeachesDbContext db, int percent,
        bool isActive = true, LocalDate? startsOn = null, LocalDate? endsOn = null,
        int? maxTotal = null, int? maxPerStudent = null, IReadOnlyList<long>? plans = null,
        Instant? now = null)
    {
        var service = CreatePromoCodes(db, now);
        var code = await service.GenerateUnusedCodeAsync(CancellationToken.None);
        var result = await service.CreateAsync(code, percent, isActive, startsOn, endsOn, maxTotal, maxPerStudent,
            appliesToAllPlans: plans is null, plans ?? Array.Empty<long>(), 1L, CancellationToken.None);
        Assert.Equal(CreatePromoCodeOutcome.Created, result.Outcome);
        return result.PromoCodeId!.Value;
    }

    // ---------------------------------------------------------------- creation

    [Fact]
    public async Task A_generated_code_is_six_characters_of_capitals_and_digits()
    {
        await using var db = _fixture.CreateContext();
        var code = await CreatePromoCodes(db).GenerateUnusedCodeAsync(CancellationToken.None);

        Assert.Equal(6, code.Length);
        Assert.All(code, c => Assert.Contains(c, PromoCode.Alphabet));
        Assert.Equal(code.ToUpperInvariant(), code);
    }

    /// <summary>Stored uppercase, so "a7k2p9" and "A7K2P9" can never both
    /// exist and a family typing either one is understood.</summary>
    [Fact]
    public async Task A_code_is_stored_uppercase()
    {
        await using var db = _fixture.CreateContext();
        var service = CreatePromoCodes(db);
        var generated = await service.GenerateUnusedCodeAsync(CancellationToken.None);

        var result = await service.CreateAsync(generated.ToLowerInvariant(), 20, true, null, null, null, null,
            true, Array.Empty<long>(), 1L, CancellationToken.None);

        Assert.Equal(CreatePromoCodeOutcome.Created, result.Outcome);
        var stored = await db.PromoCodes.SingleAsync(p => p.Id == result.PromoCodeId);
        Assert.Equal(generated.ToUpperInvariant(), stored.Code);
    }

    [Fact]
    public async Task A_duplicate_code_is_refused()
    {
        await using var db = _fixture.CreateContext();
        var service = CreatePromoCodes(db);
        var code = await service.GenerateUnusedCodeAsync(CancellationToken.None);

        var first = await service.CreateAsync(code, 10, true, null, null, null, null, true,
            Array.Empty<long>(), 1L, CancellationToken.None);
        Assert.Equal(CreatePromoCodeOutcome.Created, first.Outcome);

        // Lowercase, to prove the comparison is on the normalised value rather
        // than on the exact characters the caller sent.
        var second = await service.CreateAsync(code.ToLowerInvariant(), 10, true, null, null, null, null, true,
            Array.Empty<long>(), 1L, CancellationToken.None);

        Assert.Equal(CreatePromoCodeOutcome.DuplicateCode, second.Outcome);
        Assert.Equal(1, await db.PromoCodes.CountAsync(p => p.Code == code));
    }

    [Theory]
    [InlineData("ABC12")]      // five
    [InlineData("ABC1234")]    // seven
    [InlineData("ABC 12")]     // a space
    [InlineData("ABC-12")]     // punctuation
    [InlineData("ABCأ12")]     // Arabic
    [InlineData("")]
    public async Task A_malformed_code_is_refused(string code)
    {
        await using var db = _fixture.CreateContext();

        var result = await CreatePromoCodes(db).CreateAsync(code, 10, true, null, null, null, null, true,
            Array.Empty<long>(), 1L, CancellationToken.None);

        Assert.Equal(CreatePromoCodeOutcome.MalformedCode, result.Outcome);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(101)]
    public async Task A_discount_outside_one_to_a_hundred_is_refused(int percent)
    {
        await using var db = _fixture.CreateContext();
        var service = CreatePromoCodes(db);
        var code = await service.GenerateUnusedCodeAsync(CancellationToken.None);

        var result = await service.CreateAsync(code, percent, true, null, null, null, null, true,
            Array.Empty<long>(), 1L, CancellationToken.None);

        Assert.Equal(CreatePromoCodeOutcome.InvalidDiscountPercent, result.Outcome);
        Assert.Equal(0, await db.PromoCodes.CountAsync(p => p.Code == code));
    }

    /// <summary>100 is legitimate and deliberate: a package the centre is
    /// giving away.</summary>
    [Fact]
    public async Task A_hundred_percent_discount_is_accepted()
    {
        await using var db = _fixture.CreateContext();
        var id = await CreateCodeAsync(db, 100);

        var stored = await db.PromoCodes.SingleAsync(p => p.Id == id);
        Assert.Equal(100, stored.DiscountPercent);
    }

    [Fact]
    public async Task A_window_that_ends_before_it_starts_is_refused()
    {
        await using var db = _fixture.CreateContext();
        var service = CreatePromoCodes(db);
        var code = await service.GenerateUnusedCodeAsync(CancellationToken.None);

        var result = await service.CreateAsync(code, 10, true,
            new LocalDate(2026, 10, 1), new LocalDate(2026, 9, 1), null, null, true,
            Array.Empty<long>(), 1L, CancellationToken.None);

        Assert.Equal(CreatePromoCodeOutcome.InvalidWindow, result.Outcome);
    }

    [Fact]
    public async Task Choosing_specific_packages_without_naming_one_is_refused()
    {
        await using var db = _fixture.CreateContext();
        var service = CreatePromoCodes(db);
        var code = await service.GenerateUnusedCodeAsync(CancellationToken.None);

        var result = await service.CreateAsync(code, 10, true, null, null, null, null,
            appliesToAllPlans: false, Array.Empty<long>(), 1L, CancellationToken.None);

        Assert.Equal(CreatePromoCodeOutcome.NoPlansChosen, result.Outcome);
    }

    // ------------------------------------------------------------------- scope

    [Fact]
    public async Task A_code_for_every_package_applies_to_any_package()
    {
        await using var db = _fixture.CreateContext();
        var fx = await SeedPurchasableAsync(db);
        var id = await CreateCodeAsync(db, 25);
        var code = (await db.PromoCodes.SingleAsync(p => p.Id == id)).Code;

        var applied = await CreatePromoCodes(db).ApplyAsync(code, fx.PlanId, fx.StudentId, CancellationToken.None);

        Assert.True(applied.Accepted);
        Assert.Equal(12.500m, applied.Quote!.DiscountAmount);
        Assert.Equal(37.500m, applied.Quote.FinalPrice);
    }

    [Fact]
    public async Task A_code_limited_to_a_package_applies_to_that_package()
    {
        await using var db = _fixture.CreateContext();
        var fx = await SeedPurchasableAsync(db);
        var id = await CreateCodeAsync(db, 10, plans: new[] { fx.PlanId });
        var code = (await db.PromoCodes.SingleAsync(p => p.Id == id)).Code;

        var applied = await CreatePromoCodes(db).ApplyAsync(code, fx.PlanId, fx.StudentId, CancellationToken.None);

        Assert.True(applied.Accepted);
    }

    [Fact]
    public async Task A_code_limited_to_another_package_is_refused()
    {
        await using var db = _fixture.CreateContext();
        var mine = await SeedPurchasableAsync(db);
        var other = await SeedPurchasableAsync(db);
        var id = await CreateCodeAsync(db, 10, plans: new[] { other.PlanId });
        var code = (await db.PromoCodes.SingleAsync(p => p.Id == id)).Code;

        var applied = await CreatePromoCodes(db).ApplyAsync(code, mine.PlanId, mine.StudentId, CancellationToken.None);

        Assert.False(applied.Accepted);
        Assert.Equal(PromoCodeRejection.NotForThisPackage, applied.Rejection);
    }

    // ------------------------------------------------------------ availability

    [Fact]
    public async Task A_disabled_code_is_refused()
    {
        await using var db = _fixture.CreateContext();
        var fx = await SeedPurchasableAsync(db);
        var id = await CreateCodeAsync(db, 10, isActive: false);
        var code = (await db.PromoCodes.SingleAsync(p => p.Id == id)).Code;

        var applied = await CreatePromoCodes(db).ApplyAsync(code, fx.PlanId, fx.StudentId, CancellationToken.None);

        Assert.Equal(PromoCodeRejection.Inactive, applied.Rejection);
    }

    [Fact]
    public async Task A_code_that_has_not_started_is_refused()
    {
        await using var db = _fixture.CreateContext();
        var fx = await SeedPurchasableAsync(db);
        var id = await CreateCodeAsync(db, 10, startsOn: Now.InUtc().Date.PlusDays(3));
        var code = (await db.PromoCodes.SingleAsync(p => p.Id == id)).Code;

        var applied = await CreatePromoCodes(db).ApplyAsync(code, fx.PlanId, fx.StudentId, CancellationToken.None);

        Assert.Equal(PromoCodeRejection.NotStartedYet, applied.Rejection);
    }

    [Fact]
    public async Task An_expired_code_is_refused()
    {
        await using var db = _fixture.CreateContext();
        var fx = await SeedPurchasableAsync(db);
        var id = await CreateCodeAsync(db, 10, endsOn: Now.InUtc().Date.PlusDays(-1));
        var code = (await db.PromoCodes.SingleAsync(p => p.Id == id)).Code;

        var applied = await CreatePromoCodes(db).ApplyAsync(code, fx.PlanId, fx.StudentId, CancellationToken.None);

        Assert.Equal(PromoCodeRejection.Expired, applied.Rejection);
    }

    [Fact]
    public async Task An_unknown_code_is_refused()
    {
        await using var db = _fixture.CreateContext();
        var fx = await SeedPurchasableAsync(db);

        var applied = await CreatePromoCodes(db).ApplyAsync("ZZZZZZ", fx.PlanId, fx.StudentId, CancellationToken.None);

        Assert.Equal(PromoCodeRejection.NotFound, applied.Rejection);
    }

    // ------------------------------------------------------------------ limits

    [Fact]
    public async Task The_total_usage_limit_is_enforced()
    {
        await using var db = _fixture.CreateContext();
        var fx = await SeedPurchasableAsync(db);
        var id = await CreateCodeAsync(db, 10, maxTotal: 1);
        var code = (await db.PromoCodes.SingleAsync(p => p.Id == id)).Code;

        var first = await CreateSubscriptions(db).PurchaseFromPlanAsync(fx.StudentId, fx.PlanId, fx.ActingUserId,
            SubscriptionOrigin.SelfPurchase, false, CancellationToken.None, code);
        Assert.Equal(PurchaseFromPlanOutcome.Purchased, first.Outcome);

        // A different student, so only the TOTAL limit can be what stops this.
        var other = await SeedPurchasableAsync(db);
        var applied = await CreatePromoCodes(db).ApplyAsync(code, other.PlanId, other.StudentId, CancellationToken.None);

        Assert.Equal(PromoCodeRejection.TotalLimitReached, applied.Rejection);
    }

    [Fact]
    public async Task The_per_student_usage_limit_is_enforced()
    {
        await using var db = _fixture.CreateContext();
        var fx = await SeedPurchasableAsync(db);
        var id = await CreateCodeAsync(db, 10, maxPerStudent: 1);
        var code = (await db.PromoCodes.SingleAsync(p => p.Id == id)).Code;

        var first = await CreateSubscriptions(db).PurchaseFromPlanAsync(fx.StudentId, fx.PlanId, fx.ActingUserId,
            SubscriptionOrigin.SelfPurchase, false, CancellationToken.None, code);
        Assert.Equal(PurchaseFromPlanOutcome.Purchased, first.Outcome);

        var applied = await CreatePromoCodes(db).ApplyAsync(code, fx.PlanId, fx.StudentId, CancellationToken.None);

        Assert.Equal(PromoCodeRejection.StudentLimitReached, applied.Rejection);
    }

    // ---------------------------------------------------------------- purchase

    [Fact]
    public async Task Buying_with_a_code_stamps_the_discounted_price_and_the_snapshot()
    {
        await using var db = _fixture.CreateContext();
        var fx = await SeedPurchasableAsync(db);           // 50.000 JOD
        var id = await CreateCodeAsync(db, 20);
        var code = (await db.PromoCodes.SingleAsync(p => p.Id == id)).Code;

        var result = await CreateSubscriptions(db).PurchaseFromPlanAsync(fx.StudentId, fx.PlanId, fx.ActingUserId,
            SubscriptionOrigin.SelfPurchase, false, CancellationToken.None, code);

        Assert.Equal(PurchaseFromPlanOutcome.Purchased, result.Outcome);

        db.ChangeTracker.Clear();
        var subscription = await db.Subscriptions.SingleAsync(s => s.Id == result.SubscriptionId);

        Assert.Equal(40.000m, subscription.Price.Amount);   // what is owed
        Assert.Equal(50.000m, subscription.ListPriceAmount); // what it lists for
        Assert.Equal(10.000m, subscription.DiscountAmount);
        Assert.Equal(20, subscription.DiscountPercent);
        Assert.Equal(id, subscription.PromoCodeId);

        // Still a Draft: 40 JOD is owed and no payment has been made.
        Assert.Equal(SubscriptionStatus.Draft, subscription.Status);
    }

    /// <summary>The owner's central rule. The purchase call carries the CODE;
    /// there is no parameter through which a price or a percentage could be
    /// supplied, so a crafted request cannot set its own discount.</summary>
    [Fact]
    public async Task The_discount_comes_from_the_database_not_from_the_caller()
    {
        await using var db = _fixture.CreateContext();
        var fx = await SeedPurchasableAsync(db);
        var id = await CreateCodeAsync(db, 10);            // the centre says 10%
        var code = (await db.PromoCodes.SingleAsync(p => p.Id == id)).Code;

        // A caller trying for a bigger discount has nothing to send it in, and
        // mangling the code itself simply makes it a code that does not exist.
        var forged = await CreateSubscriptions(db).PurchaseFromPlanAsync(fx.StudentId, fx.PlanId, fx.ActingUserId,
            SubscriptionOrigin.SelfPurchase, false, CancellationToken.None, code + "90");
        Assert.Equal(PurchaseFromPlanOutcome.PromoCodeRejected, forged.Outcome);

        var honest = await CreateSubscriptions(db).PurchaseFromPlanAsync(fx.StudentId, fx.PlanId, fx.ActingUserId,
            SubscriptionOrigin.SelfPurchase, false, CancellationToken.None, code);

        db.ChangeTracker.Clear();
        var subscription = await db.Subscriptions.SingleAsync(s => s.Id == honest.SubscriptionId);
        Assert.Equal(45.000m, subscription.Price.Amount);   // 10%, the centre's figure
    }

    /// <summary>Editing the code afterwards must not rewrite what a family
    /// already bought.</summary>
    [Fact]
    public async Task Changing_the_percentage_later_does_not_touch_an_existing_subscription()
    {
        await using var db = _fixture.CreateContext();
        var fx = await SeedPurchasableAsync(db);
        var id = await CreateCodeAsync(db, 20);
        var code = (await db.PromoCodes.SingleAsync(p => p.Id == id)).Code;

        var bought = await CreateSubscriptions(db).PurchaseFromPlanAsync(fx.StudentId, fx.PlanId, fx.ActingUserId,
            SubscriptionOrigin.SelfPurchase, false, CancellationToken.None, code);

        var updated = await CreatePromoCodes(db).UpdateAsync(id, 90, null, null, null, null, true,
            Array.Empty<long>(), CancellationToken.None);
        Assert.Equal(UpdatePromoCodeOutcome.Updated, updated);

        db.ChangeTracker.Clear();
        var subscription = await db.Subscriptions.SingleAsync(s => s.Id == bought.SubscriptionId);

        Assert.Equal(40.000m, subscription.Price.Amount);
        Assert.Equal(20, subscription.DiscountPercent);
        Assert.Equal(10.000m, subscription.DiscountAmount);
    }

    // ------------------------------------------------------------ free package

    /// <summary>A 100% code leaves nothing to pay, so there is no payment step:
    /// the package is active immediately, with exactly one ledger entry, and
    /// that entry has no payment behind it because none exists.</summary>
    [Fact]
    public async Task A_hundred_percent_code_activates_the_package_with_one_ledger_entry_and_no_payment()
    {
        await using var db = _fixture.CreateContext();
        var fx = await SeedPurchasableAsync(db);
        var id = await CreateCodeAsync(db, 100);
        var code = (await db.PromoCodes.SingleAsync(p => p.Id == id)).Code;

        var result = await CreateSubscriptions(db).PurchaseFromPlanAsync(fx.StudentId, fx.PlanId, fx.ActingUserId,
            SubscriptionOrigin.SelfPurchase, false, CancellationToken.None, code);

        Assert.Equal(PurchaseFromPlanOutcome.Purchased, result.Outcome);
        Assert.True(result.ActivatedWithoutPayment);

        db.ChangeTracker.Clear();
        var subscription = await db.Subscriptions.SingleAsync(s => s.Id == result.SubscriptionId);

        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
        Assert.Equal(0m, subscription.Price.Amount);
        Assert.Equal(50.000m, subscription.ListPriceAmount);
        Assert.Equal(100, subscription.DiscountPercent);

        // Exactly one, and it is a Purchase with no payment.
        var ledger = await db.EntitlementLedgerEntries
            .Where(l => l.SubscriptionId == subscription.Id).ToListAsync();
        Assert.Single(ledger);
        Assert.Equal(LedgerReason.Purchase, ledger[0].Reason);
        Assert.Null(ledger[0].PaymentId);
        Assert.Equal(subscription.MinutesTotal, ledger[0].DeltaMinutes);

        // No payment was invented to make this work.
        Assert.Equal(0, await db.Payments.CountAsync(p => p.SubscriptionId == subscription.Id));

        // And the hours are really usable: the balance is the package's minutes.
        var balance = await new EntitlementBalanceQuery(db)
            .GetSubscriptionBalancesAsync(new[] { subscription.Id }, CancellationToken.None);
        Assert.Equal(subscription.MinutesTotal, balance[subscription.Id]);
    }

    /// <summary>A code below 100% still owes money, so it must NOT activate -
    /// the pair of assertions matters more than either alone.</summary>
    [Fact]
    public async Task A_partial_discount_still_leaves_the_package_awaiting_payment()
    {
        await using var db = _fixture.CreateContext();
        var fx = await SeedPurchasableAsync(db);
        var id = await CreateCodeAsync(db, 99);
        var code = (await db.PromoCodes.SingleAsync(p => p.Id == id)).Code;

        var result = await CreateSubscriptions(db).PurchaseFromPlanAsync(fx.StudentId, fx.PlanId, fx.ActingUserId,
            SubscriptionOrigin.SelfPurchase, false, CancellationToken.None, code);

        Assert.False(result.ActivatedWithoutPayment);

        db.ChangeTracker.Clear();
        var subscription = await db.Subscriptions.SingleAsync(s => s.Id == result.SubscriptionId);
        Assert.Equal(SubscriptionStatus.Draft, subscription.Status);
        Assert.Equal(0.500m, subscription.Price.Amount);
        Assert.Empty(await db.EntitlementLedgerEntries.Where(l => l.SubscriptionId == subscription.Id).ToListAsync());
    }

    /// <summary>A rejected code refuses the whole purchase rather than quietly
    /// charging full price - the failure the owner named explicitly.</summary>
    [Fact]
    public async Task A_rejected_code_refuses_the_purchase_instead_of_charging_full_price()
    {
        await using var db = _fixture.CreateContext();
        var fx = await SeedPurchasableAsync(db);
        var id = await CreateCodeAsync(db, 50, isActive: false);
        var code = (await db.PromoCodes.SingleAsync(p => p.Id == id)).Code;

        var result = await CreateSubscriptions(db).PurchaseFromPlanAsync(fx.StudentId, fx.PlanId, fx.ActingUserId,
            SubscriptionOrigin.SelfPurchase, false, CancellationToken.None, code);

        Assert.Equal(PurchaseFromPlanOutcome.PromoCodeRejected, result.Outcome);
        Assert.Equal(PromoCodeRejection.Inactive, result.PromoRejection);
        Assert.Equal(0, await db.Subscriptions.CountAsync(s => s.StudentId == fx.StudentId));
    }

    /// <summary>Buying without a code is untouched by any of this.</summary>
    [Fact]
    public async Task Buying_without_a_code_is_unchanged()
    {
        await using var db = _fixture.CreateContext();
        var fx = await SeedPurchasableAsync(db);

        var result = await CreateSubscriptions(db).PurchaseFromPlanAsync(fx.StudentId, fx.PlanId, fx.ActingUserId,
            SubscriptionOrigin.SelfPurchase, false, CancellationToken.None);

        db.ChangeTracker.Clear();
        var subscription = await db.Subscriptions.SingleAsync(s => s.Id == result.SubscriptionId);

        Assert.Equal(SubscriptionStatus.Draft, subscription.Status);
        Assert.Equal(50.000m, subscription.Price.Amount);
        Assert.Equal(50.000m, subscription.ListPriceAmount);
        Assert.Equal(0, subscription.DiscountPercent);
        Assert.Null(subscription.PromoCodeId);
    }
}
