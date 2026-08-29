using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.People;
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

    public async Task<TeacherLevelGrantOutcome> GrantAsync(long teacherId, int levelId, long grantedByUserId, CancellationToken cancellationToken)
    {
        if (!await _db.Teachers.AnyAsync(t => t.Id == teacherId, cancellationToken))
        {
            return TeacherLevelGrantOutcome.TeacherNotFound;
        }

        if (!await _db.Levels.AnyAsync(l => l.Id == levelId, cancellationToken))
        {
            return TeacherLevelGrantOutcome.LevelNotFound;
        }

        _db.TeacherLevelAssignments.Add(new TeacherLevelAssignment(teacherId, levelId, grantedByUserId, _clock.GetCurrentInstant()));
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return TeacherLevelGrantOutcome.Granted;
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            // ux_teacher_level is the real guard against a duplicate grant
            // under concurrency; this makes the operation idempotent rather
            // than surfacing a constraint violation to the admin.
            _db.ChangeTracker.Clear();
            return TeacherLevelGrantOutcome.AlreadyGranted;
        }
    }

    public async Task<TeacherLevelRevokeOutcome> RevokeAsync(long teacherId, int levelId, CancellationToken cancellationToken)
    {
        if (!await _db.Teachers.AnyAsync(t => t.Id == teacherId, cancellationToken))
        {
            return TeacherLevelRevokeOutcome.TeacherNotFound;
        }

        var assignment = await _db.TeacherLevelAssignments
            .FirstOrDefaultAsync(a => a.TeacherId == teacherId && a.LevelId == levelId, cancellationToken);
        if (assignment is null)
        {
            return TeacherLevelRevokeOutcome.NotGranted;
        }

        _db.TeacherLevelAssignments.Remove(assignment);
        await _db.SaveChangesAsync(cancellationToken);
        return TeacherLevelRevokeOutcome.Revoked;
    }

    public Task<bool> IsAuthorizedForLevelAsync(long teacherId, int levelId, CancellationToken cancellationToken) =>
        _db.TeacherLevelAssignments.AnyAsync(a => a.TeacherId == teacherId && a.LevelId == levelId, cancellationToken);

    public async Task<IReadOnlyList<int>> GetPermittedLevelIdsAsync(long teacherId, CancellationToken cancellationToken) =>
        await _db.TeacherLevelAssignments
            .Where(a => a.TeacherId == teacherId)
            .Select(a => a.LevelId)
            .OrderBy(id => id)
            .ToListAsync(cancellationToken);
}
