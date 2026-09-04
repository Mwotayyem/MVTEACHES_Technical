using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.People;
using MVTeaches.Domain.Audit;
using MVTeaches.Domain.People;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;

namespace MVTeaches.Infrastructure.People;

/// <summary>
/// Owner decision 2026-08-30 rule 5 — see ITeacherLevelAuthorizationService
/// for the rule itself and for why revoking never cascades into sessions.
/// </summary>
public class TeacherLevelAuthorizationService : ITeacherLevelAuthorizationService
{
    private readonly MvTeachesDbContext _db;
    private readonly IClock _clock;

    public TeacherLevelAuthorizationService(MvTeachesDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<TeacherLevelGrantOutcome> GrantAsync(long teacherId, long courseId, int levelId,
        long grantedByUserId, CancellationToken cancellationToken)
    {
        if (!await _db.Teachers.AnyAsync(t => t.Id == teacherId, cancellationToken))
        {
            return TeacherLevelGrantOutcome.TeacherNotFound;
        }

        if (!await _db.Courses.AnyAsync(c => c.Id == courseId, cancellationToken))
        {
            return TeacherLevelGrantOutcome.CourseNotFound;
        }

        if (!await _db.Levels.AnyAsync(l => l.Id == levelId, cancellationToken))
        {
            return TeacherLevelGrantOutcome.LevelNotFound;
        }

        var now = _clock.GetCurrentInstant();
        _db.TeacherLevelAssignments.Add(new TeacherLevelAssignment(teacherId, courseId, levelId, grantedByUserId, now));
        _db.AuditLogEntries.Add(new AuditLogEntry("Teacher", teacherId.ToString(), "LevelGranted",
            grantedByUserId, null, beforeJson: null,
            afterJson: $"{{\"courseId\":{courseId},\"levelId\":{levelId}}}", now));
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return TeacherLevelGrantOutcome.Granted;
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            // ux_teacher_level is the real guard against a duplicate grant
            // under concurrency; this makes the operation idempotent rather
            // than surfacing a constraint violation to the admin. The audit
            // entry for a grant that never actually happened is discarded
            // along with the rest of this batch by ChangeTracker.Clear().
            _db.ChangeTracker.Clear();
            return TeacherLevelGrantOutcome.AlreadyGranted;
        }
    }

    public async Task<TeacherLevelRevokeOutcome> RevokeAsync(long teacherId, long courseId, int levelId,
        long revokedByUserId, CancellationToken cancellationToken)
    {
        if (!await _db.Teachers.AnyAsync(t => t.Id == teacherId, cancellationToken))
        {
            return TeacherLevelRevokeOutcome.TeacherNotFound;
        }

        var assignment = await _db.TeacherLevelAssignments
            .FirstOrDefaultAsync(a => a.TeacherId == teacherId && a.CourseId == courseId && a.LevelId == levelId,
                cancellationToken);
        if (assignment is null)
        {
            return TeacherLevelRevokeOutcome.NotGranted;
        }

        _db.TeacherLevelAssignments.Remove(assignment);
        _db.AuditLogEntries.Add(new AuditLogEntry("Teacher", teacherId.ToString(), "LevelRevoked",
            revokedByUserId, null, beforeJson: $"{{\"courseId\":{courseId},\"levelId\":{levelId}}}", afterJson: null,
            _clock.GetCurrentInstant()));
        await _db.SaveChangesAsync(cancellationToken);
        return TeacherLevelRevokeOutcome.Revoked;
    }

    public Task<bool> IsAuthorizedForCourseLevelAsync(long teacherId, long courseId, int levelId,
        CancellationToken cancellationToken) =>
        _db.TeacherLevelAssignments.AnyAsync(
            a => a.TeacherId == teacherId && a.CourseId == courseId && a.LevelId == levelId, cancellationToken);

    /// <summary>Owner decision 2026-09-04: distinct level ids across ALL the
    /// teacher's courses. Kept level-only because its callers use it to build a
    /// level picker; the authoritative per-session check is
    /// IsAuthorizedForCourseLevelAsync, and this must never be mistaken for it.</summary>
    public async Task<IReadOnlyList<int>> GetPermittedLevelIdsAsync(long teacherId, CancellationToken cancellationToken) =>
        await _db.TeacherLevelAssignments
            .Where(a => a.TeacherId == teacherId)
            .Select(a => a.LevelId)
            .Distinct()
            .OrderBy(id => id)
            .ToListAsync(cancellationToken);
}
