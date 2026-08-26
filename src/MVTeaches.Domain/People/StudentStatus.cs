namespace MVTeaches.Domain.People;

/// <summary>
/// Technical Study §8.1 — the exact seven states. Do not add states here without
/// an approved decision; this enum is a direct mirror of the documented list.
/// </summary>
public enum StudentStatus
{
    /// <summary>Phone not yet verified. Can do effectively nothing.</summary>
    PendingVerification,

    /// <summary>Verified, no level assigned yet. May book a placement interview
    /// (D-48) but cannot subscribe to a specific level.</summary>
    PendingLevel,

    /// <summary>Approved level + active subscription. Full access.</summary>
    Active,

    /// <summary>Outstanding balance (D-14). Can view data and upload payment
    /// proof, but is blocked from attending (blocked from pressing Join).</summary>
    PaymentBlocked,

    /// <summary>Package validity window elapsed (D-64). May purchase new hours.</summary>
    Expired,

    /// <summary>Administrative suspension. Requires an audited reason.</summary>
    Suspended,

    /// <summary>Migrated from legacy records (D-32); entitlement preserved,
    /// waiting for account activation.</summary>
    Migrated,
}
