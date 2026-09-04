using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Ledger;
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

    /// <summary>Owner decision 2026-09-04 (duplicate-purchase guard): the
    /// remaining minutes on an existing subscription are read through the one
    /// approved path (D-36/§20.2's SUM at read time), never re-derived inline
    /// here — that is precisely what IEntitlementBalanceQuery's own remarks
    /// forbid every caller from doing.</summary>
    private readonly IEntitlementBalanceQuery _balances;

    public SubscriptionService(MvTeachesDbContext db, IClock clock, IEntitlementBalanceQuery balances)
    {
        _db = db;
        _clock = clock;
        _balances = balances;
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

            // Owner decision 2026-09-04 (a student under guardian care never
            // buys for themself). Deliberately NOT an age test: the owner ruled
            // out inventing an age threshold, and the link itself is the whole
            // criterion — if someone is registered as responsible for this
            // student, then buying is that person's decision, not the student's.
            // A student with no guardian linked is unaffected and still buys
            // normally, which is the ordinary adult-learner case.
            //
            // Only the student's OWN login is blocked. The guardian branch above
            // has already established that any guardian reaching this line is
            // one of THIS student's guardians, so an unrelated guardian was
            // rejected as Unauthorized before ever getting here.
            if (isTheStudentThemself)
            {
                var isUnderGuardianCare = await _db.Guardianships
                    .AnyAsync(g => g.StudentId == studentId, cancellationToken);
                if (isUnderGuardianCare)
                {
                    return new PurchaseFromPlanResult(PurchaseFromPlanOutcome.StudentIsUnderGuardianCare);
                }
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
        //
        // Owner decision 2026-09-04 (multi-course levels): the level compared
        // is the student's level IN THIS PLAN'S OWN COURSE. Before the course
        // column existed there was one global level, so buying a Spanish
        // package was gated on the student's English level — it either blocked
        // a legitimate purchase or waved through a wrong-level one, depending
        // only on which course they happened to be placed in first.
        var currentLevelId = await _db.StudentLevels
            .Where(l => l.StudentId == studentId && l.CourseId == plan.CourseId && l.IsCurrent)
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

        // Owner decision 2026-09-04 (duplicate-purchase guard). Until this
        // existed, nothing stopped the same package being bought over and
        // over for the same student, and staging shows exactly what that
        // costs: one student holding FOUR separately-paid subscriptions for
        // the identical plan — 200 JOD for a 50 JOD package — bought partly
        // from their own login and partly from their guardian's. That is why
        // this is keyed on studentId and never on actingUserId: who clicks is
        // irrelevant, what the STUDENT already owns is the whole question.
        //
        // The row lock is the same tool (and the same reason)
        // StudentBookingService.BookSessionAsync uses for its own
        // "sum across many rows" rule: no single-row CHECK constraint can
        // express "this student must not already hold this plan", and a
        // partial unique index would be a schema change. Serializing this
        // student's own concurrent attempts is what makes two fast clicks
        // resolve to one subscription instead of both racing past the same
        // read. Everything cheaper (authorization, plan, level) has already
        // been checked above, so the lock is only ever taken on a request
        // that would otherwise really create a row.
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM students WHERE \"Id\" = {studentId} FOR UPDATE", cancellationToken);

        // Draft = requested, not yet paid. Active/Extended = paid and live
        // (Extended is an Active subscription whose expiry a human pushed
        // out — still usable, so still a package the student holds).
        // Expired/Cancelled/Completed are deliberately absent: those are done,
        // and buying the same package again is exactly what SHOULD happen next.
        var heldForThisPlan = await _db.Subscriptions
            .Where(s => s.StudentId == studentId && s.PricingPlanId == pricingPlanId
                        && (s.Status == SubscriptionStatus.Draft
                            || s.Status == SubscriptionStatus.Active
                            || s.Status == SubscriptionStatus.Extended))
            .OrderBy(s => s.Id)
            .ToListAsync(cancellationToken);

        var awaitingPayment = heldForThisPlan.FirstOrDefault(s => s.Status == SubscriptionStatus.Draft);
        if (awaitingPayment is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new PurchaseFromPlanResult(PurchaseFromPlanOutcome.DraftAlreadyAwaitingPayment,
                awaitingPayment.Id, awaitingPayment.Price);
        }

        var live = heldForThisPlan.Where(s => s.Status != SubscriptionStatus.Draft).ToList();
        if (live.Count > 0)
        {
            var balances = await _balances.GetSubscriptionBalancesAsync(
                live.Select(s => s.Id).ToList(), cancellationToken);
            var stillUsable = live.FirstOrDefault(s => balances.GetValueOrDefault(s.Id) > 0);
            if (stillUsable is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new PurchaseFromPlanResult(PurchaseFromPlanOutcome.ActivePackageStillHasBalance,
                    stillUsable.Id, stillUsable.Price);
            }
        }

        // A genuine, documented simplification (matches DashboardModel's own
        // "today" convention) — not per-country-timezone purchase dating.
        var today = _clock.GetCurrentInstant().InUtc().Date;

        // D-10's price SNAPSHOT, as its own value rather than a reference to
        // the plan's live one. Both Subscription.Price and PricingPlan.Amount
        // are EF OWNED types (see SubscriptionsPaymentsConfigurations), and an
        // owned instance belongs to exactly one principal — handing this same
        // tracked Money to a second Subscription makes EF try to re-parent it
        // and throw ("Price#Money.SubscriptionId is part of a key"). That
        // never surfaced while one plan could only ever be bought once per
        // context; it does the moment a student may legitimately re-buy a
        // plan they have finished (see the guard above). Same amount, same
        // currency — this changes what is stored not at all.
        var price = new Money(plan.Amount.Amount, plan.Amount.Currency);

        var subscription = new Subscription(studentId, plan.CountryId, plan.CourseId, plan.LevelId.Value,
            plan.SessionType, price, plan.Id, plan.SessionsCount, plan.MinutesTotal, today, plan.ValidityDays,
            origin, actingUserId, createdReason: null); // reason only mandatory for AdminCreated — see GrantAdminSubscriptionAsync
        _db.Subscriptions.Add(subscription);
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

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
