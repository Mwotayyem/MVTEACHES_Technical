using NodaTime;

namespace MVTeaches.Domain.Attendance;

/// <summary>
/// Technical Study §16.2 — the D-83 anchor. This is the ENTIRE attendance model:
///
///   - Only ever one status: Present. There is no Absent row, no Excused, no
///     Late, no NotMarked. A student with no matching row for a session that
///     has started is Absent — a value derived at read time, never written
///     (mirrors the derived-balance philosophy of the entitlement ledger,
///     D-36). See IAttendanceQueryService for the read-side derivation.
///   - <see cref="MarkedByUserId"/> is whoever pressed Join: the student's own
///     account, OR the guardian's account pressing on behalf of a child with
///     no independent login (D-02/D-03). It is NEVER the teacher — attendance
///     is self-service, not teacher-recorded.
///   - The (SessionId, StudentId) pair is unique at the database level
///     (a filtered/plain unique index in Infrastructure). That constraint,
///     not application code, is what makes a second Join a guaranteed no-op
///     even under concurrent requests.
///   - This entity carries NO duration/leave time. D-59/D-83 are explicit:
///     the system does not measure how long a student stayed and does not
///     read Zoom join/leave telemtry for any financial purpose. Whether the
///     student left after five minutes or stayed the whole session, the
///     record — and the consumption it triggers — is identical.
///
/// Do not add a Status enum with more than one member. Do not add a duration
/// field. Do not add an approval/verification field. Any of those would
/// reintroduce exactly the "Provisional Attendance / Approval Workflow /
/// Time Tracking" architecture the owner explicitly rejected when D-83 was
/// adopted.
/// </summary>
public class AttendanceRecord
{
    public long Id { get; private set; }

    public long SessionId { get; private set; }
    public long StudentId { get; private set; }

    public long MarkedByUserId { get; private set; }
    public Instant MarkedAtUtc { get; private set; }

    public string? Note { get; private set; }

    private AttendanceRecord() { }

    public AttendanceRecord(long sessionId, long studentId, long markedByUserId, Instant markedAtUtc, string? note = null)
    {
        SessionId = sessionId;
        StudentId = studentId;
        MarkedByUserId = markedByUserId;
        MarkedAtUtc = markedAtUtc;
        Note = note;
    }
}
