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
}
