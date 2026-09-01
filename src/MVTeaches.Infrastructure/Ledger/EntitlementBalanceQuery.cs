using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Ledger;
using MVTeaches.Infrastructure.Persistence;

namespace MVTeaches.Infrastructure.Ledger;

/// <inheritdoc cref="IEntitlementBalanceQuery"/>
public class EntitlementBalanceQuery : IEntitlementBalanceQuery
{
    private readonly MvTeachesDbContext _db;

    public EntitlementBalanceQuery(MvTeachesDbContext db) => _db = db;

    public async Task<int> GetSubscriptionBalanceAsync(long subscriptionId, CancellationToken cancellationToken) =>
        await _db.EntitlementLedgerEntries
            .Where(l => l.SubscriptionId == subscriptionId)
            .SumAsync(l => (int?)l.DeltaMinutes, cancellationToken) ?? 0;

    public async Task<IReadOnlyDictionary<long, int>> GetSubscriptionBalancesAsync(
        IReadOnlyCollection<long> subscriptionIds, CancellationToken cancellationToken)
    {
        if (subscriptionIds.Count == 0)
        {
            return new Dictionary<long, int>();
        }

        // Identical formula to the single-id read above - SUM(delta_minutes),
        // computed now, never a stored counter (D-36 / 20.2).
        // EntitlementLedgerEntry.SubscriptionId is nullable (an entry can
        // exist without one), so the id list is nullable too and the null
        // group is dropped rather than counted against anybody.
        var ids = subscriptionIds.Select(id => (long?)id).ToList();
        var sums = await _db.EntitlementLedgerEntries
            .Where(l => ids.Contains(l.SubscriptionId))
            .GroupBy(l => l.SubscriptionId)
            .Select(g => new { SubscriptionId = g.Key, Minutes = g.Sum(l => l.DeltaMinutes) })
            .ToListAsync(cancellationToken);

        var balances = sums.Where(x => x.SubscriptionId is not null)
            .ToDictionary(x => x.SubscriptionId!.Value, x => x.Minutes);
        foreach (var id in subscriptionIds)
        {
            balances.TryAdd(id, 0);
        }
        return balances;
    }
}
