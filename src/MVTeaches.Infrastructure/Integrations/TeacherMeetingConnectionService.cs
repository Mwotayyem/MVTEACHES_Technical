using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Integrations;
using MVTeaches.Domain.Integrations;
using MVTeaches.Infrastructure.Integrations.Security;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;

namespace MVTeaches.Infrastructure.Integrations;

/// <inheritdoc cref="ITeacherMeetingConnectionService"/>
public class TeacherMeetingConnectionService : ITeacherMeetingConnectionService
{
    private static readonly Duration StateTtl = Duration.FromMinutes(10);

    private readonly MvTeachesDbContext _db;
    private readonly IEnumerable<IVideoMeetingProviderClient> _clients;
    private readonly ITokenProtector _protector;
    private readonly IClock _clock;

    public TeacherMeetingConnectionService(MvTeachesDbContext db, IEnumerable<IVideoMeetingProviderClient> clients,
        ITokenProtector protector, IClock clock)
    {
        _db = db;
        _clients = clients;
        _protector = protector;
        _clock = clock;
    }

    public async Task<IReadOnlyList<ConnectionSummary>> GetConnectionsAsync(long teacherId, CancellationToken cancellationToken)
    {
        var rows = await _db.TeacherMeetingConnections.Where(c => c.TeacherId == teacherId).ToListAsync(cancellationToken);
        return rows.Select(c => new ConnectionSummary(c.Provider, c.ExternalAccountEmail, c.CapabilityTier,
            c.CapabilityMinutesLimit, c.CapabilityVerifiedAtUtc, c.Status, c.IsDefault, c.ConnectedAtUtc)).ToList();
    }

    public async Task<bool> IsReadyForOnlineSessionsAsync(long teacherId, CancellationToken cancellationToken) =>
        await _db.TeacherMeetingConnections.AnyAsync(
            c => c.TeacherId == teacherId && c.Status == ProviderConnectionStatus.Connected, cancellationToken);

    public async Task<BeginConnectResult> BeginConnectAsync(long teacherId, VideoProviderType provider, string redirectUri, CancellationToken cancellationToken)
    {
        var client = ClientFor(provider);
        if (!client.IsConfigured)
        {
            return new BeginConnectResult(BeginConnectOutcome.ProviderNotConfigured);
        }

        var state = PkceHelper.NewState();
        var verifier = PkceHelper.NewCodeVerifier();
        var challenge = PkceHelper.ComputeCodeChallenge(verifier);
        var now = _clock.GetCurrentInstant();

        _db.OAuthAuthorizationStates.Add(new OAuthAuthorizationState(provider, teacherId, state, verifier, now, StateTtl));
        await _db.SaveChangesAsync(cancellationToken);

        var url = client.BuildAuthorizationUrl(state, challenge, EffectiveRedirectUri(client, redirectUri));
        return new BeginConnectResult(BeginConnectOutcome.Started, url);
    }

    public async Task<CompleteConnectResult> CompleteConnectAsync(VideoProviderType provider, string stateToken, string code,
        long authenticatedTeacherId, string redirectUri, CancellationToken cancellationToken)
    {
        var client = ClientFor(provider);
        if (!client.IsConfigured)
        {
            return new CompleteConnectResult(CompleteConnectOutcome.ProviderNotConfigured);
        }

        var now = _clock.GetCurrentInstant();
        var stateRow = await _db.OAuthAuthorizationStates.FirstOrDefaultAsync(
            s => s.StateToken == stateToken && s.Provider == provider, cancellationToken);

        if (stateRow is null || !stateRow.IsUsable(now))
        {
            return new CompleteConnectResult(CompleteConnectOutcome.InvalidOrExpiredState);
        }

        if (stateRow.TeacherId != authenticatedTeacherId)
        {
            // Burn it either way so it can never be replayed, including by
            // the rightful owner later — a mismatch here is either a bug in
            // the redirect or a forged callback, neither of which should
            // leave a still-usable state row behind.
            await ConsumeStateAsync(stateRow, now, cancellationToken);
            return new CompleteConnectResult(CompleteConnectOutcome.TeacherMismatch);
        }

        if (!await ConsumeStateAsync(stateRow, now, cancellationToken))
        {
            // Someone else (a duplicate/replayed callback request) already consumed it.
            return new CompleteConnectResult(CompleteConnectOutcome.InvalidOrExpiredState);
        }

        OAuthTokenResult token;
        try
        {
            token = await client.ExchangeCodeAsync(code, stateRow.CodeVerifier, EffectiveRedirectUri(client, redirectUri), cancellationToken);
        }
        catch (Exception)
        {
            return new CompleteConnectResult(CompleteConnectOutcome.ExchangeFailed);
        }

        ProviderAccountInfo accountInfo;
        try
        {
            accountInfo = await client.GetAccountInfoAsync(token.AccessToken, cancellationToken);
        }
        catch (Exception)
        {
            return new CompleteConnectResult(CompleteConnectOutcome.ExchangeFailed);
        }

        var encAccess = _protector.Protect(token.AccessToken);
        var encRefresh = token.RefreshToken is not null ? _protector.Protect(token.RefreshToken) : null;

        await UpsertConnectionAsync(authenticatedTeacherId, provider, accountInfo, encAccess, encRefresh, token.ExpiresAtUtc, now, cancellationToken);

        return new CompleteConnectResult(CompleteConnectOutcome.Connected);
    }

    public async Task<DisconnectResult> DisconnectAsync(long teacherId, VideoProviderType provider, CancellationToken cancellationToken)
    {
        var connection = await _db.TeacherMeetingConnections.FirstOrDefaultAsync(
            c => c.TeacherId == teacherId && c.Provider == provider, cancellationToken);
        if (connection is null)
        {
            return new DisconnectResult(DisconnectOutcome.NotFound);
        }

        var client = ClientFor(provider);
        if (client.IsConfigured)
        {
            try
            {
                var accessToken = _protector.Unprotect(connection.EncryptedAccessToken);
                await client.RevokeAsync(accessToken, cancellationToken);
            }
            catch
            {
                // Best-effort only — local disconnection below is not
                // conditional on remote revocation succeeding (owner's own
                // wording: "...and mark the connection disconnected").
            }
        }

        connection.Disconnect(_clock.GetCurrentInstant());
        await _db.SaveChangesAsync(cancellationToken);

        // Disconnecting clears IsDefault. If the teacher still has another
        // usable provider, it must become the default — otherwise they are
        // left "connected but with no default", and every future session
        // fails provisioning with NoProviderConnection for no visible reason.
        await EnsureSomeUsableDefaultAsync(teacherId, cancellationToken);
        return new DisconnectResult(DisconnectOutcome.Disconnected);
    }

    public async Task<SetDefaultProviderResult> SetDefaultProviderAsync(long teacherId, VideoProviderType provider, CancellationToken cancellationToken)
    {
        var target = await _db.TeacherMeetingConnections.FirstOrDefaultAsync(
            c => c.TeacherId == teacherId && c.Provider == provider && c.Status == ProviderConnectionStatus.Connected, cancellationToken);
        if (target is null)
        {
            return new SetDefaultProviderResult(SetDefaultProviderOutcome.NotConnected);
        }

        // Changing the default only steers FUTURE, not-yet-provisioned
        // sessions (MeetingProvisioningService reads IsDefault only when
        // creating a session's very first meeting) — no existing
        // ProvisionedMeeting row is touched by this call.
        var others = await _db.TeacherMeetingConnections
            .Where(c => c.TeacherId == teacherId && c.Id != target.Id)
            .ToListAsync(cancellationToken);
        foreach (var other in others)
        {
            other.ClearDefault();
        }

        target.MarkDefault();
        await _db.SaveChangesAsync(cancellationToken);
        return new SetDefaultProviderResult(SetDefaultProviderOutcome.Updated);
    }

    private async Task<bool> ConsumeStateAsync(OAuthAuthorizationState stateRow, Instant now, CancellationToken cancellationToken)
    {
        // "Id" is quoted deliberately — the PK column keeps EF's default
        // PascalCase name, so an unquoted `id` would not resolve.
        var rows = await _db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE oauth_authorization_states SET consumed_at_utc = {now}
            WHERE ""Id"" = {stateRow.Id} AND consumed_at_utc IS NULL
        ", cancellationToken);
        return rows == 1;
    }

    private async Task UpsertConnectionAsync(long teacherId, VideoProviderType provider, ProviderAccountInfo accountInfo,
        string encAccess, string? encRefresh, Instant? expiresAtUtc, Instant now, CancellationToken cancellationToken)
    {
        var existing = await _db.TeacherMeetingConnections.FirstOrDefaultAsync(
            c => c.TeacherId == teacherId && c.Provider == provider, cancellationToken);

        if (existing is null)
        {
            var connection = new TeacherMeetingConnection(teacherId, provider, accountInfo.ExternalAccountId,
                accountInfo.ExternalAccountEmail, encAccess, encRefresh, expiresAtUtc, now);
            connection.UpdateCapability(accountInfo.CapabilityTier, accountInfo.CapabilityMinutesLimit, now);

            // Become the default when the teacher has no USABLE default today
            // (their first-ever connection, or their only remaining one after
            // a disconnect). Deliberately not "first row ever created" — a
            // teacher whose only prior connection was revoked would otherwise
            // end up with no default at all and silently fail provisioning.
            if (!await HasUsableDefaultAsync(teacherId, cancellationToken))
            {
                connection.MarkDefault();
            }

            _db.TeacherMeetingConnections.Add(connection);

            try
            {
                await _db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (Microsoft.EntityFrameworkCore.DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // Lost a race to insert the same (teacher, provider) row —
                // fall through and reconnect onto the winner's row instead.
                _db.ChangeTracker.Clear();
                existing = await _db.TeacherMeetingConnections.FirstAsync(
                    c => c.TeacherId == teacherId && c.Provider == provider, cancellationToken);
            }
        }

        existing.Reconnect(accountInfo.ExternalAccountId, accountInfo.ExternalAccountEmail, encAccess, encRefresh, expiresAtUtc, now);
        existing.UpdateCapability(accountInfo.CapabilityTier, accountInfo.CapabilityMinutesLimit, now);
        // Reconnecting a row that had been disconnected (which cleared its
        // IsDefault) must not leave the teacher without any default.
        if (!existing.IsDefault && !await HasUsableDefaultAsync(teacherId, cancellationToken))
        {
            existing.MarkDefault();
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> HasUsableDefaultAsync(long teacherId, CancellationToken cancellationToken) =>
        await _db.TeacherMeetingConnections.AnyAsync(
            c => c.TeacherId == teacherId && c.IsDefault && c.Status == ProviderConnectionStatus.Connected, cancellationToken);

    /// <summary>Promotes any still-connected provider to default when the
    /// teacher has none. Deterministic (lowest id wins) so two concurrent
    /// disconnects cannot promote two different rows and collide on
    /// ux_teacher_meeting_connection_default.</summary>
    private async Task EnsureSomeUsableDefaultAsync(long teacherId, CancellationToken cancellationToken)
    {
        if (await HasUsableDefaultAsync(teacherId, cancellationToken))
        {
            return;
        }

        var candidate = await _db.TeacherMeetingConnections
            .Where(c => c.TeacherId == teacherId && c.Status == ProviderConnectionStatus.Connected)
            .OrderBy(c => c.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (candidate is null)
        {
            return;
        }

        candidate.MarkDefault();
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // A concurrent caller already promoted one — theirs stands.
            _db.ChangeTracker.Clear();
        }
    }

    private IVideoMeetingProviderClient ClientFor(VideoProviderType provider) => _clients.First(c => c.Provider == provider);

    /// <summary>Configuration wins over the request-derived URI. Both providers
    /// compare redirect_uri byte-for-byte against their registered value, and
    /// the exchange must present the SAME URI the authorization used — so
    /// this single helper is applied to both halves of the handshake.</summary>
    private static string EffectiveRedirectUri(IVideoMeetingProviderClient client, string requestDerived) =>
        string.IsNullOrWhiteSpace(client.ConfiguredRedirectUri) ? requestDerived : client.ConfiguredRedirectUri!;

    private static bool IsUniqueViolation(Microsoft.EntityFrameworkCore.DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: "23505" };
}
