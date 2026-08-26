namespace MVTeaches.Domain.Ledger;

/// <summary>
/// Technical Study §20.2 — the exact, closed list of reasons. Do not add a
/// value here without an approved decision; new reasons are exactly the kind
/// of "new Business Rule" the owner has repeatedly instructed against.
/// </summary>
public enum LedgerReason
{
    /// <summary>+ Subscription purchase.</summary>
    Purchase,

    /// <summary>+ Admin-created subscription with no payment yet (D-13).</summary>
    AdminGrant,

    /// <summary>+ Opening balance for a migrated student (D-32).</summary>
    MigrationOpening,

    /// <summary>- The one and only debit path: a Join press (D-83).</summary>
    Consumption,

    /// <summary>+ Compensation — ONLY when no direct replacement session exists (D-19/D-20).</summary>
    MakeUpGranted,

    /// <summary>- A compensation grant's validity window elapsed.</summary>
    MakeUpExpired,

    /// <summary>- Package validity window elapsed (D-64).</summary>
    Expiry,

    /// <summary>± Manual administrative correction — a note is mandatory (§20.5 rule 3).</summary>
    AdminAdjustment,

    /// <summary>± Reverses an earlier, incorrect entry (§20.5 rule 2) — the error stays visible forever.</summary>
    Correction,
}
