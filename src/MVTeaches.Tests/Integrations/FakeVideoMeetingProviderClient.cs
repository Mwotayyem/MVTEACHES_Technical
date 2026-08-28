using MVTeaches.Application.Integrations;
using MVTeaches.Domain.Integrations;
using NodaTime;

namespace MVTeaches.Tests.Integrations;

/// <summary>
/// A controllable stand-in for one provider's REST client. This is NOT a
/// pretend Zoom/Google integration — the real clients
/// (ZoomVideoMeetingProviderClient/GoogleMeetProviderClient) are separate,
/// fully written, and deliberately untested here because testing them
/// without live credentials would mean asserting against a guessed API
/// shape. What these tests DO exercise for real, against real PostgreSQL,
/// is everything MVTeaches itself owns: ownership/IDOR, OAuth state
/// lifecycle, token encryption, refresh-rotation concurrency, capability
/// enforcement, idempotent/concurrent provisioning, reassignment cleanup,
/// and host-link secrecy.
/// </summary>
public class FakeVideoMeetingProviderClient : IVideoMeetingProviderClient
{
    private int _createdCount;

    public FakeVideoMeetingProviderClient(VideoProviderType provider) => Provider = provider;

    public VideoProviderType Provider { get; }

    public bool IsConfigured { get; set; } = true;

    public string? ConfiguredRedirectUri { get; set; }

    public MeetingCapabilityTier CapabilityTier { get; set; } = MeetingCapabilityTier.Full;
    public int? CapabilityMinutesLimit { get; set; }
    public string ExternalAccountId { get; set; } = "acct-1";
    public string? ExternalAccountEmail { get; set; } = "teacher@example.test";

    public string AccessTokenToReturn { get; set; } = "access-token-1";
    public string? RefreshTokenToReturn { get; set; } = "refresh-token-1";
    public Instant? ExpiresAtToReturn { get; set; }

    public bool ThrowOnExchange { get; set; }
    public bool ThrowOnCreate { get; set; }
    public bool ThrowOnCancel { get; set; }

    /// <summary>How many external meetings this client was actually asked to
    /// create — the assertion that idempotent/concurrent provisioning really
    /// produced at most one.</summary>
    public int CreatedCount => _createdCount;

    public int CancelledCount { get; private set; }
    public List<string> RefreshTokensSeen { get; } = new();
    public string HostStartUrl { get; set; } = "https://provider.test/start?secret=HOSTONLY";

    public string BuildAuthorizationUrl(string state, string codeChallenge, string redirectUri) =>
        $"https://provider.test/authorize?state={state}&code_challenge={codeChallenge}&redirect_uri={Uri.EscapeDataString(redirectUri)}";

    public Task<OAuthTokenResult> ExchangeCodeAsync(string code, string codeVerifier, string redirectUri, CancellationToken cancellationToken)
    {
        if (ThrowOnExchange)
        {
            throw new InvalidOperationException("exchange rejected");
        }

        return Task.FromResult(new OAuthTokenResult(AccessTokenToReturn, RefreshTokenToReturn, ExpiresAtToReturn));
    }

    public Task<OAuthTokenResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        lock (RefreshTokensSeen)
        {
            RefreshTokensSeen.Add(refreshToken);
        }

        return Task.FromResult(new OAuthTokenResult(AccessTokenToReturn, RefreshTokenToReturn, ExpiresAtToReturn));
    }

    public Task RevokeAsync(string token, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<ProviderAccountInfo> GetAccountInfoAsync(string accessToken, CancellationToken cancellationToken) =>
        Task.FromResult(new ProviderAccountInfo(ExternalAccountId, ExternalAccountEmail, CapabilityTier, CapabilityMinutesLimit));

    public Task<ProviderMeetingHandle> CreateMeetingAsync(string accessToken, ProviderMeetingRequest request, CancellationToken cancellationToken)
    {
        if (ThrowOnCreate)
        {
            throw new InvalidOperationException("provider rejected the meeting");
        }

        var n = Interlocked.Increment(ref _createdCount);
        return Task.FromResult(new ProviderMeetingHandle($"ext-{Provider}-{request.SessionId}-{n}",
            $"https://provider.test/join/{request.SessionId}/{n}"));
    }

    public Task CancelMeetingAsync(string accessToken, string externalMeetingId, CancellationToken cancellationToken)
    {
        if (ThrowOnCancel)
        {
            throw new InvalidOperationException("cancel failed");
        }

        CancelledCount++;
        return Task.CompletedTask;
    }

    public Task<string> GetHostStartUrlAsync(string accessToken, string externalMeetingId, CancellationToken cancellationToken) =>
        Task.FromResult(HostStartUrl);
}

/// <summary>
/// A deterministic stand-in for ITokenProtector. It genuinely TRANSFORMS the
/// value (base64 of the reversed UTF-8 bytes) rather than merely tagging it,
/// so "the plaintext token must not appear in the stored column" is a real
/// assertion here and not one a pass-through would pass vacuously. The
/// production implementation's actual cryptography and key persistence are
/// covered separately by DataProtectionTokenProtectorTests.
/// </summary>
public class FakeTokenProtector : Application.Integrations.ITokenProtector
{
    public const string Prefix = "enc::";

    public string Protect(string plaintextToken)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(plaintextToken);
        Array.Reverse(bytes);
        return Prefix + Convert.ToBase64String(bytes);
    }

    /// <summary>A shared instance for seeding/asserting stored values in tests.</summary>
    public static readonly FakeTokenProtector Instance = new();

    public string Unprotect(string protectedToken)
    {
        if (!protectedToken.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Not a protected value.");
        }

        var bytes = Convert.FromBase64String(protectedToken[Prefix.Length..]);
        Array.Reverse(bytes);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }
}
