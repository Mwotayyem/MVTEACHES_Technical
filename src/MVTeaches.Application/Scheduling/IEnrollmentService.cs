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

public enum RescheduleOutcome
{
    Rescheduled,

    /// <summary>No Active, not-yet-consumed enrollment for this student on the
    /// original session — either they were never enrolled there, or (see
    /// OriginalSessionAlreadyConsumed) this is actually the OTHER case.</summary>
    OriginalEnrollmentNotFound,

    /// <summary>The student already pressed Join on the original session — this
    /// is not an unattended-lesson reschedule, it's ApproveReplacementLessonAsync's
    /// case (an already-consumed session that then had a problem).</summary>
    OriginalSessionAlreadyConsumed,

    ReplacementSessionNotFound,
    ReplacementSessionIsTheSameSession,
    ReplacementSessionFull,
    NoApplicableAgeGroup,
}

public record RescheduleResult(RescheduleOutcome Outcome);

public enum ApproveReplacementOutcome
{
    Approved,

    /// <summary>No AttendanceRecord for (originalSessionId, studentId) — the
    /// student never actually pressed Join on the original session, so there is
    /// nothing to compensate; use RescheduleUnattendedEnrollmentAsync instead.</summary>
    OriginalNotYetConsumed,

    ReplacementSessionNotFound,
    ReplacementSessionIsTheSameSession,
    ReplacementSessionFull,

    /// <summary>The student already has an active enrollment on the proposed
    /// replacement session — either an ordinary one, or a previously-approved
    /// replacement for this same original session.</summary>
    AlreadyEnrolledInReplacementSession,

    NoApplicableAgeGroup,
}

public record ApproveReplacementResult(ApproveReplacementOutcome Outcome);

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

    /// <summary>
    /// Owner clarification (case 1 of 2 — supersedes the earlier standalone
    /// makeup-credit design entirely): a student never pressed Join on the
    /// original session — nothing was ever consumed, so their purchased
    /// balance is untouched already, with nothing to compensate financially.
    /// The admin is simply moving that specific, still-unused lesson-hour to a
    /// new specific time: the original enrollment is marked Transferred (never
    /// deleted — the permanent audit trail D-20 already requires) and a fresh
    /// enrollment is created on the replacement session via the ordinary
    /// EnrollInSessionAsync path — no ledger entry of any kind, because none
    /// was ever needed. Rejects if the original was actually already consumed
    /// (that is ApproveReplacementLessonAsync's case instead).
    /// </summary>
    Task<RescheduleResult> RescheduleUnattendedEnrollmentAsync(long originalSessionId, long replacementSessionId,
        long studentId, long actingUserId, CancellationToken cancellationToken);

    /// <summary>
    /// Owner clarification (case 2 of 2): the student DID press Join on the
    /// original session (their consumption stands, untouched, forever) and
    /// then had a legitimate problem outside their control (§17.4/line 1018 —
    /// the one case the Technical Study reserves for the admin's own
    /// judgment). The admin approves exactly ONE specific replacement session;
    /// the resulting enrollment is linked back to the original via
    /// SessionEnrollment.CompensatesForSessionId, which
    /// IJoinAttendanceService checks to skip the balance debit entirely when
    /// the student later joins the replacement — never a second deduction,
    /// and never an independently spendable credit: this is tied to exactly
    /// one real replacement lesson, usable exactly once, the same as any
    /// other enrollment. Rejects if the original was never actually consumed
    /// (that is RescheduleUnattendedEnrollmentAsync's case instead).
    /// </summary>
    Task<ApproveReplacementResult> ApproveReplacementLessonAsync(long originalSessionId, long replacementSessionId,
        long studentId, long approvedByUserId, CancellationToken cancellationToken);
}
