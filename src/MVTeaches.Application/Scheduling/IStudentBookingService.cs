namespace MVTeaches.Application.Scheduling;

public enum BookSessionOutcome
{
    Booked,

    /// <summary>The acting user is neither this student's own login nor one
    /// of their guardians — resolved entirely server-side from the Student and
    /// Guardianship rows, never from a request parameter.</summary>
    Unauthorized,

    SessionNotFound,

    /// <summary>The student has no current level assignment at all (§10.3) —
    /// nothing to browse or book yet; an admin must assign one first.</summary>
    NoCurrentLevelAssigned,

    /// <summary>The session's level does not match the student's own current
    /// level — never trusted from the request, always the server-resolved
    /// value compared against the server-resolved session.</summary>
    SessionLevelMismatch,

    /// <summary>The session has already started, or isn't in Scheduled
    /// status — nothing to book.</summary>
    SessionNotBookable,

    AlreadyBooked,

    SessionFull,

    /// <summary>Owner correction (2026-08-28): consumed + already-booked,
    /// not-yet-consumed minutes for this course/level, plus this session's
    /// own duration, would exceed what the student's active package(s) for
    /// this course/level can cover. The student must finish or cancel an
    /// existing booking, or purchase more, before booking further.</summary>
    PackageLimitExceeded,

    NoApplicableAgeGroup,
}

public record BookSessionResult(BookSessionOutcome Outcome);

/// <summary>
/// Owner correction (2026-08-28), superseding admin-assigns-every-session:
/// "Admin does not manually assign normal sessions for every student" — a
/// student browses and books their OWN future sessions, filtered to their
/// OWN current level, within their OWN purchased package's remaining
/// capacity. This is deliberately a SEPARATE entry point from
/// IEnrollmentService.EnrollInSessionAsync (the admin path), not a
/// shared one — the trust boundary is different: an admin is already
/// trusted to enroll any student in anything, while booking on a student's
/// own behalf needs three checks nothing else in this codebase needed
/// together before: (1) the caller is entitled to act for that student,
/// (2) the session matches the student's own level in that session's own
/// course, (3) booking it would not exceed what their package can actually
/// cover once every other not-yet-consumed booking is counted too. Reuses
/// EnrollInSessionAsync's own atomic seat claim internally rather than
/// duplicating it.
///
/// <para><b>Owner decision 2026-09-04: a guardian may book for their own
/// child.</b> Until now this entry point accepted only the student's own
/// login, and the guardian screen had no booking action at all — so a child
/// registered by their guardian, who by design has no login of their own,
/// could not be booked into anything by anybody except an admin. A family
/// could buy a package and then have no way to use it. The identity check
/// is therefore the same "self, or one of this student's guardians" rule
/// already used by IPaymentService and IPlacementAttemptService, and NOTHING
/// else about the method changed: every level, age-group, package-capacity
/// and seat check still runs exactly as it did, because those are questions
/// about the STUDENT and do not care who pressed the button.</para>
/// </summary>
public interface IStudentBookingService
{
    Task<BookSessionResult> BookSessionAsync(long studentId, long sessionId, long actingUserId, CancellationToken cancellationToken);
}
