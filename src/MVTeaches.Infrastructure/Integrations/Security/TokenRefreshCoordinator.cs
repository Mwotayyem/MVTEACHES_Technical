using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MVTeaches.Application.Integrations;
using MVTeaches.Domain.Integrations;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;

namespace MVTeaches.Infrastructure.Integrations.Security;

/// <summary>
/// Owner clarification (2026-08-29): "Implement refresh-token rotation
/// atomically. Concurrent requests for one connection must not reuse an
/// invalidated refresh token, overwrite a newer token, or provision
/// duplicate meetings." The atomicity here is a single conditional UPDATE
/// keyed on the exact encrypted refresh token this coordinator read — the
/// same read-then-conditional-write idiom the rest of the codebase already
/// uses for concurrency (ClassSession.TryTakeSeat, the package-limit
/// FOR-UPDATE check), not a database transaction held open across the
/// outbound HTTP refresh call.
/// </summary>
public class TokenRefreshCoordinator
{
    private static readonly Duration ExpiryLeeway = Duration.FromSeconds(60);

    private readonly MvTeachesDbContext _db;
    private readonly ITokenProtector _protector;
    private readonly IClock _clock;
    private readonly ILogger<TokenRefreshCoordinator> _logger;

    public TokenRefreshCoordinator(MvTeachesDbContext db, ITokenProtector protector, IClock clock,
        ILogger<TokenRefreshCoordinator> logger)
    {
        _db = db;
        _protector = protector;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Returns a plaintext, currently-valid access token for this
    /// connection, refreshing (and durably persisting the rotated tokens)
    /// first if the stored one is expired or about to expire. Returns null
    /// when refresh is impossible or fails — the caller must treat that as
    /// "this connection needs to be reconnected", never fall back to another
    /// connection/provider.</summary>
    public async Task<string?> GetValidAccessTokenAsync(TeacherMeetingConnection connection,
        IVideoMeetingProviderClient client, CancellationToken cancellationToken)
    {
        var now = _clock.GetCurrentInstant();
        if (connection.AccessTokenExpiresAtUtc is null || connection.AccessTokenExpiresAtUtc.Value > now.Plus(ExpiryLeeway))
        {
            return _protector.Unprotect(connection.EncryptedAccessToken);
        }

        if (connection.EncryptedRefreshToken is null)
        {
            return null;
        }

        var refreshToken = _protector.Unprotect(connection.EncryptedRefreshToken);
        OAuthTokenResult result;
        try
        {
            result = await client.RefreshAsync(refreshToken, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Refresh failed for {Provider} connection {ConnectionId}.", connection.Provider, connection.Id);
            return null;
        }

        var newEncryptedAccess = _protector.Protect(result.AccessToken);
        var newEncryptedRefresh = result.RefreshToken is not null ? _protector.Protect(result.RefreshToken) : null;

        // Only apply if the row's refresh token is STILL the exact one we
        // just used — otherwise a concurrent request already rotated it
        // (Zoom invalidates the old value on every use) and ours is stale.
        var previousEncryptedRefresh = connection.EncryptedRefreshToken;
        var rowsUpdated = await _db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE teacher_meeting_connections
            SET encrypted_access_token = {newEncryptedAccess},
                encrypted_refresh_token = COALESCE({newEncryptedRefresh}, encrypted_refresh_token),
                access_token_expires_at_utc = {result.ExpiresAtUtc},
                token_version = token_version + 1
            WHERE ""Id"" = {connection.Id} AND encrypted_refresh_token = {previousEncryptedRefresh}
        ", cancellationToken);

        if (rowsUpdated == 0)
        {
            // Lost the race — reload and use whatever the winner just wrote.
            await _db.Entry(connection).ReloadAsync(cancellationToken);
            return connection.AccessTokenExpiresAtUtc is not null && connection.AccessTokenExpiresAtUtc.Value > now
                ? _protector.Unprotect(connection.EncryptedAccessToken)
                : null;
        }

        connection.UpdateTokens(newEncryptedAccess, newEncryptedRefresh, result.ExpiresAtUtc);
        return result.AccessToken;
    }
}
