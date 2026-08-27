using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Common;
using MVTeaches.Domain.Subscriptions;
using NodaTime;

namespace MVTeaches.Application.Subscriptions;

public record CreatePricingPlanResult(long PricingPlanId);

public record PurchaseSubscriptionResult(long SubscriptionId, Money Price);

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
    /// hours, and validity (D-10) — it is NOT activated here. Activation and the
    /// Purchase ledger entry happen only once a payment tied to this
    /// subscription is confirmed in full (IPaymentService.ConfirmAsync, already
    /// built and tested) — D-38's "no partial activation" rule.</summary>
    Task<PurchaseSubscriptionResult> PurchaseFromPlanAsync(long studentId, long pricingPlanId, int levelId,
        SubscriptionOrigin origin, long createdByUserId, CancellationToken cancellationToken);

    /// <summary>D-13: an admin-created subscription with NO payment. Unlike the
    /// purchase path above, this activates immediately and posts the AdminGrant
    /// ledger entry in the same operation — there is no payment step to wait
    /// for, so nothing should ever leave this in Draft.</summary>
    Task<PurchaseSubscriptionResult> GrantAdminSubscriptionAsync(long studentId, int countryId, long courseId,
        int levelId, int sessionsCount, int minutesTotal, int validityDays, long createdByUserId, string reason,
        CancellationToken cancellationToken);
}
