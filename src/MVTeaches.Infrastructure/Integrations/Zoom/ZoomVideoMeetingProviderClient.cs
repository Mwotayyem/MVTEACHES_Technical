using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MVTeaches.Application.Integrations;
using MVTeaches.Domain.Integrations;
using NodaTime;

namespace MVTeaches.Infrastructure.Integrations.Zoom;

/// <summary>
/// A real Zoom user-level OAuth client against Zoom's currently-published
/// REST API (endpoints verified against developers.zoom.us at the time this
/// was written) — NOT a Server-to-Server/account-wide integration. Every
/// call operates as "me": whichever teacher's access token is passed in.
///
/// Scopes requested at authorization time are the documented minimum for
/// this feature: <c>user:read:user</c> (capability detection) and
/// <c>meeting:write:meeting meeting:read:meeting</c> (create/cancel/refresh
/// a meeting) — never an account-admin or master-account scope.
///
/// Every call throws <see cref="IntegrationNotConfiguredException"/> when no
/// ClientId/ClientSecret/RedirectUri exists yet — this class is written and
/// ready, but "live Zoom integration" is not claimed verified until a real
/// Zoom OAuth app and real teacher accounts (one paid, one Basic) exercise
/// it end to end (see the launch checklist).
/// </summary>
public class ZoomVideoMeetingProviderClient : IVideoMeetingProviderClient
{
    private const string AuthorizeEndpoint = "https://zoom.us/oauth/authorize";
    private const string TokenEndpoint = "https://zoom.us/oauth/token";
    private const string RevokeEndpoint = "https://zoom.us/oauth/revoke";
    private const string ApiBase = "https://api.zoom.us/v2";

    /// <summary>Zoom's own published Basic-host limit (support.zoom.com
    /// KB0067966): "almost all meetings scheduled and hosted by Basic (free)
    /// users ... are limited to 40 minutes", explicitly including 1:1s.</summary>
    public const int ZoomBasicMinutesLimit = 40;

    private readonly HttpClient _http;
    private readonly ZoomOptions _options;
    private readonly ILogger<ZoomVideoMeetingProviderClient> _logger;

    public ZoomVideoMeetingProviderClient(HttpClient http, IOptions<ZoomOptions> options,
        ILogger<ZoomVideoMeetingProviderClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public VideoProviderType Provider => VideoProviderType.Zoom;

    public bool IsConfigured => _options.IsConfigured;

    public string? ConfiguredRedirectUri => _options.RedirectUri;

    public string BuildAuthorizationUrl(string state, string codeChallenge, string redirectUri)
    {
        EnsureConfigured();
        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = _options.ClientId,
            ["redirect_uri"] = redirectUri,
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            // Minimum user-level scopes only — never an account-admin scope.
            ["scope"] = "user:read:user meeting:write:meeting meeting:read:meeting",
        };
        return QueryHelpers(AuthorizeEndpoint, query);
    }

    public async Task<OAuthTokenResult> ExchangeCodeAsync(string code, string codeVerifier, string redirectUri, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier,
        };
        return await PostTokenRequestAsync(form, cancellationToken);
    }

    public async Task<OAuthTokenResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        };
        // Zoom rotates the refresh token on every use and invalidates the
        // old one — the returned RefreshToken here is always the new value
        // the caller must persist (TokenRefreshCoordinator).
        return await PostTokenRequestAsync(form, cancellationToken);
    }

    public async Task RevokeAsync(string token, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        using var request = new HttpRequestMessage(HttpMethod.Post, RevokeEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }),
        };
        request.Headers.Authorization = BasicAuthHeader();
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<ProviderAccountInfo> GetAccountInfoAsync(string accessToken, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/users/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var user = await response.Content.ReadFromJsonAsync<ZoomUserResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Zoom returned an empty user response.");

        // Zoom's documented user_type mapping: 1 = Basic, 2 = Licensed, 3 = On-prem.
        var (tier, minutes) = user.Type switch
        {
            2 or 3 => (MeetingCapabilityTier.Full, (int?)null),
            _ => (MeetingCapabilityTier.Restricted, (int?)ZoomBasicMinutesLimit),
        };

        return new ProviderAccountInfo(user.Id, user.Email, tier, minutes);
    }

    public async Task<ProviderMeetingHandle> CreateMeetingAsync(string accessToken, ProviderMeetingRequest request, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var body = new
        {
            topic = request.Topic,
            type = 2, // scheduled meeting
            start_time = request.StartsAtUtc.ToDateTimeUtc().ToString("yyyy-MM-ddTHH:mm:ssZ"),
            duration = request.DurationMinutes,
            timezone = "UTC",
            settings = new
            {
                join_before_host = false,
                waiting_room = true,
                approval_type = 2, // no registration required
            },
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/users/me/meetings")
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _http.SendAsync(httpRequest, cancellationToken);
        response.EnsureSuccessStatusCode();

        var created = await response.Content.ReadFromJsonAsync<ZoomMeetingResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Zoom returned an empty meeting-creation response.");

        return new ProviderMeetingHandle(created.Id.ToString(), created.JoinUrl);
    }

    public async Task CancelMeetingAsync(string accessToken, string externalMeetingId, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{ApiBase}/meetings/{externalMeetingId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _http.SendAsync(request, cancellationToken);
        // 404 = already gone on Zoom's side (e.g. the teacher deleted it
        // manually) — treat as a successful cancellation, not a failure.
        if (response.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    public async Task<string> GetHostStartUrlAsync(string accessToken, string externalMeetingId, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/meetings/{externalMeetingId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var meeting = await response.Content.ReadFromJsonAsync<ZoomMeetingResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Zoom returned an empty meeting response.");

        // A genuine host-only secret — the caller (Web layer) must redirect
        // to this immediately and never persist or log it.
        return meeting.StartUrl ?? throw new InvalidOperationException("Zoom did not return a start_url for this meeting.");
    }

    private async Task<OAuthTokenResult> PostTokenRequestAsync(Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Authorization = BasicAuthHeader();
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var token = await response.Content.ReadFromJsonAsync<ZoomTokenResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Zoom returned an empty token response.");

        Instant? expiresAt = token.ExpiresIn.HasValue
            ? SystemClock.Instance.GetCurrentInstant().Plus(Duration.FromSeconds(token.ExpiresIn.Value))
            : null;

        return new OAuthTokenResult(token.AccessToken, token.RefreshToken, expiresAt);
    }

    private AuthenticationHeaderValue BasicAuthHeader()
    {
        var raw = System.Text.Encoding.ASCII.GetBytes($"{_options.ClientId}:{_options.ClientSecret}");
        return new AuthenticationHeaderValue("Basic", Convert.ToBase64String(raw));
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured)
        {
            throw new IntegrationNotConfiguredException("Zoom", "no user-authorized OAuth app configured (ZoomOptions).");
        }
    }

    private static string QueryHelpers(string baseUrl, Dictionary<string, string?> query)
    {
        var parts = query.Where(kv => kv.Value is not null)
            .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!)}");
        return $"{baseUrl}?{string.Join("&", parts)}";
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private record ZoomTokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int? ExpiresIn);

    private record ZoomUserResponse(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("email")] string? Email,
        [property: JsonPropertyName("type")] int Type);

    private record ZoomMeetingResponse(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("join_url")] string? JoinUrl,
        [property: JsonPropertyName("start_url")] string? StartUrl);
}
