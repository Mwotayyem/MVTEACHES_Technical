namespace MVTeaches.Application.Attendance;

public enum JoinOutcome
{
    /// <summary>First press for this (session, student) — Present recorded,
    /// full duration consumed, in one transaction (D-83).</summary>
    Recorded,

    /// <summary>A later press for the same (session, student) — a deliberate,
    /// silent no-op. Not an error: D-83 requires the second press to change
    /// nothing, and the caller should show the same "you're marked present"
    /// state either way.</summary>
    AlreadyRecorded,

    /// <summary>The acting user is neither the student nor an active guardian
    /// of the student, or the student has no active enrollment in this session.</summary>
    Unauthorized,

    SessionNotFound,

    /// <summary>The session has not started yet. This is the one, minimal,
    /// uncontroversial precondition this service enforces — see the remarks on
    /// JoinAttendanceService for why no other time boundary (an admin-configurable
    /// "closing window") exists: D-83 explicitly forbids inventing one.</summary>
    SessionNotYetJoinable,

    /// <summary>§20.5 rule 5: entitlement is checked before writing. No
    /// subscription for this student/course/level carries enough remaining
    /// balance to cover the session's full duration in one draw.</summary>
    InsufficientBalance,
}

public record JoinAttendanceRequest(long SessionId, long StudentId, long ActingUserId);

public record JoinAttendanceResult(JoinOutcome Outcome, string? Detail = null)
{
    public bool IsPresent => Outcome is JoinOutcome.Recorded or JoinOutcome.AlreadyRecorded;
}

/// <summary>
/// The single entry point for D-83. Every "Join" button in the UI — the
/// student's own, or a guardian pressing on behalf of a child with no
/// independent login — calls this and nothing else. See the Infrastructure
/// implementation for the full transactional/concurrency contract.
/// </summary>
public interface IJoinAttendanceService
{
    Task<JoinAttendanceResult> JoinAsync(JoinAttendanceRequest request, CancellationToken cancellationToken);
}
