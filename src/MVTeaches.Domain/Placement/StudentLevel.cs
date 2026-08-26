using NodaTime;

namespace MVTeaches.Domain.Placement;

public enum LevelAssignmentSource
{
    PlacementInterview,
    Promotion,
    AdminOverride,
    Migration,
}

public enum AssignedByRole
{
    Teacher,
    Admin,
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
