namespace MVTeaches.Application.Scheduling;

public enum CancelSessionOutcome
{
    Cancelled,
    SessionNotFound,

    /// <summary>The session is already Cancelled/Completed/NotDelivered — only
    /// a Scheduled session can be cancelled (ClassSession.EnsureCancellable).</summary>
    NotCancellable,

    ReplacementSessionNotFound,
    ReplacementSessionIsTheSameSession,
}

/// <summary>
/// EnrollmentsMovedOrCancelled: students who had NOT yet consumed this session
/// (no AttendanceRecord) and were either cancelled outright (no replacement) or
/// successfully re-enrolled into the replacement session (D-20: the entitlement
/// transfers, no ledger movement — see EnrollmentsThatCouldNotBeMovedToReplacement
/// for the exception).
///
/// EnrollmentsLeftUntouchedBecauseAlreadyConsumed: students who had already
/// pressed Join on this session before it was cancelled. Their consumption is
/// D-83-final and is never touched here — per the Technical Study (§17.4/line
/// 1018), a session going wrong AFTER a student already joined it is the one
/// case that requires the admin's own case-by-case judgment (a separate,
/// explicit makeup-credit grant — see IMakeUpCreditService), never an automatic
/// side effect of cancelling the session.
///
/// EnrollmentsThatCouldNotBeMovedToReplacement: a replacement was given, but
/// re-enrolling that specific student into it failed (most commonly: the
/// replacement is full). The admin sees this count and resolves those students
/// individually — this service does not invent a further fallback.
/// </summary>
public record CancelSessionResult(
    CancelSessionOutcome Outcome,
    int EnrollmentsMovedOrCancelled = 0,
    int EnrollmentsLeftUntouchedBecauseAlreadyConsumed = 0,
    int EnrollmentsThatCouldNotBeMovedToReplacement = 0);

/// <summary>
/// Technical Study D-20 ("no double makeup" — a cancellation either moves the
/// enrollment to a direct replacement with no ledger movement, or leaves a
/// plain cancellation for IMakeUpCreditService to address separately, D-19).
/// This is the first place anywhere in the codebase that can actually put a
/// ClassSession into Cancelled state — ClassSession.Cancel/CancelAndReplace
/// have existed since an earlier pass but were never wired to anything.
/// </summary>
public interface ISessionCancellationService
{
    Task<CancelSessionResult> CancelAsync(long sessionId, string reason, long cancelledByUserId,
        long? replacementSessionId, CancellationToken cancellationToken);
}
