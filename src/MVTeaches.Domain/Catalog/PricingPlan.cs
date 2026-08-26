using NodaTime;
using MVTeaches.Domain.Common;

namespace MVTeaches.Domain.Catalog;

public enum SessionType
{
    Group,
    Private,
    Placement,
}

/// <summary>
/// Technical Study §23.2 (D-10, extended by D-53/D-64/D-86). Referred to
/// informally as "price_lists" in several decision rows (D-53, D-86) — this
/// class and its EF mapping to the `pricing_plans` table are the same concept;
/// the repository's own DDL under this name is authoritative.
///
/// A private lesson (D-86) is just another row here with SessionType.Private —
/// no separate purchase path, no separate table.
/// </summary>
public class PricingPlan
{
    public long Id { get; private set; }

    public int CountryId { get; private set; }
    public long CourseId { get; private set; }

    /// <summary>NULL = applies to every level.</summary>
    public int? LevelId { get; private set; }

    /// <summary>NULL = applies to every age group.</summary>
    public int? AgeGroupId { get; private set; }

    public SessionType SessionType { get; private set; } = SessionType.Group;

    public int SessionsCount { get; private set; }
    public int MinutesTotal { get; private set; }

    public Money Amount { get; private set; } = null!;

    /// <summary>D-64: validity is a field on the plan, admin-written, never a
    /// hardcoded default. There is intentionally no fallback constant.</summary>
    public int ValidityDays { get; private set; }

    public bool IsActive { get; private set; } = true;
    public LocalDate EffectiveFrom { get; private set; }
    public LocalDate? EffectiveTo { get; private set; }
    public long CreatedByUserId { get; private set; }

    private PricingPlan() { }

    public PricingPlan(int countryId, long courseId, int? levelId, int? ageGroupId,
        SessionType sessionType, int sessionsCount, int minutesTotal, Money amount,
        int validityDays, LocalDate effectiveFrom, long createdByUserId)
    {
        if (sessionsCount <= 0) throw new ArgumentOutOfRangeException(nameof(sessionsCount));
        if (minutesTotal <= 0) throw new ArgumentOutOfRangeException(nameof(minutesTotal));
        if (validityDays <= 0) throw new ArgumentOutOfRangeException(nameof(validityDays), "D-64: validity must be set explicitly by the admin.");
        if (amount.Amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));

        CountryId = countryId;
        CourseId = courseId;
        LevelId = levelId;
        AgeGroupId = ageGroupId;
        SessionType = sessionType;
        SessionsCount = sessionsCount;
        MinutesTotal = minutesTotal;
        Amount = amount;
        ValidityDays = validityDays;
        EffectiveFrom = effectiveFrom;
        CreatedByUserId = createdByUserId;
    }

    /// <summary>§23.3: plans are never UPDATEd once live — a price change closes
    /// this row (<see cref="EffectiveTo"/>) and a new plan row is created.</summary>
    public void CloseEffectiveness(LocalDate effectiveTo)
    {
        if (effectiveTo <= EffectiveFrom)
        {
            throw new ArgumentOutOfRangeException(nameof(effectiveTo));
        }

        EffectiveTo = effectiveTo;
        IsActive = false;
    }
}
