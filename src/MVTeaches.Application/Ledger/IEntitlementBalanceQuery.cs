namespace MVTeaches.Application.Ledger;

/// <summary>
/// D-36 / §20.2: a remaining balance is ALWAYS SUM(delta_minutes) computed at
/// read time — never a stored counter — named directly in
/// EntitlementLedgerEntry's own doc comment as the intended read path.
/// JoinAttendanceService already computes this inline for its own
/// sufficient-balance check; this interface exists so every OTHER caller
/// (admin screens, reports) reads a balance the exact same way instead of
/// re-deriving the SUM query ad hoc.
/// </summary>
public interface IEntitlementBalanceQuery
{
    Task<int> GetSubscriptionBalanceAsync(long subscriptionId, CancellationToken cancellationToken);

    /// <summary>The same SUM, for many subscriptions in one read. A register
    /// listing every student would otherwise have to call the single-id method
    /// once per subscription, or re-derive the SUM inline in a page — which is
    /// the exact thing this interface exists to prevent. Subscriptions with no
    /// ledger entry are returned as 0, so every requested id is present in the
    /// result.</summary>
    Task<IReadOnlyDictionary<long, int>> GetSubscriptionBalancesAsync(
        IReadOnlyCollection<long> subscriptionIds, CancellationToken cancellationToken);
}
