using NodaTime;

namespace MVTeaches.Domain.Scheduling;

/// <summary>Technical Study §14.3. Non-contiguous availability is two rows, not
/// one row with a gap — "16:00-18:00 and 20:00-22:00" is naturally two rows.</summary>
public class TeacherAvailabilityRule
{
    public long Id { get; private set; }
    public long TeacherId { get; private set; }

    public IsoDayOfWeek DayOfWeek { get; private set; }
    public LocalTime StartLocal { get; private set; }
    public LocalTime EndLocal { get; private set; }

    /// <summary>The teacher's own IANA zone (§14.3).</summary>
    public string TimeZoneId { get; private set; } = string.Empty;

    public LocalDate ValidFrom { get; private set; }
    public LocalDate? ValidTo { get; private set; }

    private TeacherAvailabilityRule() { }

    public TeacherAvailabilityRule(long teacherId, IsoDayOfWeek dayOfWeek, LocalTime startLocal, LocalTime endLocal,
        string timeZoneId, LocalDate validFrom)
    {
        if (endLocal <= startLocal)
        {
            throw new ArgumentException("End must be after start.");
        }

        TeacherId = teacherId;
        DayOfWeek = dayOfWeek;
        StartLocal = startLocal;
        EndLocal = endLocal;
        TimeZoneId = timeZoneId;
        ValidFrom = validFrom;
    }
}

/// <summary>Technical Study §14.3 (Q-13) — leave/exceptions.</summary>
public class TeacherTimeOff
{
    public long Id { get; private set; }
    public long TeacherId { get; private set; }

    public Instant StartsAtUtc { get; private set; }
    public Instant EndsAtUtc { get; private set; }
    public string? Reason { get; private set; }
    public long CreatedByUserId { get; private set; }

    private TeacherTimeOff() { }

    public TeacherTimeOff(long teacherId, Instant startsAtUtc, Instant endsAtUtc, string? reason, long createdByUserId)
    {
        if (endsAtUtc <= startsAtUtc)
        {
            throw new ArgumentException("End must be after start.");
        }

        TeacherId = teacherId;
        StartsAtUtc = startsAtUtc;
        EndsAtUtc = endsAtUtc;
        Reason = reason;
        CreatedByUserId = createdByUserId;
    }
}
