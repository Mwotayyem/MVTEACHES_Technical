namespace MVTeaches.Application.Files;

public enum SaveUploadOutcome
{
    Saved,

    /// <summary>The file's REAL content (sniffed by magic bytes), not its
    /// extension or the browser's declared Content-Type, is not one of the
    /// allowed receipt formats (JPEG/PNG/PDF).</summary>
    RejectedContentType,

    RejectedTooLarge,

    /// <summary>An empty stream, or one that could not be read at all.</summary>
    RejectedEmpty,
}

public record SaveUploadResult(SaveUploadOutcome Outcome, long? DocumentId = null);

public record OpenedDocument(Stream Content, string ContentType, string OriginalFileName);

/// <summary>
/// Owner decision 2026-08-30 (receipt uploads): a small, private file store
/// built on top of §26.2's pre-existing FileRecord metadata table (whose
/// FilePurpose.PaymentProof case already existed, unused, before this) —
/// never a raw link under `wwwroot`, never trusting a browser-supplied
/// extension or Content-Type. <paramref name="purpose"/> below is the
/// string name of a MVTeaches.Domain.Files.FilePurpose value.
/// </summary>
public interface IFileStorageService
{
    /// <summary><paramref name="content"/> is fully read and validated
    /// (real content type by magic bytes, size limit) before anything is
    /// written to disk — a rejected upload leaves no trace.
    /// <paramref name="ownerStudentId"/> anchors §26.2's own authorization
    /// chain (file → student → self/guardian/admin) for this record.</summary>
    Task<SaveUploadResult> SaveAsync(Stream content, string purpose, string originalFileName, long uploadedByUserId,
        CancellationToken cancellationToken, long? ownerStudentId = null);

    /// <summary>Returns null if the document id doesn't exist. Callers are
    /// responsible for their own authorization check (this service has no
    /// notion of who is allowed to see which document) before ever calling this.</summary>
    Task<OpenedDocument?> OpenAsync(long documentId, CancellationToken cancellationToken);
}
