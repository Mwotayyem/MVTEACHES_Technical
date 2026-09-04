using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Subscriptions;
using MVTeaches.Domain.Catalog;
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
/// Owner report 2026-09-05, from a real Local Staging run. A guardian with two
/// daughters bought a package for the first, and when buying for the second was
/// told "you already have a request for this package (#3) still awaiting
/// payment" — which reads as the first daughter's request blocking the second.
///
/// <para>The rule the owner set: a guardian may buy the same package for as
/// many of their children as they like; the duplicate guard applies to one
/// student and one plan, never across a family. These tests exist to pin that
/// down permanently, because the guard is the kind of code that a later
/// "helpful" widening — keying it on the payer instead of the student — would
/// break silently and expensively.</para>
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class SiblingPurchaseTests
{
    private readonly TestDatabaseFixture _fixture;
    private static long _idSeed = 66_000_000;

    public SiblingPurchaseTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static long NextId() => Interlocked.Increment(ref _idSeed);

    private static string TwoLetterCode(long seed)
    {
        var n = (int)(seed % 676);
        return string.Concat((char)('A' + n / 26), (char)('A' + n % 26));
    }

    /// <summary>Its own country, retrying on the unique code — the same reason
    /// SubscriptionServiceTests does it: these classes share one database and
    /// must not depend on another's rows or on seeding having run.</summary>
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

    private static ISubscriptionService CreateService(MvTeachesDbContext db) =>
        new SubscriptionService(db, new FakeClock(SystemClock.Instance.GetCurrentInstant()),
            new EntitlementBalanceQuery(db));

    private sealed record Family(long GuardianUserId, long FirstChildId, long SecondChildId, long PlanId);

    /// <summary>One guardian, two children, both placed at the same level in
    /// the same course, and one package published for it — the owner's exact
    /// shape.</summary>
    private async Task<Family> SeedFamilyAsync(MvTeachesDbContext db, ISubscriptionService subscriptions)
    {
        var countryId = await SeedCountryAsync(db);

        var course = new Course($"SIB-{NextId()}", "دورة", "Sibling Course");
        db.Courses.Add(course);

        var levelId = (int)NextId();
        db.Levels.Add(new Level(levelId, $"SB{levelId}", "مستوى", "Level", levelId));
        await db.SaveChangesAsync();

        var guardianUser = new MVTeaches.Infrastructure.Identity.ApplicationUser
        {
            UserName = $"sib-guardian-{Guid.NewGuid():N}",
            Email = $"sib-guardian-{Guid.NewGuid():N}@test.mvteaches.local",
        };
        db.Users.Add(guardianUser);
        await db.SaveChangesAsync();

        var guardian = new MVTeaches.Domain.People.Guardian(guardianUser.Id, "Guardian Of Two");
        db.Guardians.Add(guardian);

        var first = new Student(countryId, "First Daughter", new LocalDate(2012, 3, 3), userId: null);
        var second = new Student(countryId, "Second Daughter", new LocalDate(2014, 7, 7), userId: null);
        db.Students.AddRange(first, second);
        await db.SaveChangesAsync();

        db.Guardianships.Add(new Guardianship(guardian.Id, first.Id, GuardianRelationship.Parent, true, guardianUser.Id));
        db.Guardianships.Add(new Guardianship(guardian.Id, second.Id, GuardianRelationship.Parent, false, guardianUser.Id));

        var now = SystemClock.Instance.GetCurrentInstant();
        foreach (var studentId in new[] { first.Id, second.Id })
        {
            db.StudentLevels.Add(new StudentLevel(studentId, course.Id, levelId, guardianUser.Id,
                AssignedByRole.Admin, LevelAssignmentSource.AdminOverride, null, "seed", now));
        }
        await db.SaveChangesAsync();

        var plan = await subscriptions.CreatePricingPlanAsync(countryId, course.Id, levelId, null,
            SessionType.Group, 10, 600, new MVTeaches.Domain.Common.Money(50m, "JOD"), 30,
            now.InUtc().Date, guardianUser.Id, CancellationToken.None);

        return new Family(guardianUser.Id, first.Id, second.Id, plan.PricingPlanId);
    }

    /// <summary>The owner's rule, stated as a test: the second child gets her
    /// own request, for the same package, at the same time as her sister's is
    /// still unpaid.</summary>
    [Fact]
    public async Task A_guardian_buys_the_same_package_for_a_second_child()
    {
        await using var db = _fixture.CreateContext();
        var subscriptions = CreateService(db);
        var family = await SeedFamilyAsync(db, subscriptions);

        var forFirst = await subscriptions.PurchaseFromPlanAsync(family.FirstChildId, family.PlanId,
            family.GuardianUserId, SubscriptionOrigin.GuardianPurchase, false, CancellationToken.None);
        Assert.Equal(PurchaseFromPlanOutcome.Purchased, forFirst.Outcome);

        // The first child's request is left deliberately unpaid — that is the
        // exact state that produced the owner's message.
        var forSecond = await subscriptions.PurchaseFromPlanAsync(family.SecondChildId, family.PlanId,
            family.GuardianUserId, SubscriptionOrigin.GuardianPurchase, false, CancellationToken.None);

        Assert.Equal(PurchaseFromPlanOutcome.Purchased, forSecond.Outcome);
        Assert.NotEqual(forFirst.SubscriptionId, forSecond.SubscriptionId);

        // And each request belongs to the child it was made for — the mix-up
        // the owner was worried about would show up right here.
        var firstSub = await db.Subscriptions.SingleAsync(s => s.Id == forFirst.SubscriptionId);
        var secondSub = await db.Subscriptions.SingleAsync(s => s.Id == forSecond.SubscriptionId);
        Assert.Equal(family.FirstChildId, firstSub.StudentId);
        Assert.Equal(family.SecondChildId, secondSub.StudentId);
    }

    /// <summary>The guard still does its own job: the SAME child cannot stack
    /// two unpaid requests for the same package. Both halves matter — this is
    /// what stopped one student holding four separately-paid copies of one 50
    /// JOD package.</summary>
    [Fact]
    public async Task The_same_child_still_cannot_stack_two_unpaid_requests()
    {
        await using var db = _fixture.CreateContext();
        var subscriptions = CreateService(db);
        var family = await SeedFamilyAsync(db, subscriptions);

        var first = await subscriptions.PurchaseFromPlanAsync(family.FirstChildId, family.PlanId,
            family.GuardianUserId, SubscriptionOrigin.GuardianPurchase, false, CancellationToken.None);
        Assert.Equal(PurchaseFromPlanOutcome.Purchased, first.Outcome);

        var again = await subscriptions.PurchaseFromPlanAsync(family.FirstChildId, family.PlanId,
            family.GuardianUserId, SubscriptionOrigin.GuardianPurchase, false, CancellationToken.None);

        Assert.Equal(PurchaseFromPlanOutcome.DraftAlreadyAwaitingPayment, again.Outcome);
        // It names the request the payer should finish, which is what the
        // message on screen now quotes back to them along with the child's name.
        Assert.Equal(first.SubscriptionId, again.SubscriptionId);
        Assert.Equal(1, await db.Subscriptions.CountAsync(s => s.StudentId == family.FirstChildId));
    }

    /// <summary>The sibling's unpaid request must not even be visible to the
    /// guard — asserted separately from the outcome above, because "purchased
    /// anyway" could in principle happen for the wrong reason.</summary>
    [Fact]
    public async Task One_childs_unpaid_request_is_not_counted_against_another()
    {
        await using var db = _fixture.CreateContext();
        var subscriptions = CreateService(db);
        var family = await SeedFamilyAsync(db, subscriptions);

        await subscriptions.PurchaseFromPlanAsync(family.FirstChildId, family.PlanId,
            family.GuardianUserId, SubscriptionOrigin.GuardianPurchase, false, CancellationToken.None);
        await subscriptions.PurchaseFromPlanAsync(family.SecondChildId, family.PlanId,
            family.GuardianUserId, SubscriptionOrigin.GuardianPurchase, false, CancellationToken.None);

        var drafts = await db.Subscriptions
            .Where(s => s.PricingPlanId == family.PlanId && s.Status == SubscriptionStatus.Draft)
            .ToListAsync();

        Assert.Equal(2, drafts.Count);
        Assert.Single(drafts, s => s.StudentId == family.FirstChildId);
        Assert.Single(drafts, s => s.StudentId == family.SecondChildId);
    }
}
