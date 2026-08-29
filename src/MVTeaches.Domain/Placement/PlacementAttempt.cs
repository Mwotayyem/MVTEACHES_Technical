using NodaTime;

namespace MVTeaches.Domain.Placement;

public enum PlacementAttemptStatus
{
    InProgress,
    Completed,
}

/// <summary>
/// One student's pass through a specific, immutable <see cref="PlacementTestVersion"/>.
/// Owner decision 2026-08-30 rule 3: "The first placement attempt is free. A
/// student cannot repeatedly retake the test to obtain a preferred level. A
/// retake requires explicit Admin approval. A new approved retake creates a
/// new attempt; it never edits the previous attempt." — this class is
/// append-only in spirit: once <see cref="Complete"/> runs, nothing on this
/// row or its <see cref="PlacementAttemptAnswer"/> children is ever mutated
/// again, so a later edit to the question bank can never rewrite a historical
/// score or level. The test version id is stored precisely so "which version
/// produced this result" survives even after new versions are published.
/// </summary>
public class PlacementAttempt
{
    public long Id { get; private set; }

    public long StudentId { get; private set; }
    public long TestVersionId { get; private set; }

    /// <summary>Null for the free first attempt. Set only when this attempt
    /// exists because an admin approved a <see cref="PlacementRetakeRequest"/> —
    /// never inferred, never settable by the student.</summary>
    public long? ApprovedRetakeRequestId { get; private set; }

    public long StartedByUserId { get; private set; }
    public Instant StartedAtUtc { get; private set; }
    public Instant? CompletedAtUtc { get; private set; }

    public PlacementAttemptStatus Status { get; private set; } = PlacementAttemptStatus.InProgress;

    public int? Score { get; private set; }
    public int? AssignedLevelId { get; private set; }

    private PlacementAttempt() { }

    public PlacementAttempt(long studentId, long testVersionId, long? approvedRetakeRequestId,
        long startedByUserId, Instant startedAtUtc)
    {
        StudentId = studentId;
        TestVersionId = testVersionId;
        ApprovedRetakeRequestId = approvedRetakeRequestId;
        StartedByUserId = startedByUserId;
        StartedAtUtc = startedAtUtc;
    }

    /// <summary>The one and only scoring write. <paramref name="score"/> and
    /// <paramref name="assignedLevelId"/> are computed server-side by
    /// IPlacementAttemptService from the student's submitted answers against
    /// this attempt's own TestVersionId's score ranges — never accepted as
    /// caller-supplied values, and never recomputed afterward.</summary>
    public void Complete(int score, int assignedLevelId, Instant completedAtUtc)
    {
        if (Status != PlacementAttemptStatus.InProgress)
        {
            throw new InvalidOperationException("This attempt has already been completed.");
        }

        Score = score;
        AssignedLevelId = assignedLevelId;
        CompletedAtUtc = completedAtUtc;
        Status = PlacementAttemptStatus.Completed;
    }
}
