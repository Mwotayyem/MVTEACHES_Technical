using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.People;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.People;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.People;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace MVTeaches.Tests.People;

/// <summary>
/// Owner decision 2026-08-30 rule 5: the admin decides which levels a teacher
/// may publish sessions for, and a teacher must not publish for an
/// unauthorized level. Runs against real PostgreSQL so ux_teacher_level is
/// exercised for real.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class TeacherLevelAuthorizationServiceTests
{
    private readonly TestDatabaseFixture _fixture;
    private static long _idSeed = 61_000_000;

    public TeacherLevelAuthorizationServiceTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static long NextId() => Interlocked.Increment(ref _idSeed);

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

    private record Scene(long TeacherId, int LevelA, int LevelB, long AdminUserId);

    private static async Task<Scene> SeedAsync(MvTeachesDbContext db)
    {
        var levelA = (int)NextId();
        var levelB = (int)NextId();
        var teacherUserId = await CreateUserAsync(db, "teacher");
        var adminUserId = await CreateUserAsync(db, "admin");

        db.Levels.Add(new Level(levelA, "L" + levelA, "مستوى", "Level A", levelA));
        db.Levels.Add(new Level(levelB, "L" + levelB, "مستوى", "Level B", levelB));
        var teacher = new Teacher(teacherUserId, "Teacher", "Asia/Amman");
        db.Teachers.Add(teacher);
        await db.SaveChangesAsync();

        return new Scene(teacher.Id, levelA, levelB, adminUserId);
    }

    private static TeacherLevelAuthorizationService CreateService(MvTeachesDbContext db) =>
        new(db, new FakeClock(SystemClock.Instance.GetCurrentInstant()));

    [Fact]
    public async Task A_teacher_starts_with_no_permitted_levels_at_all()
    {
        await using var db = _fixture.CreateContext();
        var scene = await SeedAsync(db);
        var service = CreateService(db);

        // Absence of a grant is denial — there is no implicit default.
        Assert.False(await service.IsAuthorizedForLevelAsync(scene.TeacherId, scene.LevelA, CancellationToken.None));
        Assert.Empty(await service.GetPermittedLevelIdsAsync(scene.TeacherId, CancellationToken.None));
    }

    [Fact]
    public async Task Granting_a_level_authorizes_only_that_level()
    {
        await using var db = _fixture.CreateContext();
        var scene = await SeedAsync(db);
        var service = CreateService(db);

        var outcome = await service.GrantAsync(scene.TeacherId, scene.LevelA, scene.AdminUserId, CancellationToken.None);

        Assert.Equal(TeacherLevelGrantOutcome.Granted, outcome);
        Assert.True(await service.IsAuthorizedForLevelAsync(scene.TeacherId, scene.LevelA, CancellationToken.None));
        Assert.False(await service.IsAuthorizedForLevelAsync(scene.TeacherId, scene.LevelB, CancellationToken.None));
    }

    [Fact]
    public async Task Granting_the_same_level_twice_is_idempotent_not_an_error()
    {
        await using var db = _fixture.CreateContext();
        var scene = await SeedAsync(db);
        var service = CreateService(db);

        Assert.Equal(TeacherLevelGrantOutcome.Granted,
            await service.GrantAsync(scene.TeacherId, scene.LevelA, scene.AdminUserId, CancellationToken.None));
        Assert.Equal(TeacherLevelGrantOutcome.AlreadyGranted,
            await service.GrantAsync(scene.TeacherId, scene.LevelA, scene.AdminUserId, CancellationToken.None));

        await using var verify = _fixture.CreateContext();
        Assert.Equal(1, await verify.TeacherLevelAssignments.CountAsync(
            a => a.TeacherId == scene.TeacherId && a.LevelId == scene.LevelA));
    }

    /// <summary>ux_teacher_level, not the service's own read, is what makes a
    /// duplicate grant impossible under a genuine race.</summary>
    [Fact]
    public async Task Two_concurrent_grants_of_the_same_level_still_produce_exactly_one_row()
    {
        await using var seed = _fixture.CreateContext();
        var scene = await SeedAsync(seed);

        await using var dbA = _fixture.CreateContext();
        await using var dbB = _fixture.CreateContext();

        var results = await Task.WhenAll(
            CreateService(dbA).GrantAsync(scene.TeacherId, scene.LevelA, scene.AdminUserId, CancellationToken.None),
            CreateService(dbB).GrantAsync(scene.TeacherId, scene.LevelA, scene.AdminUserId, CancellationToken.None));

        Assert.Contains(TeacherLevelGrantOutcome.Granted, results);
        Assert.All(results, r => Assert.True(
            r is TeacherLevelGrantOutcome.Granted or TeacherLevelGrantOutcome.AlreadyGranted));

        await using var verify = _fixture.CreateContext();
        Assert.Equal(1, await verify.TeacherLevelAssignments.CountAsync(
            a => a.TeacherId == scene.TeacherId && a.LevelId == scene.LevelA));
    }

    [Fact]
    public async Task Revoking_removes_the_authorization()
    {
        await using var db = _fixture.CreateContext();
        var scene = await SeedAsync(db);
        var service = CreateService(db);

        await service.GrantAsync(scene.TeacherId, scene.LevelA, scene.AdminUserId, CancellationToken.None);
        var outcome = await service.RevokeAsync(scene.TeacherId, scene.LevelA, scene.AdminUserId, CancellationToken.None);

        Assert.Equal(TeacherLevelRevokeOutcome.Revoked, outcome);
        Assert.False(await service.IsAuthorizedForLevelAsync(scene.TeacherId, scene.LevelA, CancellationToken.None));
    }

    /// <summary>Owner decision 2026-08-30 rule 6: "audit-log changes."</summary>
    [Fact]
    public async Task Granting_and_revoking_a_level_are_both_audit_logged()
    {
        await using var db = _fixture.CreateContext();
        var scene = await SeedAsync(db);
        var service = CreateService(db);

        await service.GrantAsync(scene.TeacherId, scene.LevelA, scene.AdminUserId, CancellationToken.None);
        await service.RevokeAsync(scene.TeacherId, scene.LevelA, scene.AdminUserId, CancellationToken.None);

        await using var verify = _fixture.CreateContext();
        var entries = await verify.AuditLogEntries
            .Where(a => a.EntityType == "Teacher" && a.EntityId == scene.TeacherId.ToString())
            .OrderBy(a => a.Id)
            .ToListAsync();

        Assert.Equal(2, entries.Count);
        Assert.Equal("LevelGranted", entries[0].Action);
        Assert.Equal("LevelRevoked", entries[1].Action);
        Assert.Equal(scene.AdminUserId, entries[0].PerformedByUserId);
        Assert.Equal(scene.AdminUserId, entries[1].PerformedByUserId);
    }

    [Fact]
    public async Task Revoking_a_level_that_was_never_granted_is_reported_not_thrown()
    {
        await using var db = _fixture.CreateContext();
        var scene = await SeedAsync(db);

        Assert.Equal(TeacherLevelRevokeOutcome.NotGranted,
            await CreateService(db).RevokeAsync(scene.TeacherId, scene.LevelB, scene.AdminUserId, CancellationToken.None));
    }

    [Fact]
    public async Task Granting_for_an_unknown_teacher_or_level_is_refused()
    {
        await using var db = _fixture.CreateContext();
        var scene = await SeedAsync(db);
        var service = CreateService(db);

        Assert.Equal(TeacherLevelGrantOutcome.TeacherNotFound,
            await service.GrantAsync(-1, scene.LevelA, scene.AdminUserId, CancellationToken.None));
        Assert.Equal(TeacherLevelGrantOutcome.LevelNotFound,
            await service.GrantAsync(scene.TeacherId, -1, scene.AdminUserId, CancellationToken.None));
    }

    [Fact]
    public async Task Permitted_levels_lists_exactly_what_was_granted()
    {
        await using var db = _fixture.CreateContext();
        var scene = await SeedAsync(db);
        var service = CreateService(db);

        await service.GrantAsync(scene.TeacherId, scene.LevelA, scene.AdminUserId, CancellationToken.None);
        await service.GrantAsync(scene.TeacherId, scene.LevelB, scene.AdminUserId, CancellationToken.None);

        var permitted = await service.GetPermittedLevelIdsAsync(scene.TeacherId, CancellationToken.None);

        Assert.Equal(2, permitted.Count);
        Assert.Contains(scene.LevelA, permitted);
        Assert.Contains(scene.LevelB, permitted);
    }
}
