using NodaTime;

namespace MVTeaches.Domain.Placement;

public enum LevelAssignmentSource
{
    PlacementInterview,
    Promotion,
    AdminOverride,
    Migration,

    /// <summary>Owner decision 2026-08-30, reversing D-48 ("no student-submitted
    /// placement exam"): a level assigned automatically from a scored
    /// PlacementAttempt's score range. PlacementInterview (the teacher-judgment
    /// path D-48 introduced) is left in place, not removed — this is an
    /// additional source, not a replacement for it.</summary>
    PlacementTest,
}

public enum AssignedByRole
{
    Teacher,
    Admin,

    /// <summary>Owner decision 2026-08-30: a PlacementAttempt's score maps to
    /// a level automatically, by the scoring engine — no human judgment call
    /// is in that loop the way there is for a teacher's interview or an
    /// admin's override. AssignedByUserId still records who triggered the
    /// submission (the student or their guardian) for traceability; this
    /// value records that the LEVEL DECISION ITSELF was the system's, not theirs.</summary>
    System,
}

/// <summary>
/// Technical Study §10.3 (D-05). A full history, not a single mutable column —
/// the first row is never deleted or edited, only superseded by a new row with
/// <see cref="IsCurrent"/> = true.
///
/// <para>Owner decision 2026-09-04: a level belongs to a COURSE. A student who
/// is B2 in English may be A1 in Spanish, and the single global "current level"
/// this table used to enforce could not express that — it silently made every
/// second course inherit the first one's level. The partial unique index behind
/// <see cref="IsCurrent"/> is now on (StudentId, CourseId) rather than
/// (StudentId), so "one current level" still holds, but per course.</para>
/// </summary>
public class StudentLevel
{
    public long Id { get; private set; }

    public long StudentId { get; private set; }

    /// <summary>Owner decision 2026-09-04: which course this level is in.
    /// Backfilled for existing rows to the centre's original course, which was
    /// the only one that existed when they were written — so no historical row
    /// changes meaning.</summary>
    public long CourseId { get; private set; }

    public int LevelId { get; private set; }

    public long AssignedByUserId { get; private set; }
    public AssignedByRole AssignedByRole { get; private set; }
    public LevelAssignmentSource Source { get; private set; }
    public long? PlacementInterviewId { get; private set; }

    /// <summary>Mandatory when Source == AdminOverride.</summary>
    public string? Reason { get; private set; }

    public Instant EffectiveFromUtc { get; private set; }
    public bool IsCurrent { get; private set; } = true;

    private StudentLevel() { }

    public StudentLevel(long studentId, long courseId, int levelId, long assignedByUserId, AssignedByRole assignedByRole,
        LevelAssignmentSource source, long? placementInterviewId, string? reason, Instant effectiveFromUtc)
    {
        if (source == LevelAssignmentSource.AdminOverride && string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("An admin override requires a reason (§10.3).", nameof(reason));
        }

        StudentId = studentId;
        CourseId = courseId;
        LevelId = levelId;
        AssignedByUserId = assignedByUserId;
        AssignedByRole = assignedByRole;
        Source = source;
        PlacementInterviewId = placementInterviewId;
        Reason = reason;
        EffectiveFromUtc = effectiveFromUtc;
    }

    /// <summary>Called only on the PREVIOUS current row FOR THE SAME COURSE when
    /// a new one is inserted. Superseding across courses would be the bug this
    /// column exists to prevent.</summary>
    public void Supersede() => IsCurrent = false;
}
