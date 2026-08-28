using NodaTime;

namespace MVTeaches.Domain.Integrations;

/// <summary>
/// Owner clarification (2026-08-29): "Protect authorization callbacks with
/// short-lived, single-use OAuth state, bound to the authenticated teacher
/// ... Reject missing, forged, expired, reused, or teacher-mismatched
/// state." One row is created when a teacher clicks "Connect Zoom"/"Connect
/// Google Meet" and consumed exactly once when the provider redirects back —
/// see ux_oauth_state_token (uniqueness) and <see cref="IsUsable"/> (expiry +
/// single-use) for the two halves of that guarantee; the caller separately
/// re-checks <see cref="TeacherId"/> against the currently authenticated
/// account (the "browser session" binding — the callback can only be
/// completed by the still-logged-in teacher who started it).
///
/// <see cref="CodeVerifier"/> is the PKCE verifier — plaintext is acceptable
/// here because it is useless without the matching single-use authorization
/// code AND the provider client secret, and the row is deleted/consumed
/// within minutes.
/// </summary>
public class OAuthAuthorizationState
{
    public long Id { get; private set; }
    public VideoProviderType Provider { get; private set; }
    public long TeacherId { get; private set; }
    public string StateToken { get; private set; } = string.Empty;
    public string CodeVerifier { get; private set; } = string.Empty;

    public Instant CreatedAtUtc { get; private set; }
    public Instant ExpiresAtUtc { get; private set; }
    public Instant? ConsumedAtUtc { get; private set; }

    private OAuthAuthorizationState() { }

    public OAuthAuthorizationState(VideoProviderType provider, long teacherId, string stateToken,
        string codeVerifier, Instant nowUtc, Duration ttl)
    {
        if (string.IsNullOrWhiteSpace(stateToken))
        {
            throw new ArgumentException("A state token is required.", nameof(stateToken));
        }

        Provider = provider;
        TeacherId = teacherId;
        StateToken = stateToken;
        CodeVerifier = codeVerifier;
        CreatedAtUtc = nowUtc;
        ExpiresAtUtc = nowUtc.Plus(ttl);
    }

    public bool IsUsable(Instant nowUtc) => ConsumedAtUtc is null && nowUtc <= ExpiresAtUtc;

    public void MarkConsumed(Instant nowUtc) => ConsumedAtUtc = nowUtc;
}
