using NodaTime;

namespace MVTeaches.Domain.Settings;

/// <summary>
/// One row per <see cref="SettingKey"/>, stored as text and parsed by the typed
/// reader (ISettingsProvider in Application). §19.5: "every edit is audited —
/// who changed it, when, from which value to which value" — that audit trail
/// lives in AuditLogEntry, not here; this class only carries the current value.
/// </summary>
public class Setting
{
    public SettingKey Key { get; private set; }
    public string Value { get; private set; } = string.Empty;
    public long? LastUpdatedByUserId { get; private set; }
    public Instant? LastUpdatedAtUtc { get; private set; }

    private Setting() { }

    public Setting(SettingKey key, string value)
    {
        Key = key;
        Value = value;
    }

    public string PreviousValueForAudit { get; private set; } = string.Empty;

    public void UpdateValue(string newValue, long updatedByUserId, Instant nowUtc)
    {
        PreviousValueForAudit = Value;
        Value = newValue;
        LastUpdatedByUserId = updatedByUserId;
        LastUpdatedAtUtc = nowUtc;
    }
}
