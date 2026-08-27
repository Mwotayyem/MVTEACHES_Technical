using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Ledger;
using MVTeaches.Domain.Ledger;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;

namespace MVTeaches.Infrastructure.Ledger;

/// <inheritdoc cref="IMakeUpCreditService"/>
public class MakeUpCreditService : IMakeUpCreditService
{
    private readonly MvTeachesDbContext _db;
    private readonly IClock _clock;

    public MakeUpCreditService(MvTeachesDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task GrantAsync(long studentId, long courseId, int levelId, int minutes, LocalDate expiresOn,
        long performedByUserId, CancellationToken cancellationToken)
    {
        var now = _clock.GetCurrentInstant();
        var entry = EntitlementLedgerEntry.ForMakeUpGranted(studentId, courseId, levelId, minutes, performedByUserId, expiresOn, now);
        _db.EntitlementLedgerEntries.Add(entry);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PendingMakeUpCredit>> GetPendingQueueAsync(CancellationToken cancellationToken)
    {
        var asOf = _clock.GetCurrentInstant().InZone(DateTimeZoneProviders.Tzdb["Asia/Amman"]).Date;

        // "Pending" = not yet expired by the sweep below. There is no separate
        // "redeemed" flag (see IMakeUpCreditService's remarks on CONF-04) — this
        // queue is deliberately just "granted, deadline not yet passed", exactly
        // D-66's own description, nothing more assumed.
        var grants = await _db.EntitlementLedgerEntries
            .Where(l => l.Reason == LedgerReason.MakeUpGranted && l.ExpiresOn != null && l.ExpiresOn >= asOf)
            .OrderBy(l => l.ExpiresOn)
            .ToListAsync(cancellationToken);

        if (grants.Count == 0)
        {
            return Array.Empty<PendingMakeUpCredit>();
        }

        var studentIds = grants.Select(g => g.StudentId).Distinct().ToList();
        var namesByStudentId = await _db.Students
            .Where(s => studentIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.FullName, cancellationToken);

        return grants
            .Select(g => new PendingMakeUpCredit(
                g.Id, g.StudentId, namesByStudentId.GetValueOrDefault(g.StudentId, "?"),
                g.CourseId, g.LevelId, g.DeltaMinutes, g.ExpiresOn!.Value, g.CreatedAtUtc))
            .ToList();
    }

    public async Task<int> ExpireDueAsync(CancellationToken cancellationToken)
    {
        var nowUtc = _clock.GetCurrentInstant();
        var asOf = nowUtc.InZone(DateTimeZoneProviders.Tzdb["Asia/Amman"]).Date;

        var alreadyExpiredGrantIds = await _db.EntitlementLedgerEntries
            .Where(l => l.Reason == LedgerReason.MakeUpExpired && l.ReversesEntryId != null)
            .Select(l => l.ReversesEntryId!.Value)
            .ToListAsync(cancellationToken);

        var dueGrants = await _db.EntitlementLedgerEntries
            .Where(l => l.Reason == LedgerReason.MakeUpGranted && l.ExpiresOn != null && l.ExpiresOn < asOf
                        && !alreadyExpiredGrantIds.Contains(l.Id))
            .ToListAsync(cancellationToken);

        foreach (var grant in dueGrants)
        {
            _db.EntitlementLedgerEntries.Add(EntitlementLedgerEntry.ForMakeUpExpired(
                grant.StudentId, grant.CourseId, grant.LevelId, grant.DeltaMinutes, grant.Id, nowUtc));
        }

        if (dueGrants.Count > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }

        return dueGrants.Count;
    }
}
