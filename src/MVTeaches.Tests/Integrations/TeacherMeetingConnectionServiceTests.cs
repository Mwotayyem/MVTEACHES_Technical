using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Integrations;
using MVTeaches.Domain.Integrations;
using MVTeaches.Domain.People;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Integrations;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace MVTeaches.Tests.Integrations;

/// <summary>
/// Owner clarification (2026-08-29). Covers the OAuth half of the
/// provider-neutral video-meeting feature against real PostgreSQL:
/// state/CSRF expiry, one-time use, and teacher binding; encrypted token
/// persistence (and the absence of plaintext in the row); disconnect and
/// revocation; and the default-provider selection rule.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class TeacherMeetingConnectionServiceTests
{
    private readonly TestDatabaseFixture _fixture;
    private static long _idSeed = 90_000_000;

    public TeacherMeetingConnectionServiceTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static long NextId() => Interlocked.Increment(ref _idSeed);

    private const string RedirectUri = "https://mvteaches.test/oauth/zoom/callback";

    private static async Task<long> CreateTeacherAsync(MvTeachesDbContext db)
    {
        var user = new ApplicationUser
        {
            UserName = $"teacher-{Guid.NewGuid():N}",
            NormalizedUserName = $"TEACHER-{Guid.NewGuid():N}".ToUpperInvariant(),
            Email = $"{Guid.NewGuid():N}@test.mvteaches.local",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var teacher = new Teacher(user.Id, "Teacher " + NextId(), "Asia/Amman");
        db.Teachers.Add(teacher);
        await db.SaveChangesAsync();
        return teacher.Id;
    }

    private static (TeacherMeetingConnectionService Service, FakeVideoMeetingProviderClient Zoom, FakeVideoMeetingProviderClient Google)
        CreateService(MvTeachesDbContext db, Instant now)
    {
        var zoom = new FakeVideoMeetingProviderClient(VideoProviderType.Zoom);
        var google = new FakeVideoMeetingProviderClient(VideoProviderType.GoogleMeet);
        var service = new TeacherMeetingConnectionService(db, new IVideoMeetingProviderClient[] { zoom, google },
            new FakeTokenProtector(), new FakeClock(now));
        return (service, zoom, google);
    }

    [Fact]
    public async Task A_completed_connection_stores_only_encrypted_tokens_never_plaintext()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var teacherId = await CreateTeacherAsync(db);
        var (service, zoom, _) = CreateService(db, now);
        zoom.AccessTokenToReturn = "PLAINTEXT-ACCESS-SECRET";
        zoom.RefreshTokenToReturn = "PLAINTEXT-REFRESH-SECRET";

        await service.BeginConnectAsync(teacherId, VideoProviderType.Zoom, RedirectUri, CancellationToken.None);
        var state = await db.OAuthAuthorizationStates.OrderByDescending(s => s.Id).FirstAsync(s => s.TeacherId == teacherId);

        var result = await service.CompleteConnectAsync(VideoProviderType.Zoom, state.StateToken, "code-1", teacherId, RedirectUri, CancellationToken.None);

        Assert.Equal(CompleteConnectOutcome.Connected, result.Outcome);

        // Read the raw columns — not through the entity's own accessors — so
        // this genuinely asserts what is on disk.
        await using var raw = _fixture.CreateContext();
        var stored = await raw.TeacherMeetingConnections.AsNoTracking().FirstAsync(c => c.TeacherId == teacherId);
        Assert.DoesNotContain("PLAINTEXT-ACCESS-SECRET", stored.EncryptedAccessToken, StringComparison.Ordinal);
        Assert.DoesNotContain("PLAINTEXT-REFRESH-SECRET", stored.EncryptedRefreshToken!, StringComparison.Ordinal);
        Assert.StartsWith(FakeTokenProtector.Prefix, stored.EncryptedAccessToken, StringComparison.Ordinal);
        Assert.StartsWith(FakeTokenProtector.Prefix, stored.EncryptedRefreshToken!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_oauth_state_can_be_used_only_once()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var teacherId = await CreateTeacherAsync(db);
        var (service, _, _) = CreateService(db, now);

        await service.BeginConnectAsync(teacherId, VideoProviderType.Zoom, RedirectUri, CancellationToken.None);
        var state = await db.OAuthAuthorizationStates.OrderByDescending(s => s.Id).FirstAsync(s => s.TeacherId == teacherId);

        var first = await service.CompleteConnectAsync(VideoProviderType.Zoom, state.StateToken, "code-1", teacherId, RedirectUri, CancellationToken.None);
        var replay = await service.CompleteConnectAsync(VideoProviderType.Zoom, state.StateToken, "code-1", teacherId, RedirectUri, CancellationToken.None);

        Assert.Equal(CompleteConnectOutcome.Connected, first.Outcome);
        Assert.Equal(CompleteConnectOutcome.InvalidOrExpiredState, replay.Outcome);
    }

    [Fact]
    public async Task An_expired_oauth_state_is_rejected()
    {
        var issuedAt = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var teacherId = await CreateTeacherAsync(db);

        var (issuing, _, _) = CreateService(db, issuedAt);
        await issuing.BeginConnectAsync(teacherId, VideoProviderType.Zoom, RedirectUri, CancellationToken.None);
        var state = await db.OAuthAuthorizationStates.OrderByDescending(s => s.Id).FirstAsync(s => s.TeacherId == teacherId);

        // Same state token, but the callback arrives well past the 10-minute TTL.
        var (late, _, _) = CreateService(db, issuedAt.Plus(Duration.FromMinutes(30)));
        var result = await late.CompleteConnectAsync(VideoProviderType.Zoom, state.StateToken, "code-1", teacherId, RedirectUri, CancellationToken.None);

        Assert.Equal(CompleteConnectOutcome.InvalidOrExpiredState, result.Outcome);
        Assert.False(await db.TeacherMeetingConnections.AnyAsync(c => c.TeacherId == teacherId));
    }

    [Fact]
    public async Task A_state_issued_for_one_teacher_cannot_be_completed_by_another()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var victimTeacherId = await CreateTeacherAsync(db);
        var attackerTeacherId = await CreateTeacherAsync(db);
        var (service, _, _) = CreateService(db, now);

        await service.BeginConnectAsync(victimTeacherId, VideoProviderType.Zoom, RedirectUri, CancellationToken.None);
        var state = await db.OAuthAuthorizationStates.OrderByDescending(s => s.Id).FirstAsync(s => s.TeacherId == victimTeacherId);

        var result = await service.CompleteConnectAsync(VideoProviderType.Zoom, state.StateToken, "code-1", attackerTeacherId, RedirectUri, CancellationToken.None);

        Assert.Equal(CompleteConnectOutcome.TeacherMismatch, result.Outcome);
        Assert.False(await db.TeacherMeetingConnections.AnyAsync(c => c.TeacherId == attackerTeacherId));
        Assert.False(await db.TeacherMeetingConnections.AnyAsync(c => c.TeacherId == victimTeacherId));

        // And the burnt state cannot then be replayed by its rightful owner either.
        var retryByOwner = await service.CompleteConnectAsync(VideoProviderType.Zoom, state.StateToken, "code-1", victimTeacherId, RedirectUri, CancellationToken.None);
        Assert.Equal(CompleteConnectOutcome.InvalidOrExpiredState, retryByOwner.Outcome);
    }

    [Fact]
    public async Task A_forged_state_token_is_rejected()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var teacherId = await CreateTeacherAsync(db);
        var (service, _, _) = CreateService(db, now);

        var result = await service.CompleteConnectAsync(VideoProviderType.Zoom, "totally-made-up-state", "code-1", teacherId, RedirectUri, CancellationToken.None);

        Assert.Equal(CompleteConnectOutcome.InvalidOrExpiredState, result.Outcome);
        Assert.False(await db.TeacherMeetingConnections.AnyAsync(c => c.TeacherId == teacherId));
    }

    [Fact]
    public async Task Disconnecting_marks_the_connection_disconnected_even_when_remote_revocation_fails()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var teacherId = await CreateTeacherAsync(db);
        var (service, _, _) = CreateService(db, now);

        await ConnectAsync(service, db, teacherId, VideoProviderType.Zoom);

        var result = await service.DisconnectAsync(teacherId, VideoProviderType.Zoom, CancellationToken.None);

        Assert.Equal(DisconnectOutcome.Disconnected, result.Outcome);
        var stored = await db.TeacherMeetingConnections.AsNoTracking().FirstAsync(c => c.TeacherId == teacherId);
        Assert.Equal(ProviderConnectionStatus.Disconnected, stored.Status);
        Assert.False(stored.IsDefault);
        Assert.False(await service.IsReadyForOnlineSessionsAsync(teacherId, CancellationToken.None));
    }

    [Fact]
    public async Task The_first_connection_becomes_the_default_and_the_default_can_then_be_switched()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var teacherId = await CreateTeacherAsync(db);
        var (service, _, _) = CreateService(db, now);

        await ConnectAsync(service, db, teacherId, VideoProviderType.Zoom);
        var afterFirst = await service.GetConnectionsAsync(teacherId, CancellationToken.None);
        Assert.True(afterFirst.Single(c => c.Provider == VideoProviderType.Zoom).IsDefault);

        await ConnectAsync(service, db, teacherId, VideoProviderType.GoogleMeet);
        var afterSecond = await service.GetConnectionsAsync(teacherId, CancellationToken.None);
        // Adding a second provider must NOT silently steal the default.
        Assert.True(afterSecond.Single(c => c.Provider == VideoProviderType.Zoom).IsDefault);
        Assert.False(afterSecond.Single(c => c.Provider == VideoProviderType.GoogleMeet).IsDefault);

        var switched = await service.SetDefaultProviderAsync(teacherId, VideoProviderType.GoogleMeet, CancellationToken.None);
        Assert.Equal(SetDefaultProviderOutcome.Updated, switched.Outcome);

        var afterSwitch = await service.GetConnectionsAsync(teacherId, CancellationToken.None);
        Assert.False(afterSwitch.Single(c => c.Provider == VideoProviderType.Zoom).IsDefault);
        Assert.True(afterSwitch.Single(c => c.Provider == VideoProviderType.GoogleMeet).IsDefault);
    }

    [Fact]
    public async Task Disconnecting_the_default_promotes_the_teachers_other_connection()
    {
        // Regression: Disconnect() clears IsDefault. Without promotion the
        // teacher would still show as "ready" (they have a Connected
        // provider) while every session silently failed to provision with
        // NoProviderConnection.
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var teacherId = await CreateTeacherAsync(db);
        var (service, _, _) = CreateService(db, now);

        await ConnectAsync(service, db, teacherId, VideoProviderType.Zoom);      // becomes default
        await ConnectAsync(service, db, teacherId, VideoProviderType.GoogleMeet);

        await service.DisconnectAsync(teacherId, VideoProviderType.Zoom, CancellationToken.None);

        var after = await service.GetConnectionsAsync(teacherId, CancellationToken.None);
        Assert.True(after.Single(c => c.Provider == VideoProviderType.GoogleMeet).IsDefault);
        Assert.True(await service.IsReadyForOnlineSessionsAsync(teacherId, CancellationToken.None));
    }

    [Fact]
    public async Task Connecting_after_the_only_previous_connection_was_disconnected_becomes_the_default()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var teacherId = await CreateTeacherAsync(db);
        var (service, _, _) = CreateService(db, now);

        await ConnectAsync(service, db, teacherId, VideoProviderType.Zoom);
        await service.DisconnectAsync(teacherId, VideoProviderType.Zoom, CancellationToken.None);

        // A brand-new provider connected while the teacher has no usable
        // default must take it — "first row ever created" is not the rule.
        await ConnectAsync(service, db, teacherId, VideoProviderType.GoogleMeet);

        var after = await service.GetConnectionsAsync(teacherId, CancellationToken.None);
        Assert.True(after.Single(c => c.Provider == VideoProviderType.GoogleMeet).IsDefault);
    }

    [Fact]
    public async Task Reconnecting_the_only_disconnected_provider_restores_it_as_the_default()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var teacherId = await CreateTeacherAsync(db);
        var (service, _, _) = CreateService(db, now);

        await ConnectAsync(service, db, teacherId, VideoProviderType.Zoom);
        await service.DisconnectAsync(teacherId, VideoProviderType.Zoom, CancellationToken.None);
        await ConnectAsync(service, db, teacherId, VideoProviderType.Zoom);

        var after = await service.GetConnectionsAsync(teacherId, CancellationToken.None);
        var zoomConnection = after.Single(c => c.Provider == VideoProviderType.Zoom);
        Assert.Equal(ProviderConnectionStatus.Connected, zoomConnection.Status);
        Assert.True(zoomConnection.IsDefault);
    }

    [Fact]
    public async Task A_provider_that_is_not_connected_cannot_be_made_the_default()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var teacherId = await CreateTeacherAsync(db);
        var (service, _, _) = CreateService(db, now);

        var result = await service.SetDefaultProviderAsync(teacherId, VideoProviderType.GoogleMeet, CancellationToken.None);

        Assert.Equal(SetDefaultProviderOutcome.NotConnected, result.Outcome);
    }

    [Fact]
    public async Task A_zoom_basic_account_is_detected_and_recorded_as_restricted()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var teacherId = await CreateTeacherAsync(db);
        var (service, zoom, _) = CreateService(db, now);
        zoom.CapabilityTier = MeetingCapabilityTier.Restricted;
        zoom.CapabilityMinutesLimit = 40;

        await ConnectAsync(service, db, teacherId, VideoProviderType.Zoom);

        var summary = (await service.GetConnectionsAsync(teacherId, CancellationToken.None)).Single();
        Assert.Equal(MeetingCapabilityTier.Restricted, summary.CapabilityTier);
        Assert.Equal(40, summary.CapabilityMinutesLimit);
        Assert.NotNull(summary.CapabilityVerifiedAtUtc);
    }

    [Fact]
    public async Task Reconnecting_the_same_provider_reuses_one_row_and_reverifies_capability()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var teacherId = await CreateTeacherAsync(db);
        var (service, zoom, _) = CreateService(db, now);

        zoom.CapabilityTier = MeetingCapabilityTier.Restricted;
        zoom.CapabilityMinutesLimit = 40;
        await ConnectAsync(service, db, teacherId, VideoProviderType.Zoom);

        // The teacher upgrades their own Zoom plan and reconnects.
        zoom.CapabilityTier = MeetingCapabilityTier.Full;
        zoom.CapabilityMinutesLimit = null;
        await ConnectAsync(service, db, teacherId, VideoProviderType.Zoom);

        var rows = await db.TeacherMeetingConnections.AsNoTracking().Where(c => c.TeacherId == teacherId).ToListAsync();
        Assert.Single(rows);
        Assert.Equal(MeetingCapabilityTier.Full, rows[0].CapabilityTier);
        Assert.Null(rows[0].CapabilityMinutesLimit);
        Assert.Equal(ProviderConnectionStatus.Connected, rows[0].Status);
    }

    [Fact]
    public async Task The_configured_redirect_uri_wins_over_the_request_derived_one()
    {
        // Both providers compare redirect_uri byte-for-byte against their
        // registered value. Behind a reverse proxy (or with a forged Host
        // header) the request-derived URI is wrong, so configuration must win.
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var teacherId = await CreateTeacherAsync(db);
        var (service, zoom, _) = CreateService(db, now);
        zoom.ConfiguredRedirectUri = "https://real-production-host.example/oauth/zoom/callback";

        var result = await service.BeginConnectAsync(teacherId, VideoProviderType.Zoom,
            "https://attacker-controlled-host.example/oauth/zoom/callback", CancellationToken.None);

        Assert.Equal(BeginConnectOutcome.Started, result.Outcome);
        Assert.Contains(Uri.EscapeDataString("https://real-production-host.example/oauth/zoom/callback"),
            result.AuthorizationUrl!, StringComparison.Ordinal);
        Assert.DoesNotContain("attacker-controlled-host", result.AuthorizationUrl!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unconfigured_provider_cannot_start_a_connection()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var teacherId = await CreateTeacherAsync(db);
        var (service, zoom, _) = CreateService(db, now);
        zoom.IsConfigured = false;

        var result = await service.BeginConnectAsync(teacherId, VideoProviderType.Zoom, RedirectUri, CancellationToken.None);

        Assert.Equal(BeginConnectOutcome.ProviderNotConfigured, result.Outcome);
        Assert.Null(result.AuthorizationUrl);
        // And no state row was issued for a provider that cannot be used.
        Assert.False(await db.OAuthAuthorizationStates.AnyAsync(s => s.TeacherId == teacherId));
    }

    private static async Task ConnectAsync(ITeacherMeetingConnectionService service, MvTeachesDbContext db,
        long teacherId, VideoProviderType provider)
    {
        await service.BeginConnectAsync(teacherId, provider, RedirectUri, CancellationToken.None);
        var state = await db.OAuthAuthorizationStates.OrderByDescending(s => s.Id)
            .FirstAsync(s => s.TeacherId == teacherId && s.Provider == provider && s.ConsumedAtUtc == null);
        var result = await service.CompleteConnectAsync(provider, state.StateToken, "code-" + Guid.NewGuid(), teacherId, RedirectUri, CancellationToken.None);
        Assert.Equal(CompleteConnectOutcome.Connected, result.Outcome);
    }
}
