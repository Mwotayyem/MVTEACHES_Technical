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

    NoProviderConnection,
    CapabilityBlocked,

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

public record ProvisionMeetingResult(ProvisionMeetingOutcome Outcome, string? JoinUrl = null,
    VideoProviderType? Provider = null, string? Detail = null);

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
