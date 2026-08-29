using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Ledger;
using MVTeaches.Domain.Audit;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Ledger;
using MVTeaches.Domain.Subscriptions;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;

namespace MVTeaches.Infrastructure.Ledger;

/// <inheritdoc cref="IEntitlementTransferService"/>
public class EntitlementTransferService : IEntitlementTransferService
{
    private readonly MvTeachesDbContext _db;
    private readonly IClock _clock;

    public EntitlementTransferService(MvTeachesDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<IReadOnlyList<LockedEntitlementBalance>> GetLockedBalancesAsync(long studentId, CancellationToken cancellationToken)
    {
        var currentLevelId = await _db.StudentLevels
            .Where(l => l.StudentId == studentId && l.IsCurrent)
            .Select(l => (int?)l.LevelId)
            .FirstOrDefaultAsync(cancellationToken);

        var buckets = await _db.EntitlementLedgerEntries
            .Where(l => l.StudentId == studentId)
            .GroupBy(l => new { l.CourseId, l.LevelId, l.SessionType })
            .Select(g => new { g.Key.CourseId, g.Key.LevelId, g.Key.SessionType, Balance = g.Sum(x => x.DeltaMinutes) })
            .Where(b => b.Balance > 0)
            .ToListAsync(cancellationToken);

        // A bucket that matches the student's current level is normal, usable
        // balance, not a "locked" one — only the mismatched ones belong here.
        return buckets
            .Where(b => b.LevelId != currentLevelId)
            .Select(b => new LockedEntitlementBalance(b.CourseId, b.LevelId, b.SessionType, b.Balance))
            .ToList();
    }

    public async Task<TransferMinutesResult> TransferMinutesAsync(long studentId, long courseId, int fromLevelId,
        SessionType sessionType, int minutes, long performedByAdminUserId, string reason, CancellationToken cancellationToken)
    {
        if (minutes <= 0)
        {
            return new TransferMinutesResult(TransferMinutesOutcome.InvalidMinutes);
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("An entitlement transfer requires a reason (owner decision 2026-08-30 rule 5).", nameof(reason));
        }

        if (!await _db.Students.AnyAsync(s => s.Id == studentId, cancellationToken))
        {
            return new TransferMinutesResult(TransferMinutesOutcome.StudentNotFound);
        }

        var currentLevelId = await _db.StudentLevels
            .Where(l => l.StudentId == studentId && l.IsCurrent)
            .Select(l => (int?)l.LevelId)
            .FirstOrDefaultAsync(cancellationToken);
        if (currentLevelId is null)
        {
            return new TransferMinutesResult(TransferMinutesOutcome.NoCurrentLevelAssigned);
        }

        if (fromLevelId == currentLevelId.Value)
        {
            return new TransferMinutesResult(TransferMinutesOutcome.FromLevelIsCurrentLevel);
        }

        var toLevelId = currentLevelId.Value;
        var now = _clock.GetCurrentInstant();

        // Serializes this student's own concurrent transfers/bookings/joins
        // against each other — the same row-lock-then-recompute shape
        // StudentBookingService.BookSessionAsync already uses for its own
        // package-limit check, for the same reason: a plain read-then-write
        // here would let two concurrent transfers each individually pass a
        // stale balance check and together move more than was ever locked.
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM students WHERE \"Id\" = {studentId} FOR UPDATE", cancellationToken);

        // The destination must be an EXISTING active package/entitlement at
        // the student's current level — this call moves minutes into one,
        // it never fabricates one (owner decision 2026-08-30 rule 5).
        var destination = await _db.Subscriptions
            .Where(s => s.StudentId == studentId && s.CourseId == courseId && s.LevelId == toLevelId
                        && s.SessionType == sessionType && s.Status == SubscriptionStatus.Active)
            .OrderBy(s => s.ExpiresOn)
            .FirstOrDefaultAsync(cancellationToken);
        if (destination is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new TransferMinutesResult(TransferMinutesOutcome.NoEligibleDestinationSubscription);
        }

        // Debit source subscriptions oldest-expiry-first, exactly the same
        // FIFO order JoinAttendanceService.FindConsumableSubscriptionAsync
        // already uses to pick which subscription a Join draws down — so a
        // transfer depletes locked minutes in the same order a Join would
        // have, had the level never changed.
        var sourceSubscriptions = await _db.Subscriptions
            .Where(s => s.StudentId == studentId && s.CourseId == courseId && s.LevelId == fromLevelId
                        && s.SessionType == sessionType && s.Status == SubscriptionStatus.Active)
            .OrderBy(s => s.ExpiresOn)
            .ToListAsync(cancellationToken);

        var remainingToDebit = minutes;
        var debitedFromSubscriptionIds = new List<long>();
        foreach (var source in sourceSubscriptions)
        {
            if (remainingToDebit <= 0)
            {
                break;
            }

            var sourceBalance = await _db.EntitlementLedgerEntries
                .Where(l => l.SubscriptionId == source.Id)
                .SumAsync(l => (int?)l.DeltaMinutes, cancellationToken) ?? 0;
            if (sourceBalance <= 0)
            {
                continue;
            }

            var takeFromThis = Math.Min(sourceBalance, remainingToDebit);
            _db.EntitlementLedgerEntries.Add(EntitlementLedgerEntry.ForAdminAdjustment(
                studentId, source.Id, courseId, fromLevelId, sessionType, -takeFromThis, performedByAdminUserId,
                $"Level transfer out (to level {toLevelId}): {reason}", now));
            remainingToDebit -= takeFromThis;
            debitedFromSubscriptionIds.Add(source.Id);
        }

        if (remainingToDebit > 0)
        {
            // The freshly-recomputed, lock-protected total fell short of what
            // was requested — never partially transfer.
            await transaction.RollbackAsync(cancellationToken);
            return new TransferMinutesResult(TransferMinutesOutcome.InsufficientLockedBalance);
        }

        _db.EntitlementLedgerEntries.Add(EntitlementLedgerEntry.ForAdminAdjustment(
            studentId, destination.Id, courseId, toLevelId, sessionType, minutes, performedByAdminUserId,
            $"Level transfer in (from level {fromLevelId}): {reason}", now));

        _db.AuditLogEntries.Add(new AuditLogEntry("Student", studentId.ToString(), "EntitlementLevelTransfer",
            performedByAdminUserId, reason,
            beforeJson: JsonSerializer.Serialize(new { CourseId = courseId, FromLevelId = fromLevelId, SessionType = sessionType, SourceSubscriptionIds = debitedFromSubscriptionIds }),
            afterJson: JsonSerializer.Serialize(new { CourseId = courseId, ToLevelId = toLevelId, SessionType = sessionType, DestinationSubscriptionId = destination.Id, Minutes = minutes }),
            now));

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new TransferMinutesResult(TransferMinutesOutcome.Transferred, minutes);
    }
}
