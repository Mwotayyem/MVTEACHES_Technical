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
}
