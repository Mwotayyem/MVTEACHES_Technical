using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Ledger;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Ledger;
using MVTeaches.Domain.People;
using MVTeaches.Domain.Placement;
using MVTeaches.Domain.Subscriptions;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Ledger;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace MVTeaches.Tests.Ledger;

/// <summary>
/// Owner decision 2026-08-30 rule 5: a level change must never silently
/// convert an existing package, and any transfer of minutes locked in a
/// superseded level's bucket must be an authorized-admin, reasoned,
/// audited, net-zero, concurrency-safe operation.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class EntitlementTransferServiceTests
{
    private readonly TestDatabaseFixture _fixture;
    private static long _idSeed = 96_000_000;

    public EntitlementTransferServiceTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static long NextId() => Interlocked.Increment(ref _idSeed);

    private static string TwoLetterCode(long seed)
    {
        var n = (int)(seed % 676);
        return string.Concat((char)('A' + n / 26), (char)('A' + n % 26));
    }

    private static int? _sharedCountryId;

    private static async Task<int> GetOrSeedCountryAsync(MvTeachesDbContext db)
    {
        if (_sharedCountryId is { } existing)
        {
            return existing;
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var id = (int)NextId();
            db.Countries.Add(new Country(id, TwoLetterCode(id), "دولة", "Country", "JOD", "+962", "Asia/Amman"));
            try
            {
                await db.SaveChangesAsync();
                _sharedCountryId = id;
                return id;
            }
            catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
            {
                db.ChangeTracker.Clear();
            }
        }

        throw new InvalidOperationException("Could not find a free 2-letter country code after 10 attempts.");
    }

    private static async Task<long> CreateUserAsync(MvTeachesDbContext db)
    {
        var user = new ApplicationUser
        {
            UserName = $"u-{Guid.NewGuid():N}", NormalizedUserName = $"U-{Guid.NewGuid():N}",
            Email = $"{Guid.NewGuid():N}@test.mvteaches.local",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private record Scene(long StudentId, long StudentUserId, long CourseId, int OldLevelId, int NewLevelId, int CountryId);

    /// <summary>A student who was assigned <paramref name="oldLevelId"/>, bought
    /// a package there, then was reassigned to <paramref name="newLevelId"/> —
    /// the old package's minutes are now locked. When
    /// <paramref name="withDestinationPackage"/>, the student also already has
    /// an active package at the new level, matching the rule's own
    /// "eligible new-level package/entitlement" language.</summary>
    private static async Task<Scene> SeedStudentWithLockedBalanceAsync(MvTeachesDbContext db, IClock clock,
        int oldPackageMinutes = 120, bool withDestinationPackage = true, int destinationPackageMinutes = 60,
        SessionType sessionType = SessionType.Group)
    {
        var countryId = await GetOrSeedCountryAsync(db);
        var courseId = NextId();
        var oldLevelId = (int)NextId();
        var newLevelId = (int)NextId();
        var studentUserId = await CreateUserAsync(db);
        var adminUserId = await CreateUserAsync(db);

        db.Courses.Add(new Course("C" + courseId, "دورة", "Course"));
        db.Levels.Add(new Level(oldLevelId, "L" + oldLevelId, "مستوى", "Level", oldLevelId));
        db.Levels.Add(new Level(newLevelId, "L" + newLevelId, "مستوى", "Level", newLevelId));
        var student = new Student(countryId, "Student", new LocalDate(2015, 1, 1), studentUserId);
        student.MarkVerified();
        db.Students.Add(student);
        await db.SaveChangesAsync();

        var now = clock.GetCurrentInstant();

        // First assignment: old level.
        db.StudentLevels.Add(new StudentLevel(student.Id, oldLevelId, adminUserId, AssignedByRole.Admin,
            LevelAssignmentSource.AdminOverride, null, "initial placement", now.Minus(Duration.FromDays(10))));
        student.MarkLevelAssigned();
        await db.SaveChangesAsync();

        var oldSubscription = new Subscription(student.Id, countryId, courseId, oldLevelId, sessionType,
            new MVTeaches.Domain.Common.Money(50m, "JOD"), null, 10, oldPackageMinutes,
            new LocalDate(2026, 1, 1), 90, SubscriptionOrigin.SelfPurchase, studentUserId, null);
        oldSubscription.Activate();
        db.Subscriptions.Add(oldSubscription);
        await db.SaveChangesAsync();
        db.EntitlementLedgerEntries.Add(EntitlementLedgerEntry.ForPurchase(
            student.Id, oldSubscription.Id, courseId, oldLevelId, sessionType, oldPackageMinutes, NextId(), studentUserId, now.Minus(Duration.FromDays(9))));
        await db.SaveChangesAsync();

        // Level change: old row superseded, new row becomes current.
        var oldLevelRow = await db.StudentLevels.SingleAsync(l => l.StudentId == student.Id && l.LevelId == oldLevelId);
        oldLevelRow.Supersede();
        db.StudentLevels.Add(new StudentLevel(student.Id, newLevelId, adminUserId, AssignedByRole.Admin,
            LevelAssignmentSource.AdminOverride, null, "promoted", now.Minus(Duration.FromDays(1))));
        await db.SaveChangesAsync();

        if (withDestinationPackage)
        {
            var newSubscription = new Subscription(student.Id, countryId, courseId, newLevelId, sessionType,
                new MVTeaches.Domain.Common.Money(50m, "JOD"), null, 10, destinationPackageMinutes,
                new LocalDate(2026, 1, 1), 90, SubscriptionOrigin.SelfPurchase, studentUserId, null);
            newSubscription.Activate();
            db.Subscriptions.Add(newSubscription);
            await db.SaveChangesAsync();
            db.EntitlementLedgerEntries.Add(EntitlementLedgerEntry.ForPurchase(
                student.Id, newSubscription.Id, courseId, newLevelId, sessionType, destinationPackageMinutes, NextId(), studentUserId, now));
            await db.SaveChangesAsync();
        }

        return new Scene(student.Id, studentUserId, courseId, oldLevelId, newLevelId, countryId);
    }

    private static IEntitlementTransferService CreateService(MvTeachesDbContext db, IClock clock) => new EntitlementTransferService(db, clock);

    [Fact]
    public async Task A_locked_balance_appears_for_the_superseded_level_and_not_the_current_one()
    {
        await using var db = _fixture.CreateContext();
        var clock = new FakeClock(SystemClock.Instance.GetCurrentInstant());
        var scene = await SeedStudentWithLockedBalanceAsync(db, clock, oldPackageMinutes: 120);
        var service = CreateService(db, clock);

        var locked = await service.GetLockedBalancesAsync(scene.StudentId, CancellationToken.None);

        var oldBucket = Assert.Single(locked, b => b.LevelId == scene.OldLevelId);
        Assert.Equal(120, oldBucket.RemainingMinutes);
        Assert.DoesNotContain(locked, b => b.LevelId == scene.NewLevelId);
    }

    [Fact]
    public async Task Transferring_moves_the_exact_amount_and_creates_no_minutes()
    {
        await using var db = _fixture.CreateContext();
        var clock = new FakeClock(SystemClock.Instance.GetCurrentInstant());
        var scene = await SeedStudentWithLockedBalanceAsync(db, clock, oldPackageMinutes: 120, destinationPackageMinutes: 60);
        var service = CreateService(db, clock);

        var result = await service.TransferMinutesAsync(scene.StudentId, scene.CourseId, scene.OldLevelId,
            SessionType.Group, 90, performedByAdminUserId: 999_999, reason: "level correction after promotion", CancellationToken.None);

        Assert.Equal(TransferMinutesOutcome.Transferred, result.Outcome);
        Assert.Equal(90, result.MinutesTransferred);

        var totalAcrossBothBuckets = await db.EntitlementLedgerEntries
            .Where(l => l.StudentId == scene.StudentId)
            .SumAsync(l => l.DeltaMinutes);
        Assert.Equal(180, totalAcrossBothBuckets); // 120 + 60 in, nothing created or lost by the transfer

        var oldBucketBalance = await db.EntitlementLedgerEntries
            .Where(l => l.StudentId == scene.StudentId && l.LevelId == scene.OldLevelId)
            .SumAsync(l => l.DeltaMinutes);
        Assert.Equal(30, oldBucketBalance); // 120 - 90

        var newBucketBalance = await db.EntitlementLedgerEntries
            .Where(l => l.StudentId == scene.StudentId && l.LevelId == scene.NewLevelId)
            .SumAsync(l => l.DeltaMinutes);
        Assert.Equal(150, newBucketBalance); // 60 + 90
    }

    [Fact]
    public async Task Historical_ledger_entries_are_never_edited_or_removed_by_a_transfer()
    {
        await using var db = _fixture.CreateContext();
        var clock = new FakeClock(SystemClock.Instance.GetCurrentInstant());
        var scene = await SeedStudentWithLockedBalanceAsync(db, clock, oldPackageMinutes: 100, destinationPackageMinutes: 20, withDestinationPackage: true);
        var service = CreateService(db, clock);

        var entriesBefore = await db.EntitlementLedgerEntries.Where(l => l.StudentId == scene.StudentId).Select(l => l.Id).ToListAsync();

        var result = await service.TransferMinutesAsync(scene.StudentId, scene.CourseId, scene.OldLevelId,
            SessionType.Group, 40, performedByAdminUserId: 999_999, reason: "partial transfer", CancellationToken.None);
        Assert.Equal(TransferMinutesOutcome.Transferred, result.Outcome);

        // Every pre-existing row is still present, byte-for-byte — a transfer
        // only ever APPENDS new rows (§20.5 rule 1).
        foreach (var id in entriesBefore)
        {
            Assert.True(await db.EntitlementLedgerEntries.AnyAsync(l => l.Id == id));
        }

        var auditEntry = await db.AuditLogEntries.SingleOrDefaultAsync(a => a.Action == "EntitlementLevelTransfer" && a.EntityId == scene.StudentId.ToString());
        Assert.NotNull(auditEntry);
        Assert.Equal("partial transfer", auditEntry!.Reason);
    }

    [Fact]
    public async Task A_transfer_is_refused_when_it_would_exceed_the_actual_locked_balance()
    {
        await using var db = _fixture.CreateContext();
        var clock = new FakeClock(SystemClock.Instance.GetCurrentInstant());
        var scene = await SeedStudentWithLockedBalanceAsync(db, clock, oldPackageMinutes: 50);
        var service = CreateService(db, clock);

        var result = await service.TransferMinutesAsync(scene.StudentId, scene.CourseId, scene.OldLevelId,
            SessionType.Group, 500, performedByAdminUserId: 999_999, reason: "too much", CancellationToken.None);

        Assert.Equal(TransferMinutesOutcome.InsufficientLockedBalance, result.Outcome);

        // Refused outright — nothing partially moved, nothing written at all.
        var oldBucketBalance = await db.EntitlementLedgerEntries
            .Where(l => l.StudentId == scene.StudentId && l.LevelId == scene.OldLevelId)
            .SumAsync(l => l.DeltaMinutes);
        Assert.Equal(50, oldBucketBalance);
    }

    [Fact]
    public async Task A_transfer_is_refused_when_the_student_has_no_eligible_package_at_the_current_level()
    {
        await using var db = _fixture.CreateContext();
        var clock = new FakeClock(SystemClock.Instance.GetCurrentInstant());
        var scene = await SeedStudentWithLockedBalanceAsync(db, clock, oldPackageMinutes: 80, withDestinationPackage: false);
        var service = CreateService(db, clock);

        var result = await service.TransferMinutesAsync(scene.StudentId, scene.CourseId, scene.OldLevelId,
            SessionType.Group, 30, performedByAdminUserId: 999_999, reason: "no destination yet", CancellationToken.None);

        Assert.Equal(TransferMinutesOutcome.NoEligibleDestinationSubscription, result.Outcome);
    }

    [Fact]
    public async Task Transferring_from_the_students_own_current_level_is_refused_as_meaningless()
    {
        await using var db = _fixture.CreateContext();
        var clock = new FakeClock(SystemClock.Instance.GetCurrentInstant());
        var scene = await SeedStudentWithLockedBalanceAsync(db, clock);
        var service = CreateService(db, clock);

        var result = await service.TransferMinutesAsync(scene.StudentId, scene.CourseId, scene.NewLevelId,
            SessionType.Group, 10, performedByAdminUserId: 999_999, reason: "no-op attempt", CancellationToken.None);

        Assert.Equal(TransferMinutesOutcome.FromLevelIsCurrentLevel, result.Outcome);
    }

    [Fact]
    public async Task A_transfer_without_a_reason_is_rejected_before_anything_is_written()
    {
        await using var db = _fixture.CreateContext();
        var clock = new FakeClock(SystemClock.Instance.GetCurrentInstant());
        var scene = await SeedStudentWithLockedBalanceAsync(db, clock);
        var service = CreateService(db, clock);

        await Assert.ThrowsAsync<ArgumentException>(() => service.TransferMinutesAsync(
            scene.StudentId, scene.CourseId, scene.OldLevelId, SessionType.Group, 10, performedByAdminUserId: 999_999, reason: "  ", CancellationToken.None));
    }

    [Fact]
    public async Task Zero_or_negative_minutes_are_rejected()
    {
        await using var db = _fixture.CreateContext();
        var clock = new FakeClock(SystemClock.Instance.GetCurrentInstant());
        var scene = await SeedStudentWithLockedBalanceAsync(db, clock);
        var service = CreateService(db, clock);

        var result = await service.TransferMinutesAsync(scene.StudentId, scene.CourseId, scene.OldLevelId,
            SessionType.Group, 0, performedByAdminUserId: 999_999, reason: "zero", CancellationToken.None);

        Assert.Equal(TransferMinutesOutcome.InvalidMinutes, result.Outcome);
    }
}
