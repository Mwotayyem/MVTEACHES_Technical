using NodaTime;

namespace MVTeaches.Domain.Certificates;

/// <summary>
/// Technical Study §27.2 (D-51/CONF-03). A materialized view of
/// SUM(verified session-delivery minutes) for a (student, level, course) tuple
/// where the student was Present — recomputed on every delivery verification,
/// NEVER on Subscription state. Deliberately carries no "required minutes"
/// column: eligibility is `MinutesCompleted >= settings.CertificateRequiredHours * 60`,
/// evaluated live (D-65/§19.5) — never stored per student.
/// Primary key is the composite (StudentId, LevelId, CourseId).
/// </summary>
public class LevelProgress
{
    public long StudentId { get; private set; }
    public int LevelId { get; private set; }
    public long CourseId { get; private set; }

    public int MinutesCompleted { get; private set; }
    public Instant? CompletedAtUtc { get; private set; }

    private LevelProgress() { }

    public LevelProgress(long studentId, int levelId, long courseId)
    {
        StudentId = studentId;
        LevelId = levelId;
        CourseId = courseId;
    }

    /// <summary>Called by the delivery-verification pipeline with the FULL,
    /// freshly recomputed sum — this is a materialized view, not an incremental
    /// counter, precisely so it can never drift from its source query (§27.2).</summary>
    public void Recompute(int minutesCompleted, Instant? completedAtUtc)
    {
        MinutesCompleted = minutesCompleted;
        CompletedAtUtc = completedAtUtc;
    }
}
