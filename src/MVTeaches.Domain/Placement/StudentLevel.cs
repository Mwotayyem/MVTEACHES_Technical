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
/// <see cref="IsCurrent"/> = true (enforced by a partial unique index on
/// (StudentId) WHERE IsCurrent, in Infrastructure).
/// </summary>
public class StudentLevel
{
    public long Id { get; private set; }

    public long StudentId { get; private set; }
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

    public StudentLevel(long studentId, int levelId, long assignedByUserId, AssignedByRole assignedByRole,
        LevelAssignmentSource source, long? placementInterviewId, string? reason, Instant effectiveFromUtc)
    {
        if (source == LevelAssignmentSource.AdminOverride && string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("An admin override requires a reason (§10.3).", nameof(reason));
        }

        StudentId = studentId;
        LevelId = levelId;
        AssignedByUserId = assignedByUserId;
        AssignedByRole = assignedByRole;
        Source = source;
        PlacementInterviewId = placementInterviewId;
        Reason = reason;
        EffectiveFromUtc = effectiveFromUtc;
    }

    /// <summary>Called only on the PREVIOUS current row when a new one is inserted.</summary>
    public void Supersede() => IsCurrent = false;
}
