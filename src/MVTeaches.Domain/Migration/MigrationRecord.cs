using NodaTime;

namespace MVTeaches.Domain.Migration;

public enum MigrationRecordStatus
{
    Draft,
    Validated,
    Imported,
    Failed,
    RolledBack,
}

/// <summary>
/// Technical Study §25.3. <see cref="RawPayload"/> is non-negotiable (the study's
/// own words): when a migrated student disputes their balance six months later,
/// the raw data as it arrived is the only tiebreaker. Never omit it to save space.
/// </summary>
public class MigrationRecord
{
    public long Id { get; private set; }
    public Guid BatchId { get; private set; }

    public string Source { get; private set; } = string.Empty;
    public string? SourceReference { get; private set; }

    public long? StudentId { get; private set; }
    public long? GuardianId { get; private set; }

    /// <summary>The row exactly as it arrived (JSON) — never discarded.</summary>
    public string RawPayloadJson { get; private set; } = string.Empty;

    public string? LevelCode { get; private set; }
    public int? RemainingMinutes { get; private set; }
    public decimal? AmountPaid { get; private set; }
    public string? Currency { get; private set; }
    public DateOnly? PaidOn { get; private set; }
    public DateOnly? SubscriptionStart { get; private set; }
    public DateOnly? SubscriptionEnd { get; private set; }
    public string? Notes { get; private set; }

    public MigrationRecordStatus Status { get; private set; } = MigrationRecordStatus.Draft;
    public string? ErrorMessage { get; private set; }
    public long? ImportedByUserId { get; private set; }
    public Instant? ImportedAtUtc { get; private set; }

    private MigrationRecord() { }

    public MigrationRecord(Guid batchId, string source, string? sourceReference, string rawPayloadJson)
    {
        if (string.IsNullOrWhiteSpace(rawPayloadJson))
        {
            throw new ArgumentException("The raw payload must be preserved verbatim (§25.3).", nameof(rawPayloadJson));
        }

        BatchId = batchId;
        Source = source;
        SourceReference = sourceReference;
        RawPayloadJson = rawPayloadJson;
    }

    public void SetParsedFields(string? levelCode, int? remainingMinutes, decimal? amountPaid, string? currency,
        DateOnly? paidOn, DateOnly? subscriptionStart, DateOnly? subscriptionEnd, string? notes)
    {
        LevelCode = levelCode;
        RemainingMinutes = remainingMinutes;
        AmountPaid = amountPaid;
        Currency = currency;
        PaidOn = paidOn;
        SubscriptionStart = subscriptionStart;
        SubscriptionEnd = subscriptionEnd;
        Notes = notes;
    }

    public void MarkValidated() => Status = MigrationRecordStatus.Validated;

    public void MarkFailed(string errorMessage)
    {
        Status = MigrationRecordStatus.Failed;
        ErrorMessage = errorMessage;
    }

    public void MarkImported(long studentId, long? guardianId, long importedByUserId, Instant nowUtc)
    {
        StudentId = studentId;
        GuardianId = guardianId;
        ImportedByUserId = importedByUserId;
        ImportedAtUtc = nowUtc;
        Status = MigrationRecordStatus.Imported;
    }

    public void MarkRolledBack() => Status = MigrationRecordStatus.RolledBack;
}
