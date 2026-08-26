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

    private SessionEnrollment() { }

    public SessionEnrollment(long sessionId, long studentId, int ageGroupAtEnrollment, long enrolledByUserId, Instant enrolledAtUtc)
    {
        SessionId = sessionId;
        StudentId = studentId;
        AgeGroupAtEnrollment = ageGroupAtEnrollment;
        EnrolledByUserId = enrolledByUserId;
        EnrolledAtUtc = enrolledAtUtc;
    }

    public void Cancel() => State = EnrollmentState.Cancelled;

    /// <summary>D-20: moves this enrollment to the replacement session. The
    /// caller must create a NEW SessionEnrollment row for the replacement
    /// session — this only marks the original as transferred, never deleted.</summary>
    public void MarkTransferred() => State = EnrollmentState.Transferred;
}
