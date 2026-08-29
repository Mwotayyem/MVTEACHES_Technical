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
    public Money Price { get; private set; } = null!;
    public long? PricingPlanId { get; private set; }

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
