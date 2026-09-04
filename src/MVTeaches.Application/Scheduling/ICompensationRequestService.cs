namespace MVTeaches.Application.Scheduling;

public enum SubmitCompensationRequestOutcome
{
    Submitted,

    /// <summary>The acting user does not own this student account.</summary>
    Unauthorized,

    /// <summary>No AttendanceRecord with IsPresent == false exists for this
    /// exact (session, student) — either the student actually attended, was
    /// never enrolled, or (most likely) the session hasn't been finalized by
    /// SessionFinalizationService yet (it may not have ended, or the sweep
    /// hasn't run yet).</summary>
    NotANoShow,

    /// <summary>A Pending or Approved request already exists for this exact
    /// missed session (ux_compensation_request_open) — the real backstop is
    /// the database constraint, this is just the friendly pre-check.</summary>
    DuplicateRequest,
}

public record SubmitCompensationRequestResult(SubmitCompensationRequestOutcome Outcome, long? RequestId = null);

public enum ResolveCompensationRequestOutcome
{
    Approved,
    Rejected,

    RequestNotFound,

    /// <summary>The request is no longer Pending — already resolved by
    /// someone else (or the same admin, twice).</summary>
    RequestNotPending,

    // The remaining outcomes below all come straight from
    // IEnrollmentService.ApproveReplacementLessonAsync, which this service
    // calls internally to do the actual granting — never duplicated here.
    ReplacementSessionNotFound,
    ReplacementSessionCourseMismatch,
    ReplacementSessionIsTheSameSession,
    ReplacementSessionFull,
    AlreadyEnrolledInReplacementSession,
    NoApplicableAgeGroup,
    ReplacementSessionLevelMismatch,
    ReplacementSessionNotInFuture,
}

public record ResolveCompensationRequestResult(ResolveCompensationRequestOutcome Outcome);

/// <summary>
/// Owner correction (student self-service booking, 2026-08-28): a student
/// requests their own replacement lesson for a session they were finalized
/// Absent/NoShow on; the admin then approves (choosing one specific future
/// session matching the student's level with open capacity) or rejects. This
/// is deliberately thin — <see cref="ApproveAsync"/> does not duplicate
/// IEnrollmentService.ApproveReplacementLessonAsync's own granting logic
/// (level match, future check, atomic seat claim, the linked free-Join
/// enrollment); it calls it. This service's own job is exactly the request's
/// lifecycle and, on a successful approval, creating the durable
/// notification outbox item — never before the replacement is actually
/// confirmed.
/// </summary>
public interface ICompensationRequestService
{
    Task<SubmitCompensationRequestResult> RequestReplacementAsync(long studentId, long originalSessionId,
        string? reason, long actingUserId, CancellationToken cancellationToken);

    Task<ResolveCompensationRequestResult> ApproveAsync(long requestId, long replacementSessionId,
        long approvedByUserId, CancellationToken cancellationToken);

    Task<ResolveCompensationRequestResult> RejectAsync(long requestId, string reason, long rejectedByUserId,
        CancellationToken cancellationToken);
}
