using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MVTeaches.Domain.Integrations;
using MVTeaches.Domain.People;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Integrations.Security;
using MVTeaches.Infrastructure.Integrations.Zoom;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace MVTeaches.Tests.Integrations;

/// <summary>
/// Owner clarification (2026-08-29): atomic refresh-token rotation under
/// genuine concurrency, and webhook authentication/replay protection.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class TokenRefreshAndWebhookTests
{
    private static readonly FakeTokenProtector Protector = FakeTokenProtector.Instance;

    private readonly TestDatabaseFixture _fixture;

    public TokenRefreshAndWebhookTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static async Task<TeacherMeetingConnection> SeedConnectionAsync(MvTeachesDbContext db, Instant now,
        Instant? expiresAt)
    {
        var user = new ApplicationUser
        {
            UserName = $"teacher-{Guid.NewGuid():N}",
            NormalizedUserName = $"TEACHER-{Guid.NewGuid():N}".ToUpperInvariant(),
            Email = $"{Guid.NewGuid():N}@test.mvteaches.local",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var teacher = new Teacher(user.Id, "Teacher", "Asia/Amman");
        db.Teachers.Add(teacher);
        await db.SaveChangesAsync();

        var connection = new TeacherMeetingConnection(teacher.Id, VideoProviderType.Zoom, "acct-" + Guid.NewGuid(),
            "t@example.test", Protector.Protect("old-access"), Protector.Protect("old-refresh"),
            expiresAt, now);
        db.TeacherMeetingConnections.Add(connection);
        await db.SaveChangesAsync();
        return connection;
    }

    private static TokenRefreshCoordinator CreateCoordinator(MvTeachesDbContext db, Instant now) =>
        new(db, new FakeTokenProtector(), new FakeClock(now), NullLogger<TokenRefreshCoordinator>.Instance);

    [Fact]
    public async Task A_still_valid_access_token_is_returned_without_refreshing()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var connection = await SeedConnectionAsync(db, now, now.Plus(Duration.FromHours(1)));
        var client = new FakeVideoMeetingProviderClient(VideoProviderType.Zoom);

        var token = await CreateCoordinator(db, now).GetValidAccessTokenAsync(connection, client, CancellationToken.None);

        Assert.Equal("old-access", token);
        Assert.Empty(client.RefreshTokensSeen);
    }

    [Fact]
    public async Task An_expired_access_token_is_refreshed_and_the_rotated_pair_is_persisted_encrypted()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var connection = await SeedConnectionAsync(db, now, now.Minus(Duration.FromMinutes(5)));
        var client = new FakeVideoMeetingProviderClient(VideoProviderType.Zoom)
        {
            AccessTokenToReturn = "new-access",
            RefreshTokenToReturn = "new-refresh",
            ExpiresAtToReturn = now.Plus(Duration.FromHours(1)),
        };

        var token = await CreateCoordinator(db, now).GetValidAccessTokenAsync(connection, client, CancellationToken.None);

        Assert.Equal("new-access", token);
        Assert.Equal(new[] { "old-refresh" }, client.RefreshTokensSeen);

        await using var verify = _fixture.CreateContext();
        var stored = await verify.TeacherMeetingConnections.AsNoTracking().FirstAsync(c => c.Id == connection.Id);
        Assert.Equal(Protector.Protect("new-access"), stored.EncryptedAccessToken);
        Assert.Equal(Protector.Protect("new-refresh"), stored.EncryptedRefreshToken);
        // The raw token must not be readable in the stored column at all.
        Assert.DoesNotContain("new-access", stored.EncryptedAccessToken, StringComparison.Ordinal);
        Assert.DoesNotContain("new-refresh", stored.EncryptedRefreshToken!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_refreshes_of_one_connection_rotate_the_token_exactly_once()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var seed = _fixture.CreateContext();
        var seeded = await SeedConnectionAsync(seed, now, now.Minus(Duration.FromMinutes(5)));

        // A shared client across two SEPARATE DbContexts — the real race.
        var client = new FakeVideoMeetingProviderClient(VideoProviderType.Zoom)
        {
            AccessTokenToReturn = "rotated-access",
            RefreshTokenToReturn = "rotated-refresh",
            ExpiresAtToReturn = now.Plus(Duration.FromHours(1)),
        };

        await using var dbA = _fixture.CreateContext();
        await using var dbB = _fixture.CreateContext();
        var connectionA = await dbA.TeacherMeetingConnections.FirstAsync(c => c.Id == seeded.Id);
        var connectionB = await dbB.TeacherMeetingConnections.FirstAsync(c => c.Id == seeded.Id);

        var tokens = await Task.WhenAll(
            CreateCoordinator(dbA, now).GetValidAccessTokenAsync(connectionA, client, CancellationToken.None),
            CreateCoordinator(dbB, now).GetValidAccessTokenAsync(connectionB, client, CancellationToken.None));

        // Both callers end up with a usable token...
        Assert.All(tokens, t => Assert.Equal("rotated-access", t));

        // ...but the persisted row was written by exactly one winner — never
        // an older value overwriting a newer one.
        await using var verify = _fixture.CreateContext();
        var stored = await verify.TeacherMeetingConnections.AsNoTracking().FirstAsync(c => c.Id == seeded.Id);
        Assert.Equal(Protector.Protect("rotated-access"), stored.EncryptedAccessToken);
        Assert.Equal(Protector.Protect("rotated-refresh"), stored.EncryptedRefreshToken);
        // token_version advanced exactly once past the initial 1.
        Assert.Equal(2, stored.TokenVersion);
    }

    [Fact]
    public async Task A_connection_with_no_refresh_token_cannot_be_refreshed_and_reports_null()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var connection = await SeedConnectionAsync(db, now, now.Minus(Duration.FromMinutes(5)));
        connection.UpdateTokens(Protector.Protect("old-access"), null, now.Minus(Duration.FromMinutes(5)));
        // Clear it explicitly — UpdateTokens deliberately keeps an existing
        // refresh token when passed null, so this simulates a provider that
        // never issued one at all.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE teacher_meeting_connections SET encrypted_refresh_token = NULL WHERE \"Id\" = {connection.Id}");
        await db.Entry(connection).ReloadAsync();

        var client = new FakeVideoMeetingProviderClient(VideoProviderType.Zoom);
        var token = await CreateCoordinator(db, now).GetValidAccessTokenAsync(connection, client, CancellationToken.None);

        Assert.Null(token);
        Assert.Empty(client.RefreshTokensSeen);
    }

    // ---- Webhook authentication -------------------------------------

    private const string Secret = "webhook-secret-token";

    [Fact]
    public void A_correctly_signed_zoom_webhook_is_accepted()
    {
        const string body = "{\"event\":\"meeting.ended\"}";
        var timestamp = "1756000000000";
        var signature = "v0=" + HmacHex(Secret, $"v0:{timestamp}:{body}");

        Assert.True(ZoomWebhookValidator.IsValidSignature(Secret, timestamp, body, signature));
    }

    [Fact]
    public void An_unsigned_or_forged_zoom_webhook_is_rejected()
    {
        const string body = "{\"event\":\"meeting.ended\"}";
        var timestamp = "1756000000000";

        Assert.False(ZoomWebhookValidator.IsValidSignature(Secret, timestamp, body, null));
        Assert.False(ZoomWebhookValidator.IsValidSignature(Secret, timestamp, body, ""));
        Assert.False(ZoomWebhookValidator.IsValidSignature(Secret, timestamp, body, "v0=deadbeef"));
        // A signature computed with the wrong secret must not pass either.
        Assert.False(ZoomWebhookValidator.IsValidSignature(Secret, timestamp, body,
            "v0=" + HmacHex("wrong-secret", $"v0:{timestamp}:{body}")));
    }

    [Fact]
    public void A_tampered_body_invalidates_a_previously_valid_signature()
    {
        const string original = "{\"event\":\"meeting.ended\",\"payload\":{\"object\":{\"id\":\"111\"}}}";
        const string tampered = "{\"event\":\"meeting.ended\",\"payload\":{\"object\":{\"id\":\"222\"}}}";
        var timestamp = "1756000000000";
        var signature = "v0=" + HmacHex(Secret, $"v0:{timestamp}:{original}");

        Assert.True(ZoomWebhookValidator.IsValidSignature(Secret, timestamp, original, signature));
        Assert.False(ZoomWebhookValidator.IsValidSignature(Secret, timestamp, tampered, signature));
    }

    [Fact]
    public void A_stale_or_replayed_zoom_webhook_timestamp_is_rejected()
    {
        var now = Instant.FromUtc(2026, 8, 29, 12, 0, 0);
        var tolerance = Duration.FromMinutes(5);

        var fresh = now.Minus(Duration.FromMinutes(1)).ToUnixTimeMilliseconds().ToString();
        var stale = now.Minus(Duration.FromHours(2)).ToUnixTimeMilliseconds().ToString();
        var future = now.Plus(Duration.FromHours(2)).ToUnixTimeMilliseconds().ToString();

        Assert.True(ZoomWebhookValidator.IsFreshTimestamp(fresh, now, tolerance));
        Assert.False(ZoomWebhookValidator.IsFreshTimestamp(stale, now, tolerance));
        Assert.False(ZoomWebhookValidator.IsFreshTimestamp(future, now, tolerance));
        Assert.False(ZoomWebhookValidator.IsFreshTimestamp("not-a-number", now, tolerance));
    }

    private static string HmacHex(string secret, string message)
    {
        using var hmac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(message));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
