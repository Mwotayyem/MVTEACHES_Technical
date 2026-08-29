namespace MVTeaches.Application.People;

public enum TeacherLevelGrantOutcome
{
    Granted,

    /// <summary>The teacher already had this level — grants are idempotent, so
    /// this is a success state for the caller, not an error.</summary>
    AlreadyGranted,

    TeacherNotFound,
    LevelNotFound,
}

public enum TeacherLevelRevokeOutcome
{
    Revoked,
    NotGranted,
    TeacherNotFound,
}

/// <summary>
/// Owner decision 2026-08-30 rule 5. The admin-side half of "the teacher
/// creates sessions, but only for levels the admin permitted": this service
/// owns the grants, and <see cref="IsAuthorizedForLevelAsync"/> is the single
/// check every session-publishing path must consult.
///
/// Revoking a level deliberately does NOT touch sessions the teacher already
/// published for it. Those are real scheduled lessons that students may have
/// already booked and paid minutes toward; silently cancelling them from an
/// authorization change would destroy bookings as a side effect of an admin
/// permission edit. Cancelling or reassigning them stays an explicit,
/// separate admin action through the existing cancellation/reassignment paths.
/// </summary>
public interface ITeacherLevelAuthorizationService
{
    Task<TeacherLevelGrantOutcome> GrantAsync(long teacherId, int levelId, long grantedByUserId, CancellationToken cancellationToken);

    Task<TeacherLevelRevokeOutcome> RevokeAsync(long teacherId, int levelId, long revokedByUserId, CancellationToken cancellationToken);

    /// <summary>The authorization check itself. False when no grant exists —
    /// absence is denial, there is no implicit default.</summary>
    Task<bool> IsAuthorizedForLevelAsync(long teacherId, int levelId, CancellationToken cancellationToken);

    Task<IReadOnlyList<int>> GetPermittedLevelIdsAsync(long teacherId, CancellationToken cancellationToken);
}
