using NodaTime;

namespace MVTeaches.Domain.Audit;

/// <summary>
/// Technical Study §22 (security review), §31.4, §19.5. Append-only (§33.6).
/// <see cref="BeforeJson"/>/<see cref="AfterJson"/> must be produced through an
/// ALLOW-LIST of auditable fields, never a raw entity dump — the study is
/// explicit that secrets (passwords, OTPs, card numbers, API keys) must never
/// land here. That allow-listing happens in the Application-layer audit writer,
/// not in this class, but this class carries no field that could tempt someone
/// into logging a raw payload.
/// </summary>
public class AuditLogEntry
{
    public long Id { get; private set; }

    public string EntityType { get; private set; } = string.Empty;
    public string EntityId { get; private set; } = string.Empty;
    public string Action { get; private set; } = string.Empty;

    public long? PerformedByUserId { get; private set; }
    public string? Reason { get; private set; }

    /// <summary>Allow-listed field snapshots only — see the type's own remarks.</summary>
    public string? BeforeJson { get; private set; }
    public string? AfterJson { get; private set; }

    public Instant OccurredAtUtc { get; private set; }

    private AuditLogEntry() { }

    public AuditLogEntry(string entityType, string entityId, string action, long? performedByUserId,
        string? reason, string? beforeJson, string? afterJson, Instant occurredAtUtc)
    {
        EntityType = entityType;
        EntityId = entityId;
        Action = action;
        PerformedByUserId = performedByUserId;
        Reason = reason;
        BeforeJson = beforeJson;
        AfterJson = afterJson;
        OccurredAtUtc = occurredAtUtc;
    }
}
