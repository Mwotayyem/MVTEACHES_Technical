using NodaTime;

namespace MVTeaches.Domain.People;

/// <summary>
/// Owner decision 2026-08-30 rule 5: "Admin assigns the levels each teacher is
/// permitted to teach... The teacher creates and publishes available session
/// slots within their permitted levels... A teacher must not publish a session
/// for an unauthorized level."
///
/// This is the authorization record behind that rule: one row per
/// (teacher, course, level) triple the admin has granted. Its absence is the
/// denial — there is no "allowed by default", so a newly created teacher can
/// publish nothing until an admin grants them something explicitly.
///
/// <para>Owner decision 2026-09-04: the grant carries a COURSE as well as a
/// level. A level alone stopped meaning anything once the centre taught more
/// than one course — "authorised for B2" silently authorised B2 in Spanish and
/// Quran too, for a teacher hired to teach English. Existing grants are
/// backfilled to the centre's original course, the only one that existed when
/// they were made, so no grant changes meaning.</para>
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

    /// <summary>Owner decision 2026-09-04 — which course this grant is for.
    /// See the class remarks.</summary>
    public long CourseId { get; private set; }

    public int LevelId { get; private set; }

    public long GrantedByUserId { get; private set; }
    public Instant GrantedAtUtc { get; private set; }

    private TeacherLevelAssignment() { }

    public TeacherLevelAssignment(long teacherId, long courseId, int levelId, long grantedByUserId, Instant grantedAtUtc)
    {
        TeacherId = teacherId;
        CourseId = courseId;
        LevelId = levelId;
        GrantedByUserId = grantedByUserId;
        GrantedAtUtc = grantedAtUtc;
    }
}
