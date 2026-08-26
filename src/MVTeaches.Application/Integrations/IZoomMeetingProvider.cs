namespace MVTeaches.Application.Integrations;

/// <summary>
/// Technical Study §28 (D-83/D-59). Zoom is an external video service only —
/// no in-platform conferencing UI, and critically: Zoom is NEVER a source of
/// truth for attendance or billing (see JoinAttendanceService's remarks). This
/// boundary exists purely to create/manage the meeting shell and hand back a
/// join URL; nothing it returns ever feeds a financial calculation.
/// </summary>
public interface IZoomMeetingProvider
{
    Task<ZoomMeetingHandle> CreateMeetingAsync(ZoomMeetingRequest request, CancellationToken cancellationToken);

    /// <summary>The short-lived join link surfaced to the student ~5 minutes
    /// before the session (§30.1's "رابط Zoom" event) — never persisted in a
    /// permanently-readable page (§7.3's security note on the guardian dashboard).</summary>
    Task<string> GetJoinUrlAsync(long sessionId, CancellationToken cancellationToken);

    Task CancelMeetingAsync(long sessionId, CancellationToken cancellationToken);
}

public record ZoomMeetingRequest(long SessionId, NodaTime.Instant StartsAtUtc, int DurationMinutes, string HostAccountKey);

public record ZoomMeetingHandle(long SessionId, string ProviderMeetingId, string HostStartUrl, string JoinUrl);
