using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Common;
using NodaTime;

namespace MVTeaches.Domain.Subscriptions;

public enum SubscriptionStatus
{
    Draft,
    Active,
    Expired,
    Extended,
    Cancelled,
    Completed,
}

/// <summary>D-13: distinguishes WHO created the subscription and WHY — without
/// this, financial reports cannot tell real revenue from a receivable from a
/// migrated opening balance (Technical Study §19.2).</summary>
public enum SubscriptionOrigin
{
    SelfPurchase,
    GuardianPurchase,
    AdminCreated,
    Migration,
}

/// <summary>
/// Technical Study §19.2. Activation of the subscription is independent of
/// payment completion (D-13) — see PaymentEligibilityService for the
/// "fully paid ⟹ eligible to attend" rule (D-38) that governs whether a
/// Join press against this subscription's balance is allowed at all.
/// </summary>
public class Subscription
{
    public long Id { get; private set; }

    public long StudentId { get; private set; }
    public int CountryId { get; private set; }
    public long CourseId { get; private set; }
    public int LevelId { get; private set; }

    /// <summary>Owner decision 2026-08-30 rule 4: a package — and therefore
    /// every subscription purchased from one — is for Group sessions or
    /// Private sessions, never both. A Group entitlement can never be drawn
    /// on to book or attend a Private session and vice versa; every balance
    /// query and consumption path is scoped by this alongside Course/Level.</summary>
    public SessionType SessionType { get; private set; }

    /// <summary>Snapshot at purchase time (D-10) — never re-read from the
    /// pricing plan later.</summary>
    /// <summary>What the family actually owes: the price AFTER any promo-code
    /// discount. Its meaning is unchanged - every funding, activation and
    /// refund path still reads this and only this - which is why the discount
    /// is applied before the subscription is built rather than subtracted
    /// somewhere downstream.</summary>
    public Money Price { get; private set; } = null!;

    public long? PricingPlanId { get; private set; }

    /// <summary>Owner decision 2026-09-05 (promo codes). The three fields below
    /// are a SNAPSHOT, in the same spirit as Price itself: what the package
    /// cost before the discount, which code was used, and what that code was
    /// worth AT THE MOMENT OF PURCHASE. An admin editing the code's percentage
    /// next month must not change what this family bought last month, and a
    /// deleted or disabled code must not erase the record that a discount was
    /// given - so the percentage and the amount are copied here rather than
    /// looked up through PromoCodeId later.
    /// <para>Null/zero throughout when no code was used, which is every
    /// subscription that existed before this feature.</para></summary>
    public long? PromoCodeId { get; private set; }

    /// <summary>The package's own price before the discount. Equal to
    /// <see cref="Price"/> when no code was used.</summary>
    public decimal ListPriceAmount { get; private set; }

    public int DiscountPercent { get; private set; }

    public decimal DiscountAmount { get; private set; }

    public int SessionsCount { get; private set; }
    public int MinutesTotal { get; private set; }

    public LocalDate StartsOn { get; private set; }

    /// <summary>Always DERIVED as StartsOn + ValidityDays + SUM(freeze days) — see
    /// ExpiryCalculator. Never written directly (§19.3): a direct write breaks
    /// freeze tracking.</summary>
    public LocalDate ExpiresOn { get; private set; }

    /// <summary>D-64: snapshotted per-package validity, never a hardcoded constant.</summary>
    public int ValidityDays { get; private set; }

    public SubscriptionStatus Status { get; private set; } = SubscriptionStatus.Draft;
    public SubscriptionOrigin Origin { get; private set; }

    public long CreatedByUserId { get; private set; }

    /// <summary>Mandatory when Origin == AdminCreated (D-13).</summary>
    public string? CreatedReason { get; private set; }

    public long? ExtendedByUserId { get; private set; }
    public string? ExtendedReason { get; private set; }
    public LocalDate? ExtendedTo { get; private set; }

    private Subscription() { }

    public Subscription(long studentId, int countryId, long courseId, int levelId, SessionType sessionType, Money price,
        long? pricingPlanId, int sessionsCount, int minutesTotal, LocalDate startsOn, int validityDays,
        SubscriptionOrigin origin, long createdByUserId, string? createdReason)
    {
        if (sessionsCount <= 0) throw new ArgumentOutOfRangeException(nameof(sessionsCount));
        if (minutesTotal <= 0) throw new ArgumentOutOfRangeException(nameof(minutesTotal));
        if (validityDays <= 0) throw new ArgumentOutOfRangeException(nameof(validityDays));

        if (origin == SubscriptionOrigin.AdminCreated && string.IsNullOrWhiteSpace(createdReason))
        {
            throw new ArgumentException("An admin-created subscription requires a reason (D-13).", nameof(createdReason));
        }

        StudentId = studentId;
        CountryId = countryId;
        CourseId = courseId;
        LevelId = levelId;
        SessionType = sessionType;
        Price = price;
        PricingPlanId = pricingPlanId;
        // No code unless one is recorded below: the list price IS the price.
        ListPriceAmount = price.Amount;
        SessionsCount = sessionsCount;
        MinutesTotal = minutesTotal;
        StartsOn = startsOn;
        ValidityDays = validityDays;
        ExpiresOn = startsOn.PlusDays(validityDays);
        Origin = origin;
        CreatedByUserId = createdByUserId;
        CreatedReason = createdReason;
        Status = SubscriptionStatus.Draft;
    }

    /// <summary>Records the code that was applied, and what it was worth, at
    /// the moment of purchase. Called only while the subscription is being
    /// created - a discount is never applied to a subscription that already
    /// exists, because its price has already been quoted to the family and,
    /// once Active, already drawn against.</summary>
    public void RecordPromoCode(long promoCodeId, int discountPercent, decimal listPriceAmount, decimal discountAmount)
    {
        if (Status != SubscriptionStatus.Draft && Status != SubscriptionStatus.Active)
        {
            throw new InvalidOperationException("A promo code can only be recorded as a subscription is created.");
        }

        PromoCodeId = promoCodeId;
        DiscountPercent = discountPercent;
        ListPriceAmount = listPriceAmount;
        DiscountAmount = discountAmount;
    }

    public void Activate() => Status = SubscriptionStatus.Active;

    /// <summary>D-17: natural completion of every purchased minute — no extension, ever.</summary>
    public void CompleteNaturally() => Status = SubscriptionStatus.Completed;

    /// <summary>Set by the nightly sweep only (§19.3) — never anything else. The
    /// decision to extend afterward is always a human, audited action.</summary>
    public void MarkExpired() => Status = SubscriptionStatus.Expired;

    public void Cancel() => Status = SubscriptionStatus.Cancelled;

    /// <summary>D-18: exceptional extension. Reason is mandatory and audited.</summary>
    public void Extend(long extendedByUserId, string reason, LocalDate extendedTo)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("An extension requires a reason (D-18).", nameof(reason));
        }

        Status = SubscriptionStatus.Extended;
        ExtendedByUserId = extendedByUserId;
        ExtendedReason = reason;
        ExtendedTo = extendedTo;
        ExpiresOn = extendedTo;
    }

    /// <summary>Recomputes ExpiresOn from freeze totals (§19.3's formula). Called
    /// by SubscriptionFreezeService whenever a freeze is added/lifted — never
    /// set ExpiresOn any other way except Extend() above.</summary>
    public void RecalculateExpiryFromFreezeDays(int totalFreezeDays)
    {
        if (Status == SubscriptionStatus.Extended)
        {
            // An exceptional extension is a human override — freezes don't recompute over it.
            return;
        }

        ExpiresOn = StartsOn.PlusDays(ValidityDays + totalFreezeDays);
    }
}
