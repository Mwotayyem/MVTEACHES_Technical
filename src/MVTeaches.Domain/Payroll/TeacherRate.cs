using MVTeaches.Domain.Common;

namespace MVTeaches.Domain.Payroll;

public enum RateUnit
{
    PerHour,
    PerSession,
}

/// <summary>Technical Study §9.2 (D-27). Selection rule at delivery time: most
/// specific wins — (course+level+ageGroup) then (course+level) then (course)
/// then the teacher's default (all nulls) — see IPayrollRateResolver.</summary>
public class TeacherRate
{
    public long Id { get; private set; }
    public long TeacherId { get; private set; }

    public long? CourseId { get; private set; }
    public int? LevelId { get; private set; }
    public int? AgeGroupId { get; private set; }

    public Money Rate { get; private set; } = null!;
    public RateUnit Unit { get; private set; } = RateUnit.PerHour;

    public DateOnly EffectiveFrom { get; private set; }
    public DateOnly? EffectiveTo { get; private set; }
    public long CreatedByUserId { get; private set; }

    private TeacherRate() { }

    public TeacherRate(long teacherId, long? courseId, int? levelId, int? ageGroupId, Money rate,
        RateUnit unit, DateOnly effectiveFrom, long createdByUserId)
    {
        if (rate.Amount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rate));
        }

        TeacherId = teacherId;
        CourseId = courseId;
        LevelId = levelId;
        AgeGroupId = ageGroupId;
        Rate = rate;
        Unit = unit;
        EffectiveFrom = effectiveFrom;
        CreatedByUserId = createdByUserId;
    }

    public void Close(DateOnly effectiveTo)
    {
        if (effectiveTo <= EffectiveFrom)
        {
            throw new ArgumentOutOfRangeException(nameof(effectiveTo));
        }

        EffectiveTo = effectiveTo;
    }

    /// <summary>Specificity score used by the resolver's most-specific-wins rule.</summary>
    public int Specificity =>
        (CourseId is not null ? 1 : 0) + (LevelId is not null ? 1 : 0) + (AgeGroupId is not null ? 1 : 0);
}
