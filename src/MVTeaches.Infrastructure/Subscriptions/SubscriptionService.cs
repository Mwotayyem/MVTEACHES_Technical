using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Subscriptions;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Common;
using MVTeaches.Domain.Ledger;
using MVTeaches.Domain.Subscriptions;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;

namespace MVTeaches.Infrastructure.Subscriptions;

/// <inheritdoc cref="ISubscriptionService"/>
public class SubscriptionService : ISubscriptionService
{
    private readonly MvTeachesDbContext _db;
    private readonly IClock _clock;

    public SubscriptionService(MvTeachesDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<CreatePricingPlanResult> CreatePricingPlanAsync(int countryId, long courseId, int? levelId,
        int? ageGroupId, SessionType sessionType, int sessionsCount, int minutesTotal, Money amount,
        int validityDays, LocalDate effectiveFrom, long createdByUserId, CancellationToken cancellationToken)
    {
        var plan = new PricingPlan(countryId, courseId, levelId, ageGroupId, sessionType, sessionsCount,
            minutesTotal, amount, validityDays, effectiveFrom, createdByUserId);
        _db.PricingPlans.Add(plan);
        await _db.SaveChangesAsync(cancellationToken);
        return new CreatePricingPlanResult(plan.Id);
    }

    public async Task<PurchaseSubscriptionResult> PurchaseFromPlanAsync(long studentId, long pricingPlanId,
        int levelId, SubscriptionOrigin origin, long createdByUserId, CancellationToken cancellationToken)
    {
        var plan = await _db.PricingPlans.FirstOrDefaultAsync(p => p.Id == pricingPlanId, cancellationToken)
            ?? throw new InvalidOperationException("Pricing plan not found.");

        // A genuine, documented simplification (matches DashboardModel's own
        // "today" convention) — not per-country-timezone purchase dating.
        var today = _clock.GetCurrentInstant().InUtc().Date;

        var subscription = new Subscription(studentId, plan.CountryId, plan.CourseId, levelId, plan.Amount,
            plan.Id, plan.SessionsCount, plan.MinutesTotal, today, plan.ValidityDays, origin, createdByUserId,
            createdReason: null); // reason only mandatory for AdminCreated — see GrantAdminSubscriptionAsync
        _db.Subscriptions.Add(subscription);
        await _db.SaveChangesAsync(cancellationToken);

        return new PurchaseSubscriptionResult(subscription.Id, plan.Amount);
    }

    public async Task<PurchaseSubscriptionResult> GrantAdminSubscriptionAsync(long studentId, int countryId,
        long courseId, int levelId, int sessionsCount, int minutesTotal, int validityDays, long createdByUserId,
        string reason, CancellationToken cancellationToken)
    {
        var country = await _db.Countries.FirstOrDefaultAsync(c => c.Id == countryId, cancellationToken)
            ?? throw new InvalidOperationException("Country not found.");

        var today = _clock.GetCurrentInstant().InUtc().Date;
        var now = _clock.GetCurrentInstant();

        // D-13: a free grant has no price to speak of — recorded as zero in the
        // country's own currency (never a hardcoded currency literal).
        var subscription = new Subscription(studentId, countryId, courseId, levelId, Money.Zero(country.CurrencyCode),
            pricingPlanId: null, sessionsCount, minutesTotal, today, validityDays, SubscriptionOrigin.AdminCreated,
            createdByUserId, reason);
        _db.Subscriptions.Add(subscription);

        // Unlike the purchase path, there is no payment to wait for — activate
        // and post the ledger entry in the SAME operation (§20.2's AdminGrant reason).
        subscription.Activate();
        await _db.SaveChangesAsync(cancellationToken); // subscription needs its Id before the ledger entry below

        _db.EntitlementLedgerEntries.Add(EntitlementLedgerEntry.ForAdminGrant(
            studentId, subscription.Id, courseId, levelId, minutesTotal, createdByUserId, reason, now));
        await _db.SaveChangesAsync(cancellationToken);

        return new PurchaseSubscriptionResult(subscription.Id, subscription.Price);
    }
}
