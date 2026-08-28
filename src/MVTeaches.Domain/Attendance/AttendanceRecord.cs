using NodaTime;

namespace MVTeaches.Domain.Attendance;

/// <summary>
/// Technical Study §16.2 — the D-83 anchor, EXTENDED by the owner's
/// self-service-booking correction (superseding the original "Absent is
/// derived, never written" rule below): a session that ends with an enrolled
/// student never having pressed Join is now explicitly finalized as a
/// no-show, in this SAME table, by <c>SessionFinalizationService</c> — not
/// left as an unwritten, derived-at-read-time absence.
///
///   - <see cref="IsPresent"/> is the only outcome this class ever
///     distinguishes: true = the student (or their guardian) actually
///     pressed Join; false = the session ended and nobody ever did, so the
///     system finalized it. There is still no Excused/Late/NotMarked — only
///     these two, and every row always has exactly one of them.
///   - <see cref="MarkedByUserId"/> is whoever pressed Join for a Present
///     row: the student's own account, OR the guardian's account pressing on
///     behalf of a child with no independent login (D-02/D-03) — NEVER the
///     teacher. For a NoShow row it is NULL: nobody "marked" it, the system
///     finalized it once the session ended (mirrors EntitlementLedgerEntry's
///     own "NULL = the system itself" convention).
///   - The (SessionId, StudentId) pair is unique at the database level
///     (ux_attendance_session_student). That single constraint is what makes
///     BOTH a second Join AND a Join racing the no-show finalizer resolve to
///     exactly one row, deterministically — whichever insert wins the race,
///     the loser's own write fails with 23505 and is caught, never retried
///     into a second row.
///   - This entity still carries NO duration/leave time. D-59/D-83 remain
///     explicit: the system does not measure how long a student stayed and
///     does not read Zoom join/leave telemetry for any financial purpose.
///
/// Do not add a third status. Do not add a duration field. Do not add an
/// approval/verification field.
/// </summary>
public class AttendanceRecord
{
    public long Id { get; private set; }

    public long SessionId { get; private set; }
    public long StudentId { get; private set; }

    /// <summary>Whoever pressed Join for a Present row; NULL for a
    /// system-finalized NoShow row.</summary>
    public long? MarkedByUserId { get; private set; }
    public Instant MarkedAtUtc { get; private set; }

    /// <summary>true = a real Join press. false = the session ended and the
    /// student never joined; SessionFinalizationService wrote this row.</summary>
    public bool IsPresent { get; private set; }

    public string? Note { get; private set; }

    private AttendanceRecord() { }

    public AttendanceRecord(long sessionId, long studentId, long? markedByUserId, Instant markedAtUtc,
        bool isPresent, string? note = null)
    {
        SessionId = sessionId;
        StudentId = studentId;
        MarkedByUserId = markedByUserId;
        MarkedAtUtc = markedAtUtc;
        IsPresent = isPresent;
        Note = note;
    }
}
