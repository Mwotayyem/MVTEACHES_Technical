using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MVTeaches.Application.Integrations;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Integrations;
using MVTeaches.Domain.Notifications;
using MVTeaches.Domain.People;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Integrations;
using MVTeaches.Infrastructure.Integrations.Security;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace MVTeaches.Tests.Integrations;

/// <summary>
/// Owner clarification (2026-08-29). Covers the provisioning half against
/// real PostgreSQL: per-teacher/per-connection ownership and IDOR, Zoom
/// Basic 40-minute enforcement, paid-Zoom routing, Google free one-to-one
/// vs group duration enforcement, conservative handling when paid capability
/// cannot be proven, idempotent and genuinely concurrent provisioning,
/// meeting ownership after teacher reassignment, host-link secrecy, and the
/// absence of any global/centre-wide meeting lock.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class MeetingProvisioningServiceTests
{
    private static readonly FakeTokenProtector Protector = FakeTokenProtector.Instance;

    private readonly TestDatabaseFixture _fixture;
    private static long _idSeed = 95_000_000;

    public MeetingProvisioningServiceTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static long NextId() => Interlocked.Increment(ref _idSeed);

    private static string TwoLetterCode(long seed)
    {
        var n = (int)(seed % 676);
        return string.Concat((char)('A' + n / 26), (char)('A' + n % 26));
    }

    private static int? _sharedCountryId;

    private async Task<int> GetOrSeedCountryAsync(MvTeachesDbContext db)
    {
        if (_sharedCountryId is { } existing)
        {
            return existing;
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var countryId = (int)NextId();
            db.Countries.Add(new Country(countryId, TwoLetterCode(countryId), "دولة", "Country", "JOD", "+962", "Asia/Amman"));
            try
            {
                await db.SaveChangesAsync();
                _sharedCountryId = countryId;
                return countryId;
            }
            catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
            {
                db.ChangeTracker.Clear();
            }
        }

        throw new InvalidOperationException("Could not find a free 2-letter country code after 10 attempts.");
    }

    private record Scene(long TeacherId, long SessionId, long ConnectionId, int CountryId, long CourseId, int LevelId, int AgeGroupId);

    private static async Task<long> CreateUserAsync(MvTeachesDbContext db, string label)
    {
        var user = new ApplicationUser
        {
            UserName = $"{label}-{Guid.NewGuid():N}",
            NormalizedUserName = $"{label}-{Guid.NewGuid():N}".ToUpperInvariant(),
            Email = $"{Guid.NewGuid():N}@test.mvteaches.local",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task<Scene> SeedAsync(MvTeachesDbContext db, Instant sessionStart, int durationMinutes = 60,
        int capacity = 4, VideoProviderType provider = VideoProviderType.Zoom,
        MeetingCapabilityTier tier = MeetingCapabilityTier.Full, int? minutesLimit = null, bool connected = true)
    {
        var countryId = await GetOrSeedCountryAsync(db);
        var courseId = NextId();
        var levelId = (int)NextId();
        var ageGroupId = (int)NextId();
        var teacherUserId = await CreateUserAsync(db, "teacher");

        db.Courses.Add(new Course("C" + courseId, "دورة", "Course"));
        db.Levels.Add(new Level(levelId, "L" + levelId, "مستوى", "Level", levelId));
        db.AgeGroups.Add(new AgeGroup(ageGroupId, "A" + ageGroupId, 5, 60, true));
        var teacher = new Teacher(teacherUserId, "Teacher", "Asia/Amman");
        db.Teachers.Add(teacher);
        await db.SaveChangesAsync();

        var session = new ClassSession(countryId, null, courseId, levelId, ageGroupId, teacher.Id,
            sessionStart, sessionStart.Plus(Duration.FromMinutes(durationMinutes)), "Asia/Amman", "10:00",
            SessionType.Group, capacity, sessionStart.Minus(Duration.FromDays(1)));
        db.ClassSessions.Add(session);

        var connection = new TeacherMeetingConnection(teacher.Id, provider, "acct-" + NextId(), "t@example.test",
            Protector.Protect("access"), Protector.Protect("refresh"), null,
            sessionStart.Minus(Duration.FromDays(2)));
        connection.UpdateCapability(tier, minutesLimit, sessionStart.Minus(Duration.FromDays(2)));
        connection.MarkDefault();
        if (!connected)
        {
            connection.Disconnect(sessionStart.Minus(Duration.FromDays(1)));
        }

        db.TeacherMeetingConnections.Add(connection);
        await db.SaveChangesAsync();

        return new Scene(teacher.Id, session.Id, connection.Id, countryId, courseId, levelId, ageGroupId);
    }

    private static MeetingProvisioningService CreateService(MvTeachesDbContext db, Instant now,
        params FakeVideoMeetingProviderClient[] clients) =>
        new(db, clients, new TokenRefreshCoordinator(db, new FakeTokenProtector(), new FakeClock(now),
                NullLogger<TokenRefreshCoordinator>.Instance),
            new FakeClock(now), NullLogger<MeetingProvisioningService>.Instance);

    private static FakeVideoMeetingProviderClient[] BothProviders(out FakeVideoMeetingProviderClient zoom,
        out FakeVideoMeetingProviderClient google)
    {
        zoom = new FakeVideoMeetingProviderClient(VideoProviderType.Zoom);
        google = new FakeVideoMeetingProviderClient(VideoProviderType.GoogleMeet);
        return new[] { zoom, google };
    }

    [Fact]
    public async Task A_meeting_is_created_once_and_repeated_calls_are_idempotent()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var scene = await SeedAsync(db, now.Plus(Duration.FromHours(1)));
        var clients = BothProviders(out var zoom, out _);
        var service = CreateService(db, now, clients);

        var first = await service.GetOrProvisionReadyMeetingAsync(scene.SessionId, CancellationToken.None);
        var second = await service.GetOrProvisionReadyMeetingAsync(scene.SessionId, CancellationToken.None);

        Assert.Equal(ProvisionMeetingOutcome.Ready, first.Outcome);
        Assert.Equal(ProvisionMeetingOutcome.Ready, second.Outcome);
        Assert.Equal(first.JoinUrl, second.JoinUrl);
        Assert.Equal(1, zoom.CreatedCount);
        Assert.Single(await db.ProvisionedMeetings.AsNoTracking().Where(m => m.SessionId == scene.SessionId && m.IsActive).ToListAsync());
    }

    [Fact]
    public async Task Concurrent_provisioning_of_one_session_creates_at_most_one_external_meeting()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var seed = _fixture.CreateContext();
        var scene = await SeedAsync(seed, now.Plus(Duration.FromHours(1)));

        // A genuinely shared client instance across two SEPARATE DbContexts —
        // the real race, not a simulated one.
        var zoom = new FakeVideoMeetingProviderClient(VideoProviderType.Zoom);
        var google = new FakeVideoMeetingProviderClient(VideoProviderType.GoogleMeet);

        await using var dbA = _fixture.CreateContext();
        await using var dbB = _fixture.CreateContext();
        var serviceA = CreateService(dbA, now, zoom, google);
        var serviceB = CreateService(dbB, now, zoom, google);

        var results = await Task.WhenAll(
            serviceA.GetOrProvisionReadyMeetingAsync(scene.SessionId, CancellationToken.None),
            serviceB.GetOrProvisionReadyMeetingAsync(scene.SessionId, CancellationToken.None));

        // ux_provisioned_meeting_active_session is the real guarantee here.
        Assert.Equal(1, await db_ActiveMeetingCountAsync(scene.SessionId));
        Assert.True(zoom.CreatedCount <= 1, $"Expected at most one external meeting, got {zoom.CreatedCount}.");
        Assert.Contains(results, r => r.Outcome is ProvisionMeetingOutcome.Ready or ProvisionMeetingOutcome.StillProvisioning);
    }

    private async Task<int> db_ActiveMeetingCountAsync(long sessionId)
    {
        await using var verify = _fixture.CreateContext();
        return await verify.ProvisionedMeetings.CountAsync(m => m.SessionId == sessionId && m.IsActive);
    }

    /// <summary>
    /// Owner decision 2026-08-30, superseding the duration-blocking half of
    /// D-92. A Basic Zoom account no longer prevents a 60-minute session from
    /// being created; the meeting is provisioned at the session's real
    /// scheduled duration and the teacher is warned that Zoom will cut it off.
    /// </summary>
    [Fact]
    public async Task A_basic_zoom_account_may_provision_a_longer_session_but_the_teacher_is_warned()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var scene = await SeedAsync(db, now.Plus(Duration.FromHours(1)), durationMinutes: 60,
            provider: VideoProviderType.Zoom, tier: MeetingCapabilityTier.Restricted, minutesLimit: 40);
        var clients = BothProviders(out var zoom, out _);
        var service = CreateService(db, now, clients);

        var result = await service.GetOrProvisionReadyMeetingAsync(scene.SessionId, CancellationToken.None);

        Assert.Equal(ProvisionMeetingOutcome.Ready, result.Outcome);
        Assert.Equal(1, zoom.CreatedCount);                       // created, not refused
        Assert.NotNull(result.CapabilityWarning);
        Assert.Contains("40 minutes", result.CapabilityWarning);
        Assert.Contains("60 minutes", result.CapabilityWarning);  // names the real scheduled duration

        // The session itself is untouched: the plan limit must never shorten it.
        await using var verify = _fixture.CreateContext();
        var session = await verify.ClassSessions.FirstAsync(s => s.Id == scene.SessionId);
        Assert.Equal(60, session.DurationMinutes);
    }

    /// <summary>The read-only form used to warn the teacher BEFORE they press
    /// Start must agree with what provisioning reports, and must provision
    /// nothing of its own.</summary>
    [Fact]
    public async Task The_read_only_capability_warning_matches_and_provisions_nothing()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var scene = await SeedAsync(db, now.Plus(Duration.FromHours(1)), durationMinutes: 90,
            provider: VideoProviderType.Zoom, tier: MeetingCapabilityTier.Restricted, minutesLimit: 40);
        var clients = BothProviders(out var zoom, out _);
        var service = CreateService(db, now, clients);

        var warning = await service.GetCapabilityWarningAsync(scene.SessionId, CancellationToken.None);

        Assert.NotNull(warning);
        Assert.Contains("90 minutes", warning);
        Assert.Equal(0, zoom.CreatedCount);
        Assert.Equal(0, await db_ActiveMeetingCountAsync(scene.SessionId));
    }

    [Fact]
    public async Task A_basic_zoom_account_may_provision_a_session_within_forty_minutes()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var scene = await SeedAsync(db, now.Plus(Duration.FromHours(1)), durationMinutes: 40,
            provider: VideoProviderType.Zoom, tier: MeetingCapabilityTier.Restricted, minutesLimit: 40);
        var clients = BothProviders(out var zoom, out _);
        var service = CreateService(db, now, clients);

        var result = await service.GetOrProvisionReadyMeetingAsync(scene.SessionId, CancellationToken.None);

        Assert.Equal(ProvisionMeetingOutcome.Ready, result.Outcome);
        Assert.Equal(1, zoom.CreatedCount);
    }

    [Fact]
    public async Task A_licensed_zoom_account_may_provision_a_full_length_session()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var scene = await SeedAsync(db, now.Plus(Duration.FromHours(1)), durationMinutes: 120,
            provider: VideoProviderType.Zoom, tier: MeetingCapabilityTier.Full);
        var clients = BothProviders(out var zoom, out _);
        var service = CreateService(db, now, clients);

        var result = await service.GetOrProvisionReadyMeetingAsync(scene.SessionId, CancellationToken.None);

        Assert.Equal(ProvisionMeetingOutcome.Ready, result.Outcome);
        Assert.Equal(1, zoom.CreatedCount);
    }

    /// <summary>Owner decision 2026-08-30: warned, not blocked (see the Zoom
    /// equivalent above). capacity > 1 means the session MAY contain 3+
    /// participants, so the 60-minute group rule applies regardless of how
    /// many students have actually booked so far.</summary>
    [Fact]
    public async Task A_free_google_group_session_over_sixty_minutes_is_created_with_a_warning()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var scene = await SeedAsync(db, now.Plus(Duration.FromHours(1)), durationMinutes: 90, capacity: 4,
            provider: VideoProviderType.GoogleMeet, tier: MeetingCapabilityTier.Restricted);
        var clients = BothProviders(out _, out var google);
        var service = CreateService(db, now, clients);

        var result = await service.GetOrProvisionReadyMeetingAsync(scene.SessionId, CancellationToken.None);

        Assert.Equal(ProvisionMeetingOutcome.Ready, result.Outcome);
        Assert.Equal(1, google.CreatedCount);
        Assert.NotNull(result.CapabilityWarning);
        Assert.Contains("60 minutes", result.CapabilityWarning);
        Assert.Contains("90 minutes", result.CapabilityWarning);
    }

    [Fact]
    public async Task A_free_google_group_session_of_exactly_sixty_minutes_is_allowed_with_a_warning()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var scene = await SeedAsync(db, now.Plus(Duration.FromHours(1)), durationMinutes: 60, capacity: 4,
            provider: VideoProviderType.GoogleMeet, tier: MeetingCapabilityTier.Restricted);
        var clients = BothProviders(out _, out var google);
        var service = CreateService(db, now, clients);

        var result = await service.GetOrProvisionReadyMeetingAsync(scene.SessionId, CancellationToken.None);

        Assert.Equal(ProvisionMeetingOutcome.Ready, result.Outcome);
        Assert.Equal(1, google.CreatedCount);
        // Exactly at the boundary: allowed, and warned about the boundary
        // specifically rather than about exceeding the limit.
        Assert.Contains("60-minute", result.CapabilityWarning);
    }

    [Fact]
    public async Task A_free_google_one_to_one_session_may_run_far_longer_than_the_group_limit()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        // capacity == 1 is a TRUE one-to-one (one teacher, one student).
        var scene = await SeedAsync(db, now.Plus(Duration.FromHours(1)), durationMinutes: 180, capacity: 1,
            provider: VideoProviderType.GoogleMeet, tier: MeetingCapabilityTier.Restricted);
        var clients = BothProviders(out _, out var google);
        var service = CreateService(db, now, clients);

        var result = await service.GetOrProvisionReadyMeetingAsync(scene.SessionId, CancellationToken.None);

        Assert.Equal(ProvisionMeetingOutcome.Ready, result.Outcome);
        Assert.Equal(1, google.CreatedCount);
    }

    [Fact]
    public async Task A_google_session_beyond_twenty_four_hours_is_created_with_a_warning_even_one_to_one()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var scene = await SeedAsync(db, now.Plus(Duration.FromHours(1)), durationMinutes: 25 * 60, capacity: 1,
            provider: VideoProviderType.GoogleMeet, tier: MeetingCapabilityTier.Restricted);
        var clients = BothProviders(out _, out var google);
        var service = CreateService(db, now, clients);

        var result = await service.GetOrProvisionReadyMeetingAsync(scene.SessionId, CancellationToken.None);

        Assert.Equal(ProvisionMeetingOutcome.Ready, result.Outcome);
        Assert.Equal(1, google.CreatedCount);
        Assert.NotNull(result.CapabilityWarning);
        Assert.Contains("24 hours", result.CapabilityWarning);
    }

    [Fact]
    public async Task An_unverified_google_capability_is_treated_conservatively_as_free_not_paid()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        // Tier deliberately left Unknown — Google exposes no reliable paid check.
        var scene = await SeedAsync(db, now.Plus(Duration.FromHours(1)), durationMinutes: 90, capacity: 4,
            provider: VideoProviderType.GoogleMeet, tier: MeetingCapabilityTier.Unknown);
        var clients = BothProviders(out _, out var google);
        var service = CreateService(db, now, clients);

        var result = await service.GetOrProvisionReadyMeetingAsync(scene.SessionId, CancellationToken.None);

        // "Conservative" now means "warn as if free", not "refuse" — an Unknown
        // tier must still produce the free-tier warning rather than silently
        // assuming the account is paid and staying quiet.
        Assert.Equal(ProvisionMeetingOutcome.Ready, result.Outcome);
        Assert.Equal(1, google.CreatedCount);
        Assert.NotNull(result.CapabilityWarning);
        Assert.Contains("60 minutes", result.CapabilityWarning);
    }

    [Fact]
    public async Task A_teacher_with_no_connection_gets_an_actionable_status_and_no_fake_link()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var scene = await SeedAsync(db, now.Plus(Duration.FromHours(1)), connected: false);
        var clients = BothProviders(out var zoom, out var google);
        var service = CreateService(db, now, clients);

        var result = await service.GetOrProvisionReadyMeetingAsync(scene.SessionId, CancellationToken.None);

        Assert.Equal(ProvisionMeetingOutcome.NoProviderConnection, result.Outcome);
        Assert.Null(result.JoinUrl);
        Assert.Equal(0, zoom.CreatedCount);
        Assert.Equal(0, google.CreatedCount);
        Assert.False(await db.ProvisionedMeetings.AnyAsync(m => m.SessionId == scene.SessionId));
    }

    [Fact]
    public async Task A_revoked_connection_never_silently_falls_back_to_the_teachers_other_provider()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var scene = await SeedAsync(db, now.Plus(Duration.FromHours(1)));
        var clients = BothProviders(out var zoom, out var google);
        var service = CreateService(db, now, clients);

        // A meeting already exists under Zoom; then Zoom is revoked and the
        // teacher connects Google as well.
        await service.GetOrProvisionReadyMeetingAsync(scene.SessionId, CancellationToken.None);
        var meeting = await db.ProvisionedMeetings.FirstAsync(m => m.SessionId == scene.SessionId && m.IsActive);
        meeting.MarkFailed("simulated transient failure"); // force re-provisioning on the next call

        var zoomConnection = await db.TeacherMeetingConnections.FirstAsync(c => c.Id == scene.ConnectionId);
        zoomConnection.MarkRevoked(now, "revoked by the teacher at the provider");
        var googleConnection = new TeacherMeetingConnection(scene.TeacherId, VideoProviderType.GoogleMeet, "g-acct",
            "t@example.test", Protector.Protect("a"), Protector.Protect("r"), null, now);
        googleConnection.MarkDefault();
        db.TeacherMeetingConnections.Add(googleConnection);
        await db.SaveChangesAsync();

        var result = await service.GetOrProvisionReadyMeetingAsync(scene.SessionId, CancellationToken.None);

        Assert.Equal(ProvisionMeetingOutcome.ProviderDisconnected, result.Outcome);
        Assert.Equal(0, google.CreatedCount);   // ← the whole point: no silent fallback
        Assert.Equal(1, zoom.CreatedCount);     // only the original, pre-revocation one
    }

    [Fact]
    public async Task Another_teacher_cannot_obtain_the_host_link_for_someone_elses_session()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var owner = await SeedAsync(db, now.Plus(Duration.FromHours(1)));
        var intruder = await SeedAsync(db, now.Plus(Duration.FromHours(5)));
        var clients = BothProviders(out _, out _);
        var service = CreateService(db, now, clients);

        await service.GetOrProvisionReadyMeetingAsync(owner.SessionId, CancellationToken.None);

        var asOwner = await service.GetHostStartUrlAsync(owner.SessionId, owner.TeacherId, CancellationToken.None);
        var asIntruder = await service.GetHostStartUrlAsync(owner.SessionId, intruder.TeacherId, CancellationToken.None);

        Assert.NotNull(asOwner);
        Assert.Null(asIntruder);
    }

    [Fact]
    public async Task The_zoom_host_link_is_never_persisted_only_the_participant_link_is()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var scene = await SeedAsync(db, now.Plus(Duration.FromHours(1)));
        var clients = BothProviders(out var zoom, out _);
        zoom.HostStartUrl = "https://provider.test/start?secret=HOSTONLY-DO-NOT-PERSIST";
        var service = CreateService(db, now, clients);

        await service.GetOrProvisionReadyMeetingAsync(scene.SessionId, CancellationToken.None);
        var hostUrl = await service.GetHostStartUrlAsync(scene.SessionId, scene.TeacherId, CancellationToken.None);

        Assert.Equal("https://provider.test/start?secret=HOSTONLY-DO-NOT-PERSIST", hostUrl);

        await using var verify = _fixture.CreateContext();
        var stored = await verify.ProvisionedMeetings.AsNoTracking().FirstAsync(m => m.SessionId == scene.SessionId && m.IsActive);
        Assert.DoesNotContain("HOSTONLY", stored.JoinUrl ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("HOSTONLY", stored.StatusDetail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_teachers_with_independent_accounts_can_host_simultaneous_sessions()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        var start = now.Plus(Duration.FromHours(1));
        await using var db = _fixture.CreateContext();
        // Deliberately the SAME start time — there must be no centre-wide or
        // provider-wide meeting lock, only the per-teacher overlap rule.
        var a = await SeedAsync(db, start);
        var b = await SeedAsync(db, start, provider: VideoProviderType.GoogleMeet, tier: MeetingCapabilityTier.Restricted);
        var clients = BothProviders(out var zoom, out var google);
        var service = CreateService(db, now, clients);

        var resultA = await service.GetOrProvisionReadyMeetingAsync(a.SessionId, CancellationToken.None);
        var resultB = await service.GetOrProvisionReadyMeetingAsync(b.SessionId, CancellationToken.None);

        Assert.Equal(ProvisionMeetingOutcome.Ready, resultA.Outcome);
        Assert.Equal(ProvisionMeetingOutcome.Ready, resultB.Outcome);
        Assert.Equal(VideoProviderType.Zoom, resultA.Provider);
        Assert.Equal(VideoProviderType.GoogleMeet, resultB.Provider);
        Assert.Equal(1, zoom.CreatedCount);
        Assert.Equal(1, google.CreatedCount);
        Assert.NotEqual(resultA.JoinUrl, resultB.JoinUrl);
    }

    [Fact]
    public async Task Reassigning_the_teacher_cancels_the_old_meeting_and_never_reuses_it()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var original = await SeedAsync(db, now.Plus(Duration.FromDays(2)));
        // A second teacher, with their own connection, at a non-overlapping time.
        var replacement = await SeedAsync(db, now.Plus(Duration.FromDays(9)));
        var clients = BothProviders(out var zoom, out _);
        var service = CreateService(db, now, clients);

        var beforeUrl = (await service.GetOrProvisionReadyMeetingAsync(original.SessionId, CancellationToken.None)).JoinUrl;
        var oldMeetingId = (await db.ProvisionedMeetings.AsNoTracking().FirstAsync(m => m.SessionId == original.SessionId && m.IsActive)).Id;

        var reassign = await service.ReassignTeacherAsync(original.SessionId, replacement.TeacherId, performedByUserId: 1, CancellationToken.None);
        Assert.Equal(TeacherReassignmentOutcome.Reassigned, reassign.Outcome);
        Assert.Equal(1, zoom.CancelledCount);

        var afterUrl = (await service.GetOrProvisionReadyMeetingAsync(original.SessionId, CancellationToken.None)).JoinUrl;

        Assert.NotEqual(beforeUrl, afterUrl);
        Assert.Equal(2, zoom.CreatedCount);

        await using var verify = _fixture.CreateContext();
        var oldRow = await verify.ProvisionedMeetings.AsNoTracking().FirstAsync(m => m.Id == oldMeetingId);
        Assert.False(oldRow.IsActive);
        Assert.Equal(MeetingProvisioningStatus.Cancelled, oldRow.Status);

        var newRow = await verify.ProvisionedMeetings.AsNoTracking().FirstAsync(m => m.SessionId == original.SessionId && m.IsActive);
        // The new meeting belongs to the NEW teacher's own connection, never the old one.
        Assert.NotEqual(original.ConnectionId, newRow.ConnectionId);
        Assert.Equal(replacement.ConnectionId, newRow.ConnectionId);

        Assert.True(await verify.AuditLogEntries.AnyAsync(
            a => a.EntityType == "ClassSession" && a.EntityId == original.SessionId.ToString() && a.Action == "TeacherReassigned"));
    }

    [Fact]
    public async Task Reassignment_flags_an_orphan_when_the_old_connection_cannot_clean_up()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var original = await SeedAsync(db, now.Plus(Duration.FromDays(3)));
        var replacement = await SeedAsync(db, now.Plus(Duration.FromDays(11)));
        var clients = BothProviders(out var zoom, out _);
        var service = CreateService(db, now, clients);

        await service.GetOrProvisionReadyMeetingAsync(original.SessionId, CancellationToken.None);
        var oldMeetingId = (await db.ProvisionedMeetings.AsNoTracking().FirstAsync(m => m.SessionId == original.SessionId && m.IsActive)).Id;

        // The former teacher revoked MVTeaches' access before we could clean up.
        var oldConnection = await db.TeacherMeetingConnections.FirstAsync(c => c.Id == original.ConnectionId);
        oldConnection.MarkRevoked(now, "revoked at the provider");
        await db.SaveChangesAsync();

        var reassign = await service.ReassignTeacherAsync(original.SessionId, replacement.TeacherId, performedByUserId: 1, CancellationToken.None);

        Assert.Equal(TeacherReassignmentOutcome.Reassigned, reassign.Outcome);
        Assert.Equal(0, zoom.CancelledCount);

        await using var verify = _fixture.CreateContext();
        var oldRow = await verify.ProvisionedMeetings.AsNoTracking().FirstAsync(m => m.Id == oldMeetingId);
        Assert.Equal(MeetingProvisioningStatus.Orphaned, oldRow.Status);
        Assert.False(oldRow.IsActive);
        // And the new teacher is definitively NOT linked to that orphan.
        Assert.Equal(original.ConnectionId, oldRow.ConnectionId);
    }

    [Fact]
    public async Task Reassigning_to_a_teacher_with_no_connection_is_refused()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var original = await SeedAsync(db, now.Plus(Duration.FromDays(4)));
        var unreadyUserId = await CreateUserAsync(db, "unready");
        var unready = new Teacher(unreadyUserId, "Unready Teacher", "Asia/Amman");
        db.Teachers.Add(unready);
        await db.SaveChangesAsync();

        var clients = BothProviders(out _, out _);
        var service = CreateService(db, now, clients);

        var result = await service.ReassignTeacherAsync(original.SessionId, unready.Id, performedByUserId: 1, CancellationToken.None);

        Assert.Equal(TeacherReassignmentOutcome.NewTeacherNotReadyForOnlineSessions, result.Outcome);
        var session = await db.ClassSessions.AsNoTracking().FirstAsync(s => s.Id == original.SessionId);
        Assert.Equal(original.TeacherId, session.TeacherId);
    }

    [Fact]
    public async Task Reassigning_a_session_that_already_started_is_refused()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var original = await SeedAsync(db, now.Minus(Duration.FromHours(1)));
        var replacement = await SeedAsync(db, now.Plus(Duration.FromDays(13)));
        var clients = BothProviders(out _, out _);
        var service = CreateService(db, now, clients);

        var result = await service.ReassignTeacherAsync(original.SessionId, replacement.TeacherId, performedByUserId: 1, CancellationToken.None);

        Assert.Equal(TeacherReassignmentOutcome.SessionNotReassignable, result.Outcome);
    }

    [Fact]
    public async Task Reassignment_notifies_every_enrolled_student_through_the_existing_outbox()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var original = await SeedAsync(db, now.Plus(Duration.FromDays(5)));
        var replacement = await SeedAsync(db, now.Plus(Duration.FromDays(15)));

        var studentUserId = await CreateUserAsync(db, "student");
        var student = new Student(original.CountryId, "Enrolled Student", new LocalDate(2000, 1, 1), studentUserId);
        db.Students.Add(student);
        await db.SaveChangesAsync();
        db.SessionEnrollments.Add(new SessionEnrollment(original.SessionId, student.Id, original.AgeGroupId, studentUserId, now));
        await db.SaveChangesAsync();

        var clients = BothProviders(out _, out _);
        var service = CreateService(db, now, clients);
        await service.GetOrProvisionReadyMeetingAsync(original.SessionId, CancellationToken.None);

        await service.ReassignTeacherAsync(original.SessionId, replacement.TeacherId, performedByUserId: 1, CancellationToken.None);

        await using var verify = _fixture.CreateContext();
        var notifications = await verify.NotificationOutboxItems.AsNoTracking()
            .Where(n => n.RecipientUserId == studentUserId).ToListAsync();
        var item = Assert.Single(notifications);
        Assert.Equal(NotificationEvent.SessionCancelledOrMoved, item.Event);
        Assert.Contains("TeacherChanged", item.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_provider_failure_leaves_the_meeting_visibly_failed_never_falsely_ready()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var scene = await SeedAsync(db, now.Plus(Duration.FromHours(1)));
        var clients = BothProviders(out var zoom, out _);
        zoom.ThrowOnCreate = true;
        var service = CreateService(db, now, clients);

        var result = await service.GetOrProvisionReadyMeetingAsync(scene.SessionId, CancellationToken.None);

        Assert.Equal(ProvisionMeetingOutcome.Failed, result.Outcome);
        Assert.Null(result.JoinUrl);

        await using var verify = _fixture.CreateContext();
        var stored = await verify.ProvisionedMeetings.AsNoTracking().FirstAsync(m => m.SessionId == scene.SessionId);
        Assert.Equal(MeetingProvisioningStatus.Failed, stored.Status);
        Assert.Null(stored.JoinUrl);
        Assert.Null(stored.ExternalMeetingId);
    }

    [Fact]
    public async Task A_failed_provisioning_can_be_retried_successfully()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var scene = await SeedAsync(db, now.Plus(Duration.FromHours(1)));
        var clients = BothProviders(out var zoom, out _);
        zoom.ThrowOnCreate = true;
        var service = CreateService(db, now, clients);

        await service.GetOrProvisionReadyMeetingAsync(scene.SessionId, CancellationToken.None);
        zoom.ThrowOnCreate = false;
        var retry = await service.GetOrProvisionReadyMeetingAsync(scene.SessionId, CancellationToken.None);

        Assert.Equal(ProvisionMeetingOutcome.Ready, retry.Outcome);
        Assert.NotNull(retry.JoinUrl);
        Assert.Equal(1, await db_ActiveMeetingCountAsync(scene.SessionId));
    }

    [Fact]
    public async Task A_cancelled_session_never_gets_a_meeting_provisioned_for_it()
    {
        // Regression: CancelForSessionAsync deactivates the meeting row, which
        // on its own would leave the next Start/Join press free to provision a
        // brand-new meeting for a session that no longer happens.
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var scene = await SeedAsync(db, now.Plus(Duration.FromHours(1)));
        var clients = BothProviders(out var zoom, out _);
        var service = CreateService(db, now, clients);

        await service.GetOrProvisionReadyMeetingAsync(scene.SessionId, CancellationToken.None);
        await service.CancelForSessionAsync(scene.SessionId, "centre cancellation", CancellationToken.None);

        var session = await db.ClassSessions.FirstAsync(s => s.Id == scene.SessionId);
        session.Cancel("centre cancellation", cancelledByUserId: 1);
        await db.SaveChangesAsync();

        var afterCancellation = await service.GetOrProvisionReadyMeetingAsync(scene.SessionId, CancellationToken.None);

        Assert.Equal(ProvisionMeetingOutcome.SessionNotProvisionable, afterCancellation.Outcome);
        Assert.Null(afterCancellation.JoinUrl);
        Assert.Equal(1, zoom.CreatedCount); // only the original, pre-cancellation one
        Assert.Equal(0, await db_ActiveMeetingCountAsync(scene.SessionId));
    }

    [Fact]
    public async Task Cancelling_a_session_cancels_its_meeting_without_touching_attendance_or_entitlement()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var scene = await SeedAsync(db, now.Plus(Duration.FromHours(1)));
        var clients = BothProviders(out var zoom, out _);
        var service = CreateService(db, now, clients);

        await service.GetOrProvisionReadyMeetingAsync(scene.SessionId, CancellationToken.None);
        await service.CancelForSessionAsync(scene.SessionId, "centre cancellation", CancellationToken.None);

        Assert.Equal(1, zoom.CancelledCount);

        await using var verify = _fixture.CreateContext();
        var stored = await verify.ProvisionedMeetings.AsNoTracking().FirstAsync(m => m.SessionId == scene.SessionId);
        Assert.Equal(MeetingProvisioningStatus.Cancelled, stored.Status);
        Assert.False(stored.IsActive);
        // The cancellation path never writes attendance or ledger rows.
        Assert.False(await verify.AttendanceRecords.AnyAsync(a => a.SessionId == scene.SessionId));
        Assert.Equal(0, await db_ActiveMeetingCountAsync(scene.SessionId));
    }
}
