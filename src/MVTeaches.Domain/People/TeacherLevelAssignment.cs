using NodaTime;

namespace MVTeaches.Domain.People;

/// <summary>
/// Owner decision 2026-08-30 rule 5: "Admin assigns the levels each teacher is
/// permitted to teach... The teacher creates and publishes available session
/// slots within their permitted levels... A teacher must not publish a session
/// for an unauthorized level."
///
/// This is the authorization record behind that rule: one row per
/// (teacher, level) pair the admin has granted. Its absence is the denial —
/// there is no "allowed by default", so a newly created teacher can publish
/// nothing until an admin grants them a level explicitly.
///
/// Deliberately separate from <see cref="Teacher"/> rather than a collection
/// property on it: the grant carries its own audit fields (who granted it and
/// when), which a plain many-to-many join table would lose, and the uniqueness
/// guarantee belongs in the database (ux_teacher_level) rather than in a
/// collection's in-memory semantics.
/// </summary>
public class TeacherLevelAssignment
{
    public long Id { get; private set; }

    public long TeacherId { get; private set; }
    public int LevelId { get; private set; }

    public long GrantedByUserId { get; private set; }
    public Instant GrantedAtUtc { get; private set; }

    private TeacherLevelAssignment() { }

    public TeacherLevelAssignment(long teacherId, int levelId, long grantedByUserId, Instant grantedAtUtc)
    {
        TeacherId = teacherId;
        LevelId = levelId;
        GrantedByUserId = grantedByUserId;
        GrantedAtUtc = grantedAtUtc;
    }
}
