using MVTeaches.Domain.Catalog;
using NodaTime;

namespace MVTeaches.Domain.Scheduling;

public enum ClassSessionStatus
{
    Scheduled,
    Completed,
    Cancelled,
    NotDelivered,
}

/// <summary>
/// Technical Study §14.2/§14.4. UTC is the source of truth for every
/// calculation; the local wall-clock time and IANA zone are stored purely so
/// the admin's original scheduling *intent* ("17:00 Amman time") survives a
/// future DST rule change and can be recomputed (§14.4 rule 3).
///
/// The no-overlap guarantee for a teacher (no_teacher_overlap) is a database
/// EXCLUDE constraint (see Infrastructure migrations) — this entity does not
/// and must not attempt to re-implement that guarantee in C#; the database is
/// the only thing that can make it physically impossible under concurrency.
/// </summary>
public class ClassSession
{
    public long Id { get; private set; }

    public int CountryId { get; private set; }
    public long? RecurringScheduleId { get; private set; }
    public long CourseId { get; private set; }
    public int LevelId { get; private set; }
    public int AgeGroupId { get; private set; }
    public long TeacherId { get; private set; }

    public Instant StartsAtUtc { get; private set; }
    public Instant EndsAtUtc { get; private set; }

    /// <summary>D-37: a session is not necessarily an hour.</summary>
    public int DurationMinutes { get; private set; }

    /// <summary>IANA id the schedule was defined in.</summary>
    public string ScheduleTimeZone { get; private set; } = string.Empty;

    /// <summary>The admin's intended local time, e.g. "17:00" — display/audit only.</summary>
    public string LocalStartText { get; private set; } = string.Empty;

    public SessionType SessionType { get; private set; }

    /// <summary>Owner decision 2026-08-30: derived from <see cref="SessionType"/>
    /// via <see cref="CapacityFor"/>, never supplied by a caller. See that
    /// method for why.</summary>
    public int Capacity { get; private set; }

    public int SeatsTaken { get; private set; }

    public ClassSessionStatus Status { get; private set; } = ClassSessionStatus.Scheduled;

    public string? CancelReason { get; private set; }
    public long? CancelledByUserId { get; private set; }

    /// <summary>D-20: the durable proof a session was moved, not merely cancelled.
    /// No ledger entry is ever produced for this transfer — see EntitlementLedgerEntry.</summary>
    public long? ReplacedBySessionId { get; private set; }

    public Instant CreatedAtUtc { get; private set; }

    private ClassSession() { }

    /// <summary>
    /// Owner decision 2026-08-30: seat count is a property of the session's
    /// TYPE and is fixed by the centre — a group session seats exactly 4 and a
    /// private one exactly 1. It is deliberately not a parameter anywhere in
    /// the stack, so no UI field, request payload, or caller can widen or
    /// narrow it. Placement interviews are one-to-one like Private.
    /// </summary>
    public static int CapacityFor(SessionType sessionType) => sessionType switch
    {
        SessionType.Group => 4,
        SessionType.Private => 1,
        SessionType.Placement => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(sessionType), sessionType, "Unknown session type."),
    };

    public ClassSession(int countryId, long? recurringScheduleId, long courseId, int levelId, int ageGroupId,
        long teacherId, Instant startsAtUtc, Instant endsAtUtc, string scheduleTimeZone, string localStartText,
        SessionType sessionType, Instant createdAtUtc)
    {
        if (endsAtUtc <= startsAtUtc)
        {
            throw new ArgumentException("A session must end after it starts.");
        }

        var capacity = CapacityFor(sessionType);

        CountryId = countryId;
        RecurringScheduleId = recurringScheduleId;
        CourseId = courseId;
        LevelId = levelId;
        AgeGroupId = ageGroupId;
        TeacherId = teacherId;
        StartsAtUtc = startsAtUtc;
        EndsAtUtc = endsAtUtc;
        DurationMinutes = (int)(endsAtUtc - startsAtUtc).TotalMinutes;
        ScheduleTimeZone = scheduleTimeZone;
        LocalStartText = localStartText;
        SessionType = sessionType;
        Capacity = capacity;
        SeatsTaken = 0;
        CreatedAtUtc = createdAtUtc;
    }

    /// <summary>
    /// D-20: cancelling with a direct replacement produces NO ledger movement —
    /// entitlement is transferred with the enrollment, never reissued.
    /// </summary>
    public void CancelAndReplace(string reason, long cancelledByUserId, long replacementSessionId)
    {
        EnsureCancellable();
        Status = ClassSessionStatus.Cancelled;
        CancelReason = reason;
        CancelledByUserId = cancelledByUserId;
        ReplacedBySessionId = replacementSessionId;
    }

    /// <summary>A plain cancellation with no direct replacement — this is the
    /// only case where a MakeUpGranted ledger entry may later be issued (§17.4).</summary>
    public void Cancel(string reason, long cancelledByUserId)
    {
        EnsureCancellable();
        Status = ClassSessionStatus.Cancelled;
        CancelReason = reason;
        CancelledByUserId = cancelledByUserId;
    }

    /// <summary>Owner clarification (2026-08-29, video-meetings): a future,
    /// still-Scheduled session's teacher may change before it starts. The
    /// no_teacher_overlap EXCLUDE constraint still applies to the new
    /// teacher exactly as it would for any other session of theirs — a
    /// genuine overlap surfaces as a database constraint violation, not a
    /// check duplicated here. Meeting-ownership cleanup/reprovisioning is
    /// IMeetingProvisioningService's job, not this entity's.</summary>
    public void ReassignTeacher(long newTeacherId)
    {
        if (Status != ClassSessionStatus.Scheduled)
        {
            throw new InvalidOperationException($"Cannot reassign the teacher on a session that is already {Status}.");
        }

        TeacherId = newTeacherId;
    }

    public void MarkNotDelivered()
    {
        EnsureCancellable();
        Status = ClassSessionStatus.NotDelivered;
    }

    /// <summary>Set by a background sweep once the session's end time has passed
    /// with no cancellation/no-show recorded. Purely administrative bookkeeping —
    /// never a financial gate (D-83: consumption already happened at Join time).</summary>
    public void MarkCompleted()
    {
        if (Status != ClassSessionStatus.Scheduled)
        {
            return;
        }

        Status = ClassSessionStatus.Completed;
    }

    private void EnsureCancellable()
    {
        if (Status != ClassSessionStatus.Scheduled)
        {
            throw new InvalidOperationException($"Cannot change a session that is already {Status}.");
        }
    }

    /// <summary>
    /// §15.1's cheap, atomic seat guard. This must be issued as a single
    /// conditional UPDATE by the caller (Infrastructure/Application), e.g.:
    /// <c>UPDATE class_sessions SET seats_taken = seats_taken + 1
    ///     WHERE id = @id AND status = 'Scheduled' AND seats_taken &lt; capacity</c>
    /// — this method exists only to keep the in-memory entity consistent for
    /// callers that already hold a freshly-read row; it is NOT the concurrency
    /// guard itself.
    /// </summary>
    public bool TryTakeSeat()
    {
        if (Status != ClassSessionStatus.Scheduled || SeatsTaken >= Capacity)
        {
            return false;
        }

        SeatsTaken++;
        return true;
    }

    public void ReleaseSeat()
    {
        if (SeatsTaken > 0)
        {
            SeatsTaken--;
        }
    }
}
