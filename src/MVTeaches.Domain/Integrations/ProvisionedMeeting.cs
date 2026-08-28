using NodaTime;

namespace MVTeaches.Domain.Integrations;

/// <summary>
/// One external meeting instance for one ClassSession. Never modeled around
/// a single URL column on ClassSession itself — see the owner clarification
/// (2026-08-29) — because a session's meeting must survive a default-provider
/// switch untouched, must be traceable to the exact connection that owns it
/// (for isolation/IDOR checks), and must keep a full history across a
/// teacher reassignment (<see cref="Supersede"/>).
///
/// At most one row per SessionId may have <see cref="IsActive"/> = true — see
/// ux_provisioned_meeting_active_session — which is what makes concurrent
/// provisioning attempts and retries produce at most one live external
/// meeting per session.
///
/// <see cref="JoinUrl"/> is the participant-facing URL only (Zoom's
/// join_url / Google's meetingUri) — it grants no host authority and is safe
/// to persist. Zoom's host-only start_url is deliberately NOT a field here;
/// it is fetched live, once, only when the assigned teacher presses "Start
/// session" (see IMeetingProvisioningService.GetHostStartUrlAsync).
/// </summary>
public class ProvisionedMeeting
{
    public long Id { get; private set; }
    public long SessionId { get; private set; }
    public long ConnectionId { get; private set; }
    public VideoProviderType Provider { get; private set; }

    public string? ExternalMeetingId { get; private set; }
    public string? JoinUrl { get; private set; }

    public MeetingProvisioningStatus Status { get; private set; }
    public string? StatusDetail { get; private set; }

    public bool IsActive { get; private set; } = true;
    public long? SupersededByMeetingId { get; private set; }

    /// <summary>Claim fields make "at most one active external meeting per
    /// session, even under concurrent/retried provisioning" possible without
    /// holding a database row lock across a slow outbound HTTP call — see
    /// MeetingProvisioningService for the actual conditional UPDATE.</summary>
    public Instant? ClaimedAtUtc { get; private set; }
    public Guid? ClaimToken { get; private set; }

    public Instant? ProvisionedAtUtc { get; private set; }
    public Instant CreatedAtUtc { get; private set; }

    private ProvisionedMeeting() { }

    public ProvisionedMeeting(long sessionId, long connectionId, VideoProviderType provider, Instant nowUtc)
    {
        SessionId = sessionId;
        ConnectionId = connectionId;
        Provider = provider;
        Status = MeetingProvisioningStatus.Provisioning;
        ClaimedAtUtc = nowUtc;
        ClaimToken = Guid.NewGuid();
        CreatedAtUtc = nowUtc;
    }

    public void MarkReady(string externalMeetingId, string? joinUrl, Instant nowUtc)
    {
        ExternalMeetingId = externalMeetingId;
        JoinUrl = joinUrl;
        Status = MeetingProvisioningStatus.Ready;
        StatusDetail = null;
        ProvisionedAtUtc = nowUtc;
    }

    public void MarkFailed(string detail)
    {
        Status = MeetingProvisioningStatus.Failed;
        StatusDetail = detail;
    }

    public void MarkCapabilityBlocked(string detail)
    {
        Status = MeetingProvisioningStatus.CapabilityBlocked;
        StatusDetail = detail;
    }

    public void MarkDisconnected(string detail)
    {
        Status = MeetingProvisioningStatus.Disconnected;
        StatusDetail = detail;
    }

    public void MarkOrphaned(string detail)
    {
        Status = MeetingProvisioningStatus.Orphaned;
        StatusDetail = detail;
        IsActive = false;
    }

    public void MarkCancelled(string reason)
    {
        Status = MeetingProvisioningStatus.Cancelled;
        StatusDetail = reason;
        IsActive = false;
    }

    /// <summary>The old row keeps its last real status (audit: "what was
    /// true right before it was replaced") and only stops being the active
    /// row for its session.</summary>
    public void Supersede(long newMeetingId)
    {
        IsActive = false;
        SupersededByMeetingId = newMeetingId;
    }
}
