using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Common;
using MVTeaches.Domain.Subscriptions;
using NodaTime;

namespace MVTeaches.Application.Subscriptions;

public record CreatePricingPlanResult(long PricingPlanId);

public enum PurchaseFromPlanOutcome
{
    Purchased,
    PlanNotFound,

    /// <summary>Owner decision 2026-08-30 rule 4: a published package is
    /// always tied to exactly one level; a plan with no level (or one marked
    /// inactive) can never be purchased through the self/guardian-purchase
    /// path, regardless of who is asking.</summary>
    PlanNotPublishedForAnyLevel,

    /// <summary>Owner decision 2026-08-30 rule 1: "Until a placement result
    /// exists, the student must not purchase a package."</summary>
    StudentHasNoAssignedLevel,

    /// <summary>Rule 4: "A student may purchase only a package whose level
    /// matches their current assigned level." Enforced here — the level is
    /// never accepted from the caller, only ever the plan's own LevelId
    /// compared against the student's own current StudentLevel row.</summary>
    LevelMismatch,

    Unauthorized,
}

public record PurchaseSubscriptionResult(long SubscriptionId, Money Price);
public record PurchaseFromPlanResult(PurchaseFromPlanOutcome Outcome, long? SubscriptionId = null, Money? Price = null);

/// <summary>
/// Technical Study §23 (pricing plans, D-10/D-53/D-64/D-86) and §19.2/§20.2
/// (subscriptions + the entitlement ledger, D-13). This is the missing link
/// in the "purchase and payment" MVP item: the domain model
/// (PricingPlan/Subscription/EntitlementLedgerEntry) and IPaymentService's
/// own full-payment-activates-and-posts-Purchase logic already existed and
/// were already tested — there was simply no way to CREATE a pricing plan or
/// a subscription in the first place, so a payment could never actually be
/// tied to a real purchase before this.
/// </summary>
public interface ISubscriptionService
{
    /// <summary>§23.3: plans are immutable once live — a price change means
    /// closing the old row (not exposed here yet) and creating a new one.</summary>
    Task<CreatePricingPlanResult> CreatePricingPlanAsync(int countryId, long courseId, int? levelId, int? ageGroupId,
        SessionType sessionType, int sessionsCount, int minutesTotal, Money amount, int validityDays,
        LocalDate effectiveFrom, long createdByUserId, CancellationToken cancellationToken);

    /// <summary>Self/guardian-purchase path (D-13's SelfPurchase/GuardianPurchase
    /// origins): creates a Draft subscription snapshotting the plan's price,
    /// level, session type, hours, and validity (D-10) — it is NOT activated
    /// here. Activation and the Purchase ledger entry happen only once a
    /// payment tied to this subscription is confirmed in full
    /// (IPaymentService.ConfirmAsync, already built and tested) — D-38's
    /// "no partial activation" rule.
    ///
    /// Owner decision 2026-08-30 rules 1/4: unless <paramref name="isAdminInitiated"/>
    /// is true, <paramref name="actingUserId"/> must be the student themself or
    /// one of their guardians (re-checked here, never trusted from the caller —
    /// the same IDOR guard JoinAttendanceService uses). The level purchased is
    /// ALWAYS the plan's own LevelId compared against the student's current
    /// StudentLevel — there is deliberately no levelId parameter here for a
    /// caller to supply, since that was the exact trust gap this method used
    /// to have. <paramref name="isAdminInitiated"/> exists ONLY for the
    /// existing Admin/Subscriptions page's manual-payment-recording flow,
    /// whose own [Authorize(Roles=Admin,SystemAdmin)] already establishes the
    /// caller's authority — it never skips the level/session-type checks,
    /// which D-94 requires apply to a manual payment exactly as they do to
    /// self-service.</summary>
    Task<PurchaseFromPlanResult> PurchaseFromPlanAsync(long studentId, long pricingPlanId, long actingUserId,
        SubscriptionOrigin origin, bool isAdminInitiated, CancellationToken cancellationToken);

    /// <summary>D-13: an admin-created subscription with NO payment. Unlike the
    /// purchase path above, this activates immediately and posts the AdminGrant
    /// ledger entry in the same operation — there is no payment step to wait
    /// for, so nothing should ever leave this in Draft. This exceptional path
    /// alone still lets an admin pick the level/type explicitly (e.g. a
    /// goodwill grant, a migration correction) — see D-94's "admin
    /// intervention only for cash/exceptional cases".</summary>
    Task<PurchaseSubscriptionResult> GrantAdminSubscriptionAsync(long studentId, int countryId, long courseId,
        int levelId, SessionType sessionType, int sessionsCount, int minutesTotal, int validityDays,
        long createdByUserId, string reason, CancellationToken cancellationToken);
}
