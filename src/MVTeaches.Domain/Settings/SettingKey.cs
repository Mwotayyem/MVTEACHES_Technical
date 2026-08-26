namespace MVTeaches.Domain.Settings;

/// <summary>
/// Technical Study §19.5 (D-65) — a CLOSED, pre-defined list of keys, deliberately
/// NOT a free-form key/value bag. A free key means a silent typo quietly falls
/// back to a default with no warning (the study's explicit reasoning). Add a
/// value here only when a real, approved decision introduces a new
/// admin-configurable number — never speculatively.
///
/// Snapshot vs live-read (§19.5's "القاعدة الحاسمة"): financial facts (price,
/// FX rate, a subscription's validity_days) are snapshotted onto the record at
/// the moment they matter and must NEVER re-read a setting. Every value below
/// is non-financial and is read live, every time, with no per-student copy.
/// </summary>
public enum SettingKey
{
    /// <summary>D-30/D-51/D-65. Global, not per-level, not per-student — see the
    /// note in Catalog/Level.cs for the contradiction this closed.</summary>
    CertificateRequiredHours,

    /// <summary>D-54.</summary>
    FreezesPerMonth,

    /// <summary>D-18.</summary>
    ExtensionsPerStudent,

    MaxChildrenPerGuardian,

    /// <summary>D-46.</summary>
    DefaultSessionMinutes,

    /// <summary>D-63 — an initial suggestion only; the admin can override per case.</summary>
    DefaultMakeUpExpiryDays,

    /// <summary>Dashboard §2.3 — "ending soon" threshold in days.</summary>
    DashboardEndingSoonDays,

    /// <summary>Dashboard §2.3 — "starting soon" threshold in days.</summary>
    DashboardStartingSoonDays,

    /// <summary>Dashboard §2.3 — "low remaining lessons" threshold.</summary>
    DashboardLowRemainingLessonsThreshold,

    /// <summary>§15.3 — the generator's horizon is documented as "8–12 weeks
    /// ahead", a range, not a fixed number; the admin picks the exact value
    /// within that range from the control panel, never a hardcoded constant.</summary>
    ScheduleGenerationHorizonWeeks,
}
