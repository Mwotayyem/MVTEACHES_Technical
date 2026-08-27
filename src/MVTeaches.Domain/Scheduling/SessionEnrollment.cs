using NodaTime;

namespace MVTeaches.Domain.Scheduling;

public enum EnrollmentState
{
    Active,
    Cancelled,

    /// <summary>D-20: the enrollment moved to a replacement session; the original
    /// row is kept (not deleted) as part of the permanent audit trail.</summary>
    Transferred,
}

/// <summary>
/// Technical Study §33.2 section E / §12.2. Not given as a full CREATE TABLE in
/// the study (only referenced structurally), designed here consistently with
/// the documented requirements: independent of the assignment mechanism
/// (§15.4 — a student choosing their own teacher later needs no redesign here),
/// and capturing an <see cref="AgeGroupAtEnrollment"/> snapshot so historical
/// reporting never silently changes as a student ages (§12.2's binding rule).
/// </summary>
public class SessionEnrollment
{
    public long Id { get; private set; }

    public long SessionId { get; private set; }
    public long StudentId { get; private set; }

    /// <summary>Snapshot, not a live lookup — §12.2's binding technical rule.</summary>
    public int AgeGroupAtEnrollment { get; private set; }

    public EnrollmentState State { get; private set; } = EnrollmentState.Active;

    public Instant EnrolledAtUtc { get; private set; }
    public long EnrolledByUserId { get; private set; }

    /// <summary>Owner clarification (supersedes the earlier standalone
    /// makeup-credit design): set only when this enrollment is an
    /// admin-approved replacement lesson for a student who already pressed
    /// Join on the session this points to and then had a legitimate problem
    /// (§17.4/line 1018) — never inferred, never set by the ordinary
    /// enrollment path. IJoinAttendanceService checks this and skips the
    /// balance debit entirely for this session: the original consumption
    /// stands untouched, and this is not a second, independently spendable
    /// credit — it is tied to exactly this one replacement session, usable
    /// exactly once, the same way any other enrollment is.</summary>
    public long? CompensatesForSessionId { get; private set; }

    private SessionEnrollment() { }

    public SessionEnrollment(long sessionId, long studentId, int ageGroupAtEnrollment, long enrolledByUserId, Instant enrolledAtUtc)
    {
        SessionId = sessionId;
        StudentId = studentId;
        AgeGroupAtEnrollment = ageGroupAtEnrollment;
        EnrolledByUserId = enrolledByUserId;
        EnrolledAtUtc = enrolledAtUtc;
    }

    /// <summary>The one and only way CompensatesForSessionId gets set — an
    /// explicit admin decision (IEnrollmentService.ApproveReplacementLessonAsync),
    /// never the ordinary EnrollInSessionAsync path.</summary>
    public static SessionEnrollment AsReplacementLesson(long sessionId, long studentId, int ageGroupAtEnrollment,
        long compensatesForSessionId, long approvedByUserId, Instant enrolledAtUtc)
    {
        var enrollment = new SessionEnrollment(sessionId, studentId, ageGroupAtEnrollment, approvedByUserId, enrolledAtUtc);
        enrollment.CompensatesForSessionId = compensatesForSessionId;
        return enrollment;
    }

    public void Cancel() => State = EnrollmentState.Cancelled;

    /// <summary>D-20: moves this enrollment to the replacement session. The
    /// caller must create a NEW SessionEnrollment row for the replacement
    /// session — this only marks the original as transferred, never deleted.</summary>
    public void MarkTransferred() => State = EnrollmentState.Transferred;
}
