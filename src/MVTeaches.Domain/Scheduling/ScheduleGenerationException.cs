using NodaTime;

namespace MVTeaches.Domain.Scheduling;

public enum ScheduleConflictReason
{
    /// <summary>The database's own `no_teacher_overlap` EXCLUDE constraint
    /// (§14.2) rejected the occurrence — some other session already holds that
    /// teacher for an overlapping window.</summary>
    TeacherOverlap,

    /// <summary>The occurrence falls inside a <see cref="TeacherTimeOff"/>
    /// window — §15.3: "إجازة المعلم تُحجب الحصص المتصادمة وتُعرض على الأدمن".</summary>
    TeacherTimeOff,
}

/// <summary>
/// Technical Study §15.3: "إن رفض قيد EXCLUDE توليد حصة، تُسجَّل كـ Exception
/// للأدمن ولا تُتجاهل صامتة" — a skipped occurrence is never silently dropped.
/// This is the record the generator writes instead, for an admin screen to
/// surface later. It is deliberately a flat, single-purpose row: one
/// occurrence that could not be generated, why, and whether an admin has
/// looked at it — not a workflow, not a ticket system.
/// </summary>
public class ScheduleGenerationException
{
    public long Id { get; private set; }

    public long RecurringScheduleId { get; private set; }
    public LocalDate OccurrenceDate { get; private set; }
    public ScheduleConflictReason Reason { get; private set; }
    public string Detail { get; private set; } = string.Empty;
    public Instant DetectedAtUtc { get; private set; }

    public bool Resolved { get; private set; }
    public long? ResolvedByUserId { get; private set; }
    public Instant? ResolvedAtUtc { get; private set; }

    private ScheduleGenerationException() { }

    public ScheduleGenerationException(long recurringScheduleId, LocalDate occurrenceDate,
        ScheduleConflictReason reason, string detail, Instant detectedAtUtc)
    {
        RecurringScheduleId = recurringScheduleId;
        OccurrenceDate = occurrenceDate;
        Reason = reason;
        Detail = detail;
        DetectedAtUtc = detectedAtUtc;
    }

    /// <summary>An admin has seen this and dealt with it manually (rescheduled,
    /// adjusted the recurring schedule, or decided to ignore it) — this method
    /// only records that acknowledgement, it never re-attempts generation.</summary>
    public void Resolve(long resolvedByUserId, Instant nowUtc)
    {
        if (Resolved)
        {
            return;
        }

        Resolved = true;
        ResolvedByUserId = resolvedByUserId;
        ResolvedAtUtc = nowUtc;
    }
}
