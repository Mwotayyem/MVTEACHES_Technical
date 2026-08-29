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

    public async Task<PurchaseFromPlanResult> PurchaseFromPlanAsync(long studentId, long pricingPlanId,
        long actingUserId, SubscriptionOrigin origin, bool isAdminInitiated, CancellationToken cancellationToken)
    {
        // Owner decision 2026-08-30 rules 1/4 — the same IDOR guard
        // JoinAttendanceService.IsAuthorizedToJoinAsync uses: the acting
        // account must actually be this student's own login or one of their
        // guardians, never trusted from studentId arriving in the request alone.
        // Skipped only for the Admin/Subscriptions page's manual-payment flow,
        // whose own [Authorize(Roles=Admin,SystemAdmin)] already establishes
        // the caller's authority — the level/type checks below still apply to it.
        if (!isAdminInitiated)
        {
            var isTheStudentThemself = await _db.Students.AnyAsync(s => s.Id == studentId && s.UserId == actingUserId, cancellationToken);
            var isAnActiveGuardian = !isTheStudentThemself && await _db.Guardianships
                .Join(_db.Guardians, gs => gs.GuardianId, g => g.Id, (gs, g) => new { gs.StudentId, g.UserId })
                .AnyAsync(x => x.StudentId == studentId && x.UserId == actingUserId, cancellationToken);
            if (!isTheStudentThemself && !isAnActiveGuardian)
            {
                return new PurchaseFromPlanResult(PurchaseFromPlanOutcome.Unauthorized);
            }
        }

        var plan = await _db.PricingPlans.FirstOrDefaultAsync(p => p.Id == pricingPlanId, cancellationToken);
        if (plan is null)
        {
            return new PurchaseFromPlanResult(PurchaseFromPlanOutcome.PlanNotFound);
        }

        // Rule 4: "every published package is explicitly associated with...
        // one level." A wildcard (LevelId == null, "applies to every level")
        // or an inactive plan can never be bought through this self-service
        // path, regardless of who is asking — that is what makes it
        // deliberately impossible to purchase the wrong level even if the
        // level check below were somehow bypassed.
        if (!plan.IsActive || plan.LevelId is null)
        {
            return new PurchaseFromPlanResult(PurchaseFromPlanOutcome.PlanNotPublishedForAnyLevel);
        }

        // Rule 1: the student's OWN current level, resolved server-side —
        // never accepted as a request parameter (StudentBookingService's
        // exact same convention).
        var currentLevelId = await _db.StudentLevels
            .Where(l => l.StudentId == studentId && l.IsCurrent)
            .Select(l => (int?)l.LevelId)
            .FirstOrDefaultAsync(cancellationToken);
        if (currentLevelId is null)
        {
            return new PurchaseFromPlanResult(PurchaseFromPlanOutcome.StudentHasNoAssignedLevel);
        }

        if (plan.LevelId.Value != currentLevelId.Value)
        {
            return new PurchaseFromPlanResult(PurchaseFromPlanOutcome.LevelMismatch);
        }

        // A genuine, documented simplification (matches DashboardModel's own
        // "today" convention) — not per-country-timezone purchase dating.
        var today = _clock.GetCurrentInstant().InUtc().Date;

        var subscription = new Subscription(studentId, plan.CountryId, plan.CourseId, plan.LevelId.Value,
            plan.SessionType, plan.Amount, plan.Id, plan.SessionsCount, plan.MinutesTotal, today, plan.ValidityDays,
            origin, actingUserId, createdReason: null); // reason only mandatory for AdminCreated — see GrantAdminSubscriptionAsync
        _db.Subscriptions.Add(subscription);
        await _db.SaveChangesAsync(cancellationToken);

        return new PurchaseFromPlanResult(PurchaseFromPlanOutcome.Purchased, subscription.Id, plan.Amount);
    }

    public async Task<PurchaseSubscriptionResult> GrantAdminSubscriptionAsync(long studentId, int countryId,
        long courseId, int levelId, SessionType sessionType, int sessionsCount, int minutesTotal, int validityDays,
        long createdByUserId, string reason, CancellationToken cancellationToken)
    {
        var country = await _db.Countries.FirstOrDefaultAsync(c => c.Id == countryId, cancellationToken)
            ?? throw new InvalidOperationException("Country not found.");

        var today = _clock.GetCurrentInstant().InUtc().Date;
        var now = _clock.GetCurrentInstant();

        // D-13: a free grant has no price to speak of — recorded as zero in the
        // country's own currency (never a hardcoded currency literal).
        var subscription = new Subscription(studentId, countryId, courseId, levelId, sessionType, Money.Zero(country.CurrencyCode),
            pricingPlanId: null, sessionsCount, minutesTotal, today, validityDays, SubscriptionOrigin.AdminCreated,
            createdByUserId, reason);
        _db.Subscriptions.Add(subscription);

        // Unlike the purchase path, there is no payment to wait for — activate
        // and post the ledger entry in the SAME operation (§20.2's AdminGrant reason).
        subscription.Activate();
        await _db.SaveChangesAsync(cancellationToken); // subscription needs its Id before the ledger entry below

        _db.EntitlementLedgerEntries.Add(EntitlementLedgerEntry.ForAdminGrant(
            studentId, subscription.Id, courseId, levelId, sessionType, minutesTotal, createdByUserId, reason, now));
        await _db.SaveChangesAsync(cancellationToken);

        return new PurchaseSubscriptionResult(subscription.Id, subscription.Price);
    }
}
