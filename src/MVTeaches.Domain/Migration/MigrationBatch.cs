using NodaTime;

namespace MVTeaches.Domain.Migration;

public enum MigrationBatchStatus
{
    Uploaded,
    Validated,
    Previewed,
    Imported,
    RolledBack,
    Failed,
}

/// <summary>
/// Technical Study §25.4 (D-58). The header row a batch of MigrationRecord line
/// items belongs to. Migration is a first-class, transactional, reversible MVP
/// feature — not a manual side-channel operation (§25.5 rule 3: "always
/// reversible by batch_id").
/// </summary>
public class MigrationBatch
{
    public long Id { get; private set; }
    public Guid BatchId { get; private set; }

    public string SourceFileName { get; private set; } = string.Empty;
    public MigrationBatchStatus Status { get; private set; } = MigrationBatchStatus.Uploaded;

    public int TotalRows { get; private set; }
    public int ValidRows { get; private set; }
    public int ErrorRows { get; private set; }
    public int ImportedRows { get; private set; }

    public long UploadedByUserId { get; private set; }
    public Instant UploadedAtUtc { get; private set; }
    public Instant? ImportedAtUtc { get; private set; }
    public Instant? RolledBackAtUtc { get; private set; }

    private MigrationBatch() { }

    public MigrationBatch(string sourceFileName, long uploadedByUserId, Instant uploadedAtUtc)
    {
        BatchId = Guid.NewGuid();
        SourceFileName = sourceFileName;
        UploadedByUserId = uploadedByUserId;
        UploadedAtUtc = uploadedAtUtc;
    }

    public void RecordValidation(int totalRows, int validRows, int errorRows)
    {
        TotalRows = totalRows;
        ValidRows = validRows;
        ErrorRows = errorRows;
        Status = MigrationBatchStatus.Validated;
    }

    public void MarkPreviewed() => Status = MigrationBatchStatus.Previewed;

    public void MarkImported(int importedRows, Instant nowUtc)
    {
        ImportedRows = importedRows;
        ImportedAtUtc = nowUtc;
        Status = MigrationBatchStatus.Imported;
    }

    /// <summary>§25.5 rule 3: a single transaction per batch, reversible.</summary>
    public void RollBack(Instant nowUtc)
    {
        if (Status != MigrationBatchStatus.Imported)
        {
            throw new InvalidOperationException("Only an imported batch can be rolled back.");
        }

        RolledBackAtUtc = nowUtc;
        Status = MigrationBatchStatus.RolledBack;
    }

    public void MarkFailed() => Status = MigrationBatchStatus.Failed;
}
