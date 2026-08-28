using NodaTime;

namespace MVTeaches.Domain.Scheduling;

public enum CompensationRequestStatus
{
    Pending,
    Approved,
    Rejected,
}

/// <summary>
/// Owner correction (student self-service booking model, 2026-08-28):
/// compensation for a missed session now starts as a STUDENT-submitted
/// request, not only an admin-initiated action — this entity is exactly and
/// only that request's own lifecycle (Pending → Approved or Rejected). The
/// actual granting mechanism is unchanged and NOT duplicated here:
/// approving a request calls the same
/// <c>IEnrollmentService.ApproveReplacementLessonAsync</c> that the
/// admin-direct "student joined, then had a problem" path already used,
/// which creates exactly one linked replacement <see cref="SessionEnrollment"/>
/// (via <see cref="SessionEnrollment.CompensatesForSessionId"/>) — never a
/// ledger entry, never a spendable credit, never a wallet.
///
/// A request may only be created for a session where
/// <c>AttendanceRecord.IsPresent == false</c> already exists for this
/// exact (session, student) — i.e. only after
/// <c>SessionFinalizationService</c> has actually finalized the no-show; a
/// session the centre cancelled never reaches that state at all (cancelled
/// sessions are never finalized), so a request can never be filed against
/// one.
/// </summary>
public class CompensationRequest
{
    public long Id { get; private set; }

    public long StudentId { get; private set; }
    public long OriginalSessionId { get; private set; }
    public string? Reason { get; private set; }

    public CompensationRequestStatus Status { get; private set; } = CompensationRequestStatus.Pending;
    public Instant RequestedAtUtc { get; private set; }

    /// <summary>Set only on Approve — the one specific future session, matching
    /// the student's level and with open capacity, the admin chose.</summary>
    public long? ReplacementSessionId { get; private set; }

    public string? RejectionReason { get; private set; }
    public long? ResolvedByUserId { get; private set; }
    public Instant? ResolvedAtUtc { get; private set; }

    private CompensationRequest() { }

    public CompensationRequest(long studentId, long originalSessionId, string? reason, Instant requestedAtUtc)
    {
        StudentId = studentId;
        OriginalSessionId = originalSessionId;
        Reason = reason;
        RequestedAtUtc = requestedAtUtc;
    }

    public void Approve(long replacementSessionId, long resolvedByUserId, Instant nowUtc)
    {
        if (Status != CompensationRequestStatus.Pending)
        {
            throw new InvalidOperationException($"Cannot approve a request already {Status}.");
        }

        Status = CompensationRequestStatus.Approved;
        ReplacementSessionId = replacementSessionId;
        ResolvedByUserId = resolvedByUserId;
        ResolvedAtUtc = nowUtc;
    }

    public void Reject(string reason, long resolvedByUserId, Instant nowUtc)
    {
        if (Status != CompensationRequestStatus.Pending)
        {
            throw new InvalidOperationException($"Cannot reject a request already {Status}.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A rejection requires a reason.", nameof(reason));
        }

        Status = CompensationRequestStatus.Rejected;
        RejectionReason = reason;
        ResolvedByUserId = resolvedByUserId;
        ResolvedAtUtc = nowUtc;
    }
}
