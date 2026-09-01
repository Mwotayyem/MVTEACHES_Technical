using NodaTime;

namespace MVTeaches.Domain.Files;

/// <summary>What this file is for — drives the authorization chain (§26.2) and
/// which bucket/prefix it lives under in object storage.</summary>
public enum FilePurpose
{
    PaymentProof,
    HomeworkMaterial,
    HomeworkSubmission,
    Certificate,
    StudentDocument,
    TeacherDocument,

    /// <summary>Owner decision 2026-09-01: the image on an offer poster. Not
    /// a student document and not personal data - it is centre marketing
    /// material, and it has no owning student.</summary>
    PromotionalPoster,

    Other,
}

/// <summary>
/// Technical Study §26.2. Metadata only — the bytes live in object storage
/// (Cloudflare R2 per the infra study), addressed by a random <see cref="ObjectKey"/>,
/// never a predictable name like "student_5_homework_3.pdf" (the study's explicit
/// warning). Every read must go through a short-lived signed URL generated only
/// AFTER the full authorization chain is checked — never before.
/// </summary>
public class FileRecord
{
    public long Id { get; private set; }

    /// <summary>Random (UUID) — the whole point is that it must not be guessable.</summary>
    public Guid ObjectKey { get; private set; }

    public string OriginalFileName { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = string.Empty;
    public long SizeBytes { get; private set; }

    /// <summary>SHA-256 of the content — supports the "give me the file's
    /// fingerprint" requirement mentioned for payment proofs (D-56).</summary>
    public string Sha256Hash { get; private set; } = string.Empty;

    public FilePurpose Purpose { get; private set; }

    /// <summary>The student this file's authorization chain roots at, if any
    /// (§26.2's chain: file → homework → session → enrollment → student →
    /// [self] or [guardian] or [session teacher] or [admin]).</summary>
    public long? OwnerStudentId { get; private set; }

    public long UploadedByUserId { get; private set; }
    public Instant UploadedAtUtc { get; private set; }

    private FileRecord() { }

    public FileRecord(Guid objectKey, string originalFileName, string contentType, long sizeBytes,
        string sha256Hash, FilePurpose purpose, long uploadedByUserId, Instant uploadedAtUtc, long? ownerStudentId = null)
    {
        if (sizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeBytes));
        }

        ObjectKey = objectKey;
        OriginalFileName = originalFileName;
        ContentType = contentType;
        SizeBytes = sizeBytes;
        Sha256Hash = sha256Hash;
        Purpose = purpose;
        OwnerStudentId = ownerStudentId;
        UploadedByUserId = uploadedByUserId;
        UploadedAtUtc = uploadedAtUtc;
    }
}
