using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Certificates;
using MVTeaches.Application.Settings;
using MVTeaches.Domain.Certificates;
using MVTeaches.Domain.Delivery;
using MVTeaches.Domain.Settings;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;

namespace MVTeaches.Infrastructure.Certificates;

/// <summary>See ICertificateService's remarks — §27.1/§27.2/D-51/CONF-03/Q-27.</summary>
public class CertificateService : ICertificateService
{
    private readonly MvTeachesDbContext _db;
    private readonly ISettingsProvider _settings;
    private readonly IClock _clock;

    public CertificateService(MvTeachesDbContext db, ISettingsProvider settings, IClock clock)
    {
        _db = db;
        _settings = settings;
        _clock = clock;
    }

    public async Task RecomputeLevelProgressForSessionAsync(long sessionId, CancellationToken cancellationToken)
    {
        var session = await _db.ClassSessions.FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is null)
        {
            return;
        }

        // Only a Verified delivery contributes minutes at all — recomputing
        // against anything else would just recompute the same numbers, so this
        // is purely a wasted round trip to skip, not a correctness guard.
        var delivery = await _db.SessionDeliveries.AsNoTracking()
            .FirstOrDefaultAsync(d => d.SessionId == sessionId, cancellationToken);
        if (delivery is null || delivery.State != DeliveryState.Verified)
        {
            return;
        }

        // Every student who pressed Join for this session (D-83) — attendance
        // is independent of delivery verification, but a certificate only ever
        // counts hours for a session BOTH attended AND verified (§27.2).
        var studentIds = await _db.AttendanceRecords
            .Where(a => a.SessionId == sessionId)
            .Select(a => a.StudentId)
            .ToListAsync(cancellationToken);

        if (studentIds.Count == 0)
        {
            return;
        }

        var requiredMinutes = await _settings.GetIntAsync(SettingKey.CertificateRequiredHours, cancellationToken) * 60;
        var now = _clock.GetCurrentInstant();

        foreach (var studentId in studentIds)
        {
            await RecomputeOneAsync(studentId, session.LevelId, session.CourseId, requiredMinutes, now, cancellationToken);
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task RecomputeOneAsync(long studentId, int levelId, long courseId, int requiredMinutes, Instant now, CancellationToken ct)
    {
        // A full recompute from source rows — a materialized view, never an
        // incremental counter, precisely so it can never drift (§27.2).
        var minutes = await (
            from a in _db.AttendanceRecords
            join s in _db.ClassSessions on a.SessionId equals s.Id
            join d in _db.SessionDeliveries on s.Id equals d.SessionId
            where a.StudentId == studentId && s.LevelId == levelId && s.CourseId == courseId
                  && d.State == DeliveryState.Verified
            select (int?)s.DurationMinutes
        ).SumAsync(ct) ?? 0;

        var progress = await _db.LevelProgresses.FirstOrDefaultAsync(
            p => p.StudentId == studentId && p.LevelId == levelId && p.CourseId == courseId, ct);

        if (progress is null)
        {
            progress = new LevelProgress(studentId, levelId, courseId);
            _db.LevelProgresses.Add(progress);
        }

        // The FIRST moment the threshold was crossed, preserved across further
        // recomputes — deliveries only ever move forward (Declared -> Verified),
        // so minutes are monotonic in practice and this timestamp never needs
        // to move once set.
        var completedAtUtc = minutes >= requiredMinutes ? (progress.CompletedAtUtc ?? now) : (Instant?)null;
        progress.Recompute(minutes, completedAtUtc);
    }

    public async Task<CertificateEligibility> GetEligibilityAsync(long studentId, int levelId, long courseId, CancellationToken cancellationToken)
    {
        var requiredMinutes = await _settings.GetIntAsync(SettingKey.CertificateRequiredHours, cancellationToken) * 60;
        var progress = await _db.LevelProgresses.AsNoTracking().FirstOrDefaultAsync(
            p => p.StudentId == studentId && p.LevelId == levelId && p.CourseId == courseId, cancellationToken);
        var minutes = progress?.MinutesCompleted ?? 0;

        return new CertificateEligibility(studentId, levelId, courseId, minutes, requiredMinutes, minutes >= requiredMinutes);
    }

    public async Task<IssueCertificateResult> IssueAsync(long studentId, int levelId, long courseId, long issuedByUserId, CancellationToken cancellationToken)
    {
        // ⚠️ Exactly one certificate per (student, level, course) — the unique
        // index is the actual backstop; this check just gives a clean outcome
        // instead of an unhandled constraint violation on the common path.
        var alreadyIssued = await _db.Certificates.AnyAsync(
            c => c.StudentId == studentId && c.LevelId == levelId && c.CourseId == courseId, cancellationToken);
        if (alreadyIssued)
        {
            return new IssueCertificateResult(IssueCertificateOutcome.AlreadyIssued);
        }

        var eligibility = await GetEligibilityAsync(studentId, levelId, courseId, cancellationToken);
        if (!eligibility.IsEligible)
        {
            return new IssueCertificateResult(IssueCertificateOutcome.NotEligible);
        }

        var certificateNumber = GenerateCertificateNumber();
        var certificate = new Certificate(studentId, levelId, courseId, certificateNumber,
            eligibility.MinutesCompleted, _clock.GetCurrentInstant(), issuedByUserId);

        _db.Certificates.Add(certificate);
        await _db.SaveChangesAsync(cancellationToken);

        return new IssueCertificateResult(IssueCertificateOutcome.Issued, certificate.Id, certificateNumber);
    }

    public async Task RevokeAsync(long certificateId, CancellationToken cancellationToken)
    {
        var certificate = await _db.Certificates.FirstOrDefaultAsync(c => c.Id == certificateId, cancellationToken)
            ?? throw new InvalidOperationException($"Certificate {certificateId} not found.");
        certificate.Revoke();
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static string GenerateCertificateNumber() =>
        "MVT-CERT-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
}
