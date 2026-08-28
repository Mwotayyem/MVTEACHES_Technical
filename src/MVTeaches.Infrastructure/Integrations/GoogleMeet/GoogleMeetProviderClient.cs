using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using MVTeaches.Application.Integrations;
using MVTeaches.Domain.Integrations;
using NodaTime;

namespace MVTeaches.Infrastructure.Integrations.GoogleMeet;

/// <summary>
/// A real teacher-authorized Google OAuth client against the Google Meet
/// REST API v2 (developers.google.com/workspace/meet/api, verified at the
/// time this was written). Scopes requested are the documented minimum:
/// <c>openid email</c> (identity only — no profile/contacts access) and
/// <c>https://www.googleapis.com/auth/meetings.space.created</c> (create/
/// read only the spaces this app itself created — not
/// meetings.space.readonly, which would grant broader access than needed).
///
/// Owner clarification (2026-08-29): NO paid Google Workspace subscription
/// is required or assumed — a normal free Google account is sufficient, and
/// this client never claims otherwise. Google exposes no reliable,
/// documented way for a third-party app to confirm a consumer account's
/// paid status, so <see cref="GetAccountInfoAsync"/> always reports
/// <see cref="MeetingCapabilityTier.Restricted"/> — the caller
/// (MeetingProvisioningService) applies the real free-tier duration rule
/// (24h one-to-one / 60min group) based on the application session's own
/// seat capacity, not a number from this class.
/// </summary>
public class GoogleMeetProviderClient : IVideoMeetingProviderClient
{
    private const string AuthorizeEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";
    private const string UserInfoEndpoint = "https://www.googleapis.com/oauth2/v3/userinfo";
    private const string MeetApiBase = "https://meet.googleapis.com/v2";

    private readonly HttpClient _http;
    private readonly GoogleOptions _options;

    public GoogleMeetProviderClient(HttpClient http, IOptions<GoogleOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public VideoProviderType Provider => VideoProviderType.GoogleMeet;

    public bool IsConfigured => _options.IsConfigured;

    public string? ConfiguredRedirectUri => _options.RedirectUri;

    public string BuildAuthorizationUrl(string state, string codeChallenge, string redirectUri)
    {
        EnsureConfigured();
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = _options.ClientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = "openid email https://www.googleapis.com/auth/meetings.space.created",
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            // A refresh token is only issued on first consent unless both of
            // these are set — required since MVTeaches must be able to
            // create meetings without the teacher present every time.
            ["access_type"] = "offline",
            ["prompt"] = "consent",
        };
        var parts = query.Where(kv => kv.Value is not null)
            .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!)}");
        return $"{AuthorizeEndpoint}?{string.Join("&", parts)}";
    }

    public async Task<OAuthTokenResult> ExchangeCodeAsync(string code, string codeVerifier, string redirectUri, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var form = new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId!,
            ["client_secret"] = _options.ClientSecret!,
            ["code"] = code,
            ["code_verifier"] = codeVerifier,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirectUri,
        };
        return await PostTokenRequestAsync(form, cancellationToken);
    }

    public async Task<OAuthTokenResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var form = new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId!,
            ["client_secret"] = _options.ClientSecret!,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token",
        };
        // Unlike Zoom, Google normally does NOT return a new refresh_token on
        // a plain refresh — TokenRefreshCoordinator must keep the existing
        // one when this comes back null.
        return await PostTokenRequestAsync(form, cancellationToken);
    }

    public async Task RevokeAsync(string token, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{RevokeEndpoint}?token={Uri.EscapeDataString(token)}");
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<ProviderAccountInfo> GetAccountInfoAsync(string accessToken, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        using var request = new HttpRequestMessage(HttpMethod.Get, UserInfoEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var info = await response.Content.ReadFromJsonAsync<GoogleUserInfoResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Google returned an empty userinfo response.");

        // Never Full — see this class's own remarks on why Google capability
        // can never be authoritatively upgraded from this client.
        return new ProviderAccountInfo(info.Sub, info.Email, MeetingCapabilityTier.Restricted, null);
    }

    public async Task<ProviderMeetingHandle> CreateMeetingAsync(string accessToken, ProviderMeetingRequest request, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        // Google Meet spaces carry no start-time/duration — the caller
        // (MeetingProvisioningService) must already have enforced the
        // free-tier duration rule against request.DurationMinutes/IsGroupCapable
        // BEFORE calling this; the space itself is created empty per Google's
        // own documented contract ("the input space can be empty").
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{MeetApiBase}/spaces")
        {
            Content = JsonContent.Create(new { }, options: JsonOptions),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _http.SendAsync(httpRequest, cancellationToken);
        response.EnsureSuccessStatusCode();

        var space = await response.Content.ReadFromJsonAsync<GoogleSpaceResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Google returned an empty space-creation response.");

        // The resource name ("spaces/xxx") is retained as the external id —
        // never only the human-typeable meetingCode, which Google documents
        // as reusable/expirable.
        return new ProviderMeetingHandle(space.Name, space.MeetingUri);
    }

    public async Task CancelMeetingAsync(string accessToken, string externalMeetingId, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        // The Meet API v2 has no space-deletion method as of this writing —
        // only spaces.endActiveConference, which ends any conference
        // currently in progress in this space. A stopped-using space is not
        // itself billable or exploitable, so "cancel" here means "stop
        // routing MVTeaches sessions to it and end anything live in it",
        // never a fabricated delete call.
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{MeetApiBase}/{externalMeetingId}:endActiveConference");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _http.SendAsync(request, cancellationToken);
        // 404/409 here just means there was nothing active to end — not a failure.
        if (response.StatusCode is not (System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Conflict))
        {
            response.EnsureSuccessStatusCode();
        }
    }

    public Task<string> GetHostStartUrlAsync(string accessToken, string externalMeetingId, CancellationToken cancellationToken)
    {
        // Google Meet has no separate host secret — organizer authority comes
        // from the connected Google identity being signed in when they open
        // the link, per the owner's own clarification. The Web layer already
        // has the participant JoinUrl from ProvisionedMeeting; this call
        // exists only so IMeetingProvisioningService can treat both
        // providers identically. Re-fetching the space just to re-return the
        // same meetingUri would be a wasted API call, so this simply throws
        // to signal "not applicable" — callers must branch on Provider before
        // invoking this for Google (see MeetingProvisioningService.GetHostStartUrlAsync).
        throw new NotSupportedException(
            "Google Meet has no distinct host URL — use the stored ProvisionedMeeting.JoinUrl for both teacher and student.");
    }

    private async Task<OAuthTokenResult> PostTokenRequestAsync(Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form),
        };
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var token = await response.Content.ReadFromJsonAsync<GoogleTokenResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Google returned an empty token response.");

        Instant? expiresAt = token.ExpiresIn.HasValue
            ? SystemClock.Instance.GetCurrentInstant().Plus(Duration.FromSeconds(token.ExpiresIn.Value))
            : null;

        return new OAuthTokenResult(token.AccessToken, token.RefreshToken, expiresAt);
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured)
        {
            throw new IntegrationNotConfiguredException("GoogleMeet", "no teacher-authorized OAuth client configured (GoogleOptions).");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private record GoogleTokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int? ExpiresIn);

    private record GoogleUserInfoResponse(
        [property: JsonPropertyName("sub")] string Sub,
        [property: JsonPropertyName("email")] string? Email);

    private record GoogleSpaceResponse(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("meetingUri")] string? MeetingUri,
        [property: JsonPropertyName("meetingCode")] string? MeetingCode);
}
