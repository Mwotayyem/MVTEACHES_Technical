namespace MVTeaches.Application.Scheduling;

public enum BookSessionOutcome
{
    Booked,

    /// <summary>The acting user does not own this student account — resolved
    /// entirely server-side (the student's own linked Student row), never
    /// from a request parameter.</summary>
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
/// IEnrollmentService.EnrollInSessionAsync (the admin/guardian path), not a
/// shared one — the trust boundary is different: an admin is already
/// trusted to enroll any student in anything, while a student booking for
/// themselves needs three checks nothing else in this codebase needed
/// together before: (1) they own the account making the request, (2) the
/// session matches their own level, (3) booking it would not exceed what
/// their package can actually cover once every other not-yet-consumed
/// booking is counted too. Reuses EnrollInSessionAsync's own atomic seat
/// claim internally rather than duplicating it.
/// </summary>
public interface IStudentBookingService
{
    Task<BookSessionResult> BookSessionAsync(long studentId, long sessionId, long actingUserId, CancellationToken cancellationToken);
}
