using NodaTime;

namespace MVTeaches.Domain.Certificates;

public enum CertificateStatus
{
    Pending,
    Issued,
    Revoked,
}

/// <summary>
/// Technical Study §27.1/§27.2 (D-30/D-51, D-85). Free — no fee logic, no
/// "issued but unpaid" state exists or should ever be added (D-85 closed that
/// question permanently). Exactly one certificate per (student, level, course).
/// </summary>
public class Certificate
{
    public long Id { get; private set; }

    public long StudentId { get; private set; }
    public int LevelId { get; private set; }
    public long CourseId { get; private set; }

    public string CertificateNumber { get; private set; } = string.Empty;

    /// <summary>Snapshot at issuance — the eligibility threshold may change later
    /// (§19.5) but an issued certificate never gets re-evaluated.</summary>
    public int MinutesCompleted { get; private set; }

    public Instant IssuedAtUtc { get; private set; }

    /// <summary>NULL = automatic. §27.4 leaves auto-vs-approved open (UNKNOWN) —
    /// this column supports either without a schema change.</summary>
    public long? IssuedByUserId { get; private set; }

    public CertificateStatus Status { get; private set; } = CertificateStatus.Issued;
    public long? FileId { get; private set; }

    private Certificate() { }

    public Certificate(long studentId, int levelId, long courseId, string certificateNumber,
        int minutesCompleted, Instant issuedAtUtc, long? issuedByUserId)
    {
        if (string.IsNullOrWhiteSpace(certificateNumber))
        {
            throw new ArgumentException("A certificate number is required.", nameof(certificateNumber));
        }

        StudentId = studentId;
        LevelId = levelId;
        CourseId = courseId;
        CertificateNumber = certificateNumber;
        MinutesCompleted = minutesCompleted;
        IssuedAtUtc = issuedAtUtc;
        IssuedByUserId = issuedByUserId;
    }

    public void AttachFile(long fileId) => FileId = fileId;

    public void Revoke() => Status = CertificateStatus.Revoked;
}
