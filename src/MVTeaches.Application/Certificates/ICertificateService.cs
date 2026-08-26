namespace MVTeaches.Application.Certificates;

public enum IssueCertificateOutcome
{
    Issued,
    AlreadyIssued,

    /// <summary>§27.4/Q-27: reaching the hour threshold is necessary but not
    /// sufficient — issuance still needs an explicit admin approval action,
    /// and this outcome means that necessary condition itself isn't met yet.</summary>
    NotEligible,
}

public record IssueCertificateResult(IssueCertificateOutcome Outcome, long? CertificateId = null, string? CertificateNumber = null);

/// <summary>A read-only projection for an eventual "eligible for certificate"
/// admin list (§27.4) — never persisted itself; MinutesCompleted/RequiredMinutes
/// are both live values (D-65), recomputed on every read.</summary>
public record CertificateEligibility(long StudentId, int LevelId, long CourseId, int MinutesCompleted, int RequiredMinutes, bool IsEligible);

/// <summary>
/// Technical Study §27.1/§27.2 (D-30/D-51/D-85) and Q-27's resolution
/// ("الشهادة باعتماد" — certificate issuance is always by admin approval,
/// never automatic). CONF-03's resolution is load-bearing here: progress
/// accumulates on (student, level, course), never on a subscription — a
/// student can cross a course, subscription, or even guardian-purchase
/// boundary and their hours still count (D-51).
/// </summary>
public interface ICertificateService
{
    /// <summary>§27.2: "recomputed on every delivery verification, NEVER on
    /// Subscription state." Recomputes LevelProgress, from scratch, for every
    /// student who attended this session — a pure materialized-view refresh
    /// that never issues a certificate by itself. Safe to call redundantly;
    /// idempotent by construction (a full recompute, not an increment).</summary>
    Task RecomputeLevelProgressForSessionAsync(long sessionId, CancellationToken cancellationToken);

    Task<CertificateEligibility> GetEligibilityAsync(long studentId, int levelId, long courseId, CancellationToken cancellationToken);

    /// <summary>Q-27: always an explicit admin action — this method is the
    /// only way a Certificate row is ever created; nothing in this codebase
    /// issues one automatically just because a threshold was crossed.</summary>
    Task<IssueCertificateResult> IssueAsync(long studentId, int levelId, long courseId, long issuedByUserId, CancellationToken cancellationToken);

    Task RevokeAsync(long certificateId, CancellationToken cancellationToken);
}
