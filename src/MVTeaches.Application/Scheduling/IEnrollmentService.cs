namespace MVTeaches.Application.Scheduling;

public enum EnrollOutcome
{
    Enrolled,
    AlreadyEnrolled,

    /// <summary>§15.1's atomic conditional UPDATE lost the race, or the
    /// session was already full — the same outcome either way, since the
    /// caller cannot tell the difference and shouldn't need to.</summary>
    SessionFull,

    SessionNotFound,
    StudentNotFound,

    /// <summary>No AgeGroup row covers the student's current age — a data
    /// gap (§12.1's three rows should cover every real age), not a normal
    /// business outcome, but returned rather than thrown so a caller can
    /// show it plainly instead of a raw 500.</summary>
    NoApplicableAgeGroup,
}

public record EnrollResult(EnrollOutcome Outcome);

/// <summary>
/// Technical Study §15.4 (D-24): "session_enrollments مستقل عن آلية الإسناد"
/// (session_enrollments is independent of the assignment mechanism) — the
/// study deliberately leaves the actual student-to-recurring-schedule
/// assignment mechanism unspecified (no CREATE TABLE for it anywhere; the
/// entity itself, `SessionEnrollment`, is documented as "not given as a full
/// CREATE TABLE ... only referenced structurally"). This service does NOT
/// invent a new persistent "group membership" table to fill that gap — it
/// only creates the one row-per-(session, student) enrollment the schema
/// already has, closing exactly the invariant §15.1 documents: the atomic
/// conditional UPDATE against `seats_taken` (a plain read-then-write would
/// fail under concurrency — see ClassSession.TryTakeSeat's own remarks) and
/// the unique-enrollment guard, nothing more. See docs/deployment/STATUS.md
/// for the flagged open question this leaves for the owner: how a student
/// gets enrolled into a recurring schedule's FUTURE, not-yet-generated
/// sessions automatically is still undecided by the study.
/// </summary>
public interface IEnrollmentService
{
    Task<EnrollResult> EnrollInSessionAsync(long sessionId, long studentId, long enrolledByUserId, CancellationToken cancellationToken);

    /// <summary>Convenience for the common case: enroll a student into every
    /// currently-generated, not-yet-started Scheduled session under one
    /// recurring schedule in one action. Returns how many new enrollments
    /// were created (skips ones that already existed).</summary>
    Task<int> EnrollInUpcomingSessionsAsync(long recurringScheduleId, long studentId, long enrolledByUserId, CancellationToken cancellationToken);
}
