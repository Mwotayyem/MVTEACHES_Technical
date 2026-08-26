using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Settings;
using MVTeaches.Domain.Audit;
using MVTeaches.Domain.Settings;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;

namespace MVTeaches.Infrastructure.Settings;

public class SettingsProvider : ISettingsProvider
{
    private readonly MvTeachesDbContext _db;
    private readonly IClock _clock;

    public SettingsProvider(MvTeachesDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<int> GetIntAsync(SettingKey key, CancellationToken cancellationToken)
    {
        var setting = await _db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        if (setting is null)
        {
            // The row must have been seeded (§19.5) — a missing row is a
            // deployment bug, not a case to silently default around, per the
            // study's own warning against silent fallback-on-typo behavior.
            throw new InvalidOperationException($"Setting '{key}' has not been seeded. This is a deployment defect.");
        }

        if (!int.TryParse(setting.Value, out var value))
        {
            throw new InvalidOperationException($"Setting '{key}' has a non-integer value '{setting.Value}'.");
        }

        return value;
    }

    public async Task SetAsync(SettingKey key, string newValue, long updatedByUserId, CancellationToken cancellationToken)
    {
        var setting = await _db.Settings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken)
            ?? throw new InvalidOperationException($"Setting '{key}' has not been seeded.");

        var oldValue = setting.Value;
        var now = _clock.GetCurrentInstant();
        setting.UpdateValue(newValue, updatedByUserId, now);

        // §31.4: every settings change is audited with old → new value —
        // never silently applied.
        _db.AuditLogEntries.Add(new AuditLogEntry(
            entityType: "Setting", entityId: key.ToString(), action: "Update",
            performedByUserId: updatedByUserId, reason: null,
            beforeJson: $"{{\"value\":\"{oldValue}\"}}", afterJson: $"{{\"value\":\"{newValue}\"}}",
            occurredAtUtc: now));

        await _db.SaveChangesAsync(cancellationToken);
    }
}
