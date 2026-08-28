using MVTeaches.Domain.Integrations;
using NodaTime;

namespace MVTeaches.Application.Integrations;

/// <summary>
/// Provider-neutral boundary. Exactly one implementation exists per
/// <see cref="VideoProviderType"/> (Zoom, GoogleMeet) — resolved from
/// <c>IEnumerable&lt;IVideoMeetingProviderClient&gt;</c> filtered by
/// <see cref="Provider"/>, never by a type check. Every method takes an
/// already-decrypted access token; decrypting the connection's stored token
/// (and refreshing it first if expired) is TeacherMeetingConnectionService's
/// job, not this boundary's — implementations here must never see or log an
/// encrypted-token column, only the plaintext bearer value handed to them
/// for the single call, and must never log that value either.
///
/// Technical Study §28/§29 (D-83/D-59, extended by the 2026-08-29 owner
/// clarification): Zoom/Google are external video services only — nothing
/// either returns ever feeds attendance or billing.
/// </summary>
public interface IVideoMeetingProviderClient
{
    VideoProviderType Provider { get; }

    /// <summary>False when no ClientId/ClientSecret is configured for this
    /// provider — every method below throws <see cref="IntegrationNotConfiguredException"/>
    /// in that state, exactly like the pre-existing Zoom/WhatsApp boundaries.</summary>
    bool IsConfigured { get; }

    /// <summary>The redirect URI registered on the provider's own app, from
    /// configuration. Both providers match this string EXACTLY, so it must
    /// come from configuration rather than being rebuilt from the incoming
    /// request's Host header — behind a reverse proxy (or a forged Host)
    /// that would produce a URI the provider rejects. Null when unset; the
    /// caller then falls back to the request-derived value.</summary>
    string? ConfiguredRedirectUri { get; }

    string BuildAuthorizationUrl(string state, string codeChallenge, string redirectUri);

    Task<OAuthTokenResult> ExchangeCodeAsync(string code, string codeVerifier, string redirectUri, CancellationToken cancellationToken);

    Task<OAuthTokenResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken);

    /// <summary>Best-effort remote revocation. Callers must still mark the
    /// local connection disconnected even if this throws (the owner's "attempt
    /// remote revocation where supported, remove local credentials securely,
    /// and mark the connection disconnected" — local disconnection is not
    /// conditional on the remote call succeeding).</summary>
    Task RevokeAsync(string token, CancellationToken cancellationToken);

    Task<ProviderAccountInfo> GetAccountInfoAsync(string accessToken, CancellationToken cancellationToken);

    Task<ProviderMeetingHandle> CreateMeetingAsync(string accessToken, ProviderMeetingRequest request, CancellationToken cancellationToken);

    Task CancelMeetingAsync(string accessToken, string externalMeetingId, CancellationToken cancellationToken);

    /// <summary>Zoom: a genuine short-lived host-only secret, fetched fresh
    /// on every call — never cached or persisted by this method's caller.
    /// Google Meet: organizer authority comes from the connected Google
    /// identity, not a special link, so this simply returns the same
    /// participant URL. Either way the caller (Web layer) must redirect
    /// without rendering or logging the value.</summary>
    Task<string> GetHostStartUrlAsync(string accessToken, string externalMeetingId, CancellationToken cancellationToken);
}

public record OAuthTokenResult(string AccessToken, string? RefreshToken, Instant? ExpiresAtUtc);

public record ProviderAccountInfo(string ExternalAccountId, string? ExternalAccountEmail,
    MeetingCapabilityTier CapabilityTier, int? CapabilityMinutesLimit);

public record ProviderMeetingRequest(long SessionId, Instant StartsAtUtc, int DurationMinutes, bool IsGroupCapable, string Topic);

public record ProviderMeetingHandle(string ExternalMeetingId, string? JoinUrl);
