using NodaTime;

namespace MVTeaches.Domain.Placement;

public enum PlacementRetakeStatus
{
    Pending,
    Approved,
    Rejected,
}

/// <summary>
/// Owner decision 2026-08-30 rule 3: "A student cannot repeatedly retake the
/// test to obtain a preferred level. A retake requires explicit Admin
/// approval." One row per request; approving it authorizes exactly ONE new
/// <see cref="PlacementAttempt"/> (see <see cref="MarkConsumed"/>) — a single
/// approval can never be reused to spawn a second attempt, so getting another
/// retake after that means a brand new request and a brand new approval.
/// </summary>
public class PlacementRetakeRequest
{
    public long Id { get; private set; }

    public long StudentId { get; private set; }
    public long RequestedByUserId { get; private set; }
    public Instant RequestedAtUtc { get; private set; }

    public PlacementRetakeStatus Status { get; private set; } = PlacementRetakeStatus.Pending;
    public long? DecidedByUserId { get; private set; }
    public Instant? DecidedAtUtc { get; private set; }
    public string? DecisionReason { get; private set; }

    /// <summary>Set the moment the approved retake is actually used to start a
    /// new attempt — makes this approval single-use.</summary>
    public long? ConsumedByAttemptId { get; private set; }

    private PlacementRetakeRequest() { }

    public PlacementRetakeRequest(long studentId, long requestedByUserId, Instant requestedAtUtc)
    {
        StudentId = studentId;
        RequestedByUserId = requestedByUserId;
        RequestedAtUtc = requestedAtUtc;
    }

    public void Approve(long decidedByUserId, string? reason, Instant nowUtc)
    {
        if (Status != PlacementRetakeStatus.Pending)
        {
            throw new InvalidOperationException($"Cannot approve a retake request already {Status}.");
        }

        Status = PlacementRetakeStatus.Approved;
        DecidedByUserId = decidedByUserId;
        DecisionReason = reason;
        DecidedAtUtc = nowUtc;
    }

    /// <summary>A reason is mandatory for a rejection — the student is told
    /// why, and it is audit-logged by the caller.</summary>
    public void Reject(long decidedByUserId, string reason, Instant nowUtc)
    {
        if (Status != PlacementRetakeStatus.Pending)
        {
            throw new InvalidOperationException($"Cannot reject a retake request already {Status}.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A rejection requires a reason.", nameof(reason));
        }

        Status = PlacementRetakeStatus.Rejected;
        DecidedByUserId = decidedByUserId;
        DecisionReason = reason;
        DecidedAtUtc = nowUtc;
    }

    public void MarkConsumed(long attemptId)
    {
        if (Status != PlacementRetakeStatus.Approved)
        {
            throw new InvalidOperationException("Only an approved retake request can be consumed.");
        }

        if (ConsumedByAttemptId is not null)
        {
            throw new InvalidOperationException("This approved retake has already been used for a new attempt.");
        }

        ConsumedByAttemptId = attemptId;
    }
}
