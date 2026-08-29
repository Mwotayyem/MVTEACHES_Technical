using MVTeaches.Domain.Integrations;

namespace MVTeaches.Application.Integrations;

public enum ProvisionMeetingOutcome
{
    Ready,

    /// <summary>Another request is already provisioning this exact session's
    /// meeting (or the provider call is still in flight) — the caller should
    /// show a "preparing your meeting, try again in a moment" state, never
    /// start a second external meeting.</summary>
    StillProvisioning,

    /// <summary>The teacher has no connected Zoom/Google account at all. This
    /// remains a genuine hard block per the 2026-08-30 owner decision — an
    /// unconnected teacher is "not ready for online sessions" and no meeting
    /// can be created. Contrast <see cref="ProvisionMeetingResult.CapabilityWarning"/>,
    /// which is only ever informational.</summary>
    NoProviderConnection,

    /// <summary>The connection that owns this session's existing meeting is
    /// no longer usable (disconnected/revoked). Deliberately NOT silently
    /// retried under a different connection/provider — see the owner
    /// clarification's "must not silently fall back to another account or
    /// provider".</summary>
    ProviderDisconnected,

    Failed,
    SessionNotFound,

    /// <summary>The session is Cancelled, NotDelivered, or already Completed —
    /// no meeting is created for it, and an existing one is not resurrected.</summary>
    SessionNotProvisionable,
}

/// <param name="CapabilityWarning">Owner decision 2026-08-30 (supersedes the
/// duration-blocking half of D-92): when the assigned teacher's own account
/// cannot cover this session's full scheduled duration — a Zoom Basic account
/// on a 60-minute session, a free Google account on a 90-minute group session —
/// the meeting is still created at the session's real scheduled duration and
/// this carries the human-readable warning to show the teacher. It NEVER
/// shortens the session, never changes the student's debit or the teacher's
/// pay, and never silently switches provider. Null when the account covers
/// the session, and never populated for students.</param>
public record ProvisionMeetingResult(ProvisionMeetingOutcome Outcome, string? JoinUrl = null,
    VideoProviderType? Provider = null, string? Detail = null, string? CapabilityWarning = null);

public enum TeacherReassignmentOutcome { Reassigned, SessionNotFound, SessionNotReassignable, NewTeacherOverlaps, NewTeacherNotReadyForOnlineSessions }

public record TeacherReassignmentResult(TeacherReassignmentOutcome Outcome, string? Detail = null);

/// <summary>
/// Owner clarification (2026-08-29). The single entry point that turns "this
/// session needs a meeting" into an actual external Zoom/Google Meet
/// instance, idempotently and concurrency-safely, using whichever provider
/// connection the assigned teacher has selected as default at the moment a
/// meeting is first created for that session — never re-derived afterward
/// from a later default-provider change.
/// </summary>
public interface IMeetingProvisioningService
{
    /// <summary>Called lazily from both the teacher's "Start session" action
    /// and the student's Join-meeting redirect — whichever happens first
    /// provisions the meeting; every subsequent call for the same session is
    /// a fast idempotent read once <see cref="ProvisionMeetingOutcome.Ready"/>
    /// is reached.</summary>
    Task<ProvisionMeetingResult> GetOrProvisionReadyMeetingAsync(long sessionId, CancellationToken cancellationToken);

    /// <summary>Zoom's short-lived host secret / Google's organizer URL,
    /// fetched fresh for this one redirect. <paramref name="requestingTeacherId"/>
    /// must be the session's own assigned teacher (re-checked here, not
    /// trusted from the caller) or this returns null.</summary>
    Task<string?> GetHostStartUrlAsync(long sessionId, long requestingTeacherId, CancellationToken cancellationToken);

    /// <summary>Owner decision 2026-08-30: the read-only, side-effect-free
    /// form of the capability check, so the teacher can be warned while
    /// scheduling and again on their session list BEFORE pressing Start —
    /// not only after a meeting has already been provisioned. Returns null
    /// when the teacher's default connection covers the session's full
    /// scheduled duration, or when there is no connection at all (that is a
    /// separate, harder "not ready" state, not a duration warning). Provisions
    /// nothing and never contacts the provider.</summary>
    Task<string?> GetCapabilityWarningAsync(long sessionId, CancellationToken cancellationToken);

    /// <summary>A future, unstarted, Scheduled session's teacher changed.
    /// Cancels the old meeting under the OLD teacher's owning connection
    /// where possible, provisions a fresh one lazily under the new teacher on
    /// next Start/Join, records an audit trail, and notifies enrolled
    /// students via the existing SessionCancelledOrMoved notification path.
    /// If the old connection was revoked and cleanup cannot be completed,
    /// the old meeting is flagged Orphaned for admin action instead — the
    /// new teacher is never linked to it.</summary>
    Task<TeacherReassignmentResult> ReassignTeacherAsync(long sessionId, long newTeacherId, long performedByUserId, CancellationToken cancellationToken);

    /// <summary>A centre cancellation/administrative reschedule — cancels the
    /// external meeting (best-effort) and marks it Cancelled. Never touches
    /// attendance/entitlement; that boundary is entirely separate (D-83).</summary>
    Task CancelForSessionAsync(long sessionId, string reason, CancellationToken cancellationToken);
}
