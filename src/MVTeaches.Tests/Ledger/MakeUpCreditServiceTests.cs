using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Ledger;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Ledger;
using MVTeaches.Domain.People;
using MVTeaches.Infrastructure.Ledger;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace MVTeaches.Tests.Ledger;

/// <summary>
/// D-19/D-20/D-63/D-66 — the standalone makeup-credit path (distinct from
/// ISessionCancellationService's "just move the enrollment" path), for the one
/// case the Technical Study reserves for the admin's own case-by-case judgment.
/// See IMakeUpCreditService's remarks on CONF-04 for the one deliberately
/// un-resolved limitation this does NOT attempt to paper over.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class MakeUpCreditServiceTests
{
    private readonly TestDatabaseFixture _fixture;
    private static long _idSeed = 52_000_000;

    public MakeUpCreditServiceTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static long NextId() => Interlocked.Increment(ref _idSeed);

    private static string TwoLetterCode(long seed)
    {
        var n = (int)(seed % 676);
        return string.Concat((char)('A' + n / 26), (char)('A' + n % 26));
    }

    /// <summary>One Country per test method, reused for every student that
    /// test seeds — TwoLetterCode's 676-slot space is shared with every other
    /// test class in the same run, so minimizing how many rows each test
    /// creates keeps the collision odds negligible instead of merely unlikely.</summary>
    private async Task<int> SeedCountryAsync(MvTeachesDbContext db)
    {
        var countryId = (int)NextId();
        db.Countries.Add(new Country(countryId, TwoLetterCode(countryId), "دولة", "Country", "JOD", "+962", "Asia/Amman"));
        await db.SaveChangesAsync();
        return countryId;
    }

    private async Task<long> SeedStudentAsync(MvTeachesDbContext db, int countryId, string label = "student")
    {
        var userId = await CreateUserAsync(db, label);
        var student = new Student(countryId, "Student " + label, new LocalDate(2015, 1, 1), userId);
        db.Students.Add(student);
        await db.SaveChangesAsync();
        return student.Id;
    }

    private static async Task<long> CreateUserAsync(MvTeachesDbContext db, string label)
    {
        var user = new Infrastructure.Identity.ApplicationUser
        {
            UserName = $"{label}-{Guid.NewGuid():N}",
            NormalizedUserName = $"{label}-{Guid.NewGuid():N}".ToUpperInvariant(),
            Email = $"{Guid.NewGuid():N}@test.mvteaches.local",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private MakeUpCreditService CreateService(MvTeachesDbContext db, Instant now) => new(db, new FakeClock(now));

    private static LocalDate TodayAmman(Instant now) => now.InZone(DateTimeZoneProviders.Tzdb["Asia/Amman"]).Date;

    [Fact]
    public async Task Granting_a_credit_creates_a_MakeUpGranted_entry_with_the_given_amount_and_expiry()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var countryId = await SeedCountryAsync(db);
        var studentId = await SeedStudentAsync(db, countryId);
        var expiresOn = TodayAmman(now).PlusDays(14);

        await CreateService(db, now).GrantAsync(studentId, courseId: NextId(), levelId: (int)NextId(),
            minutes: 60, expiresOn, performedByUserId: NextId(), CancellationToken.None);

        await using var verifyDb = _fixture.CreateContext();
        var entry = await verifyDb.EntitlementLedgerEntries.SingleAsync(l => l.StudentId == studentId);
        Assert.Equal(LedgerReason.MakeUpGranted, entry.Reason);
        Assert.Equal(60, entry.DeltaMinutes);
        Assert.Equal(expiresOn, entry.ExpiresOn);
        Assert.Null(entry.SubscriptionId); // deliberately floating — see CONF-04 remarks
    }

    [Fact]
    public async Task Pending_queue_lists_only_not_yet_expired_grants_soonest_first()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var service = CreateService(db, now);
        var today = TodayAmman(now);

        var countryId = await SeedCountryAsync(db);
        var soonStudent = await SeedStudentAsync(db, countryId, "soon");
        var laterStudent = await SeedStudentAsync(db, countryId, "later");
        var alreadyPastStudent = await SeedStudentAsync(db, countryId, "past");

        await service.GrantAsync(laterStudent, NextId(), (int)NextId(), 60, today.PlusDays(20), NextId(), CancellationToken.None);
        await service.GrantAsync(soonStudent, NextId(), (int)NextId(), 30, today.PlusDays(2), NextId(), CancellationToken.None);
        await service.GrantAsync(alreadyPastStudent, NextId(), (int)NextId(), 45, today.PlusDays(-3), NextId(), CancellationToken.None);

        var queue = await service.GetPendingQueueAsync(CancellationToken.None);
        var relevant = queue.Where(q => q.StudentId == soonStudent || q.StudentId == laterStudent || q.StudentId == alreadyPastStudent).ToList();

        Assert.Equal(2, relevant.Count); // the already-past one is excluded
        Assert.Equal(soonStudent, relevant[0].StudentId); // soonest expiry first
        Assert.Equal(laterStudent, relevant[1].StudentId);
    }

    [Fact]
    public async Task ExpireDueAsync_issues_a_MakeUpExpired_entry_for_a_past_due_grant()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var service = CreateService(db, now);
        var countryId = await SeedCountryAsync(db);
        var studentId = await SeedStudentAsync(db, countryId);
        var courseId = NextId();
        var levelId = (int)NextId();

        await service.GrantAsync(studentId, courseId, levelId, 60, TodayAmman(now).PlusDays(-1), NextId(), CancellationToken.None);

        // ExpireDueAsync sweeps the WHOLE table (a real production sweep must),
        // so its returned count is not exclusive to this test's own data in a
        // shared test database — assert on this student's own rows instead.
        await service.ExpireDueAsync(CancellationToken.None);

        await using var verifyDb = _fixture.CreateContext();
        var grant = await verifyDb.EntitlementLedgerEntries.SingleAsync(l => l.StudentId == studentId && l.Reason == LedgerReason.MakeUpGranted);
        var expiry = await verifyDb.EntitlementLedgerEntries.SingleAsync(l => l.StudentId == studentId && l.Reason == LedgerReason.MakeUpExpired);
        Assert.Equal(-60, expiry.DeltaMinutes);
        Assert.Equal(grant.Id, expiry.ReversesEntryId);
        Assert.Null(expiry.PerformedByUserId); // system-issued
    }

    [Fact]
    public async Task ExpireDueAsync_leaves_a_not_yet_due_grant_alone()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var service = CreateService(db, now);
        var countryId = await SeedCountryAsync(db);
        var studentId = await SeedStudentAsync(db, countryId);

        await service.GrantAsync(studentId, NextId(), (int)NextId(), 60, TodayAmman(now).PlusDays(30), NextId(), CancellationToken.None);

        await service.ExpireDueAsync(CancellationToken.None);

        await using var verifyDb = _fixture.CreateContext();
        Assert.False(await verifyDb.EntitlementLedgerEntries.AnyAsync(l => l.StudentId == studentId && l.Reason == LedgerReason.MakeUpExpired));
    }

    [Fact]
    public async Task ExpireDueAsync_is_idempotent_and_never_double_expires_the_same_grant()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var service = CreateService(db, now);
        var countryId = await SeedCountryAsync(db);
        var studentId = await SeedStudentAsync(db, countryId);

        await service.GrantAsync(studentId, NextId(), (int)NextId(), 60, TodayAmman(now).PlusDays(-1), NextId(), CancellationToken.None);

        await service.ExpireDueAsync(CancellationToken.None);
        await service.ExpireDueAsync(CancellationToken.None); // must not re-process the same grant a second time

        await using var verifyDb = _fixture.CreateContext();
        Assert.Equal(1, await verifyDb.EntitlementLedgerEntries.CountAsync(l => l.StudentId == studentId && l.Reason == LedgerReason.MakeUpExpired));
    }
}
