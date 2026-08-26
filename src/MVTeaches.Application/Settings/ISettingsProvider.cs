using MVTeaches.Domain.Settings;

namespace MVTeaches.Application.Settings;

/// <summary>
/// §19.5 (D-65): every non-financial operational number is read live through
/// this interface — never a C# constant. Financial facts (price, FX rate,
/// a subscription's validity_days) are the one deliberate exception and are
/// snapshotted onto their own record instead of read through here (§19.5's
/// "القاعدة الحاسمة").
/// </summary>
public interface ISettingsProvider
{
    Task<int> GetIntAsync(SettingKey key, CancellationToken cancellationToken);

    /// <summary>§31.4/§19.5: every change is audited — who, when, old → new value.</summary>
    Task SetAsync(SettingKey key, string newValue, long updatedByUserId, CancellationToken cancellationToken);
}

/// <summary>The closed set of defaults (§19.5's table) — used only to seed the
/// `settings` table once; never read directly by business logic afterward.</summary>
public static class SettingDefaults
{
    public static readonly IReadOnlyDictionary<SettingKey, string> Values = new Dictionary<SettingKey, string>
    {
        [SettingKey.CertificateRequiredHours] = "30",
        [SettingKey.FreezesPerMonth] = "3",
        [SettingKey.ExtensionsPerStudent] = "1",
        [SettingKey.MaxChildrenPerGuardian] = "4",
        [SettingKey.DefaultSessionMinutes] = "60",
        [SettingKey.DefaultMakeUpExpiryDays] = "30",
        [SettingKey.DashboardEndingSoonDays] = "5",
        [SettingKey.DashboardStartingSoonDays] = "7",
        [SettingKey.DashboardLowRemainingLessonsThreshold] = "2",
    };
}
