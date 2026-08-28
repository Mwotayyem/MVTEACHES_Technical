using MVTeaches.Domain.Integrations;
using NodaTime;

namespace MVTeaches.Application.Integrations;

public enum BeginConnectOutcome { Started, ProviderNotConfigured }

public record BeginConnectResult(BeginConnectOutcome Outcome, string? AuthorizationUrl = null);

public enum CompleteConnectOutcome { Connected, InvalidOrExpiredState, TeacherMismatch, ProviderNotConfigured, ExchangeFailed }

public record CompleteConnectResult(CompleteConnectOutcome Outcome, string? Detail = null);

public enum DisconnectOutcome { Disconnected, NotFound }

public record DisconnectResult(DisconnectOutcome Outcome);

public enum SetDefaultProviderOutcome { Updated, NotConnected }

public record SetDefaultProviderResult(SetDefaultProviderOutcome Outcome);

public record ConnectionSummary(VideoProviderType Provider, string? ExternalAccountEmail,
    MeetingCapabilityTier CapabilityTier, int? CapabilityMinutesLimit, Instant? CapabilityVerifiedAtUtc,
    ProviderConnectionStatus Status, bool IsDefault, Instant ConnectedAtUtc);

/// <summary>
/// Owner clarification (2026-08-29). The whole OAuth handshake (state/PKCE
/// issuance, code exchange, token encryption, capability lookup) lives
/// behind this one seam so the Teacher portal's Connections page and its
/// two provider callback endpoints stay thin and identical in shape for
/// both providers.
/// </summary>
public interface ITeacherMeetingConnectionService
{
    Task<IReadOnlyList<ConnectionSummary>> GetConnectionsAsync(long teacherId, CancellationToken cancellationToken);

    /// <summary>Issues a fresh, single-use, teacher-bound OAuth state + PKCE
    /// verifier and returns the provider's authorization URL to redirect to.</summary>
    Task<BeginConnectResult> BeginConnectAsync(long teacherId, VideoProviderType provider, string redirectUri, CancellationToken cancellationToken);

    /// <summary>Called from the provider's redirect callback. <paramref name="authenticatedTeacherId"/>
    /// is resolved server-side from the currently signed-in account — never
    /// from a request parameter — and must match the state row's own
    /// TeacherId or the whole exchange is rejected (IDOR/CSRF guard).</summary>
    Task<CompleteConnectResult> CompleteConnectAsync(VideoProviderType provider, string stateToken, string code,
        long authenticatedTeacherId, string redirectUri, CancellationToken cancellationToken);

    Task<DisconnectResult> DisconnectAsync(long teacherId, VideoProviderType provider, CancellationToken cancellationToken);

    /// <summary>Affects only future, not-yet-provisioned sessions — see
    /// TeacherMeetingConnection.IsDefault's own remarks.</summary>
    Task<SetDefaultProviderResult> SetDefaultProviderAsync(long teacherId, VideoProviderType provider, CancellationToken cancellationToken);

    /// <summary>Owner clarification (2026-08-29): a teacher needs no paid
    /// subscription to either provider — a free Google account is enough —
    /// but they need AT LEAST ONE usable (Status == Connected) connection to
    /// be assignable to any online session at all. Used to gate recurring
    /// schedule creation/teacher reassignment and to show "Not ready for
    /// online sessions" on the Admin Teachers page.</summary>
    Task<bool> IsReadyForOnlineSessionsAsync(long teacherId, CancellationToken cancellationToken);
}
