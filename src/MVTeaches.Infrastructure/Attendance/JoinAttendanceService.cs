using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Attendance;
using MVTeaches.Domain.Attendance;
using MVTeaches.Domain.Ledger;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;
using Npgsql;

namespace MVTeaches.Infrastructure.Attendance;

/// <summary>
/// Technical Study §16/§20 — the D-83 anchor's implementation.
///
/// Concurrency contract (master engineering prompt §9 — do not weaken this):
/// the guard against a double-Join is NOT the "if (!exists) insert" read-check
/// below by itself. It is the database's own unique constraints
/// (ux_attendance_session_student and ux_ent_consumption). Two concurrent
/// requests can both pass the in-memory pre-checks; only one SaveChangesAsync
/// call will actually commit, and the other fails with Postgres error 23505
/// (unique_violation), which is caught here and turned into a successful
/// AlreadyRecorded outcome — never surfaced to the caller as an error. This is
/// why the pre-check exists only to give a FAST, friendly path for the common
/// case, not as the actual correctness guarantee.
/// </summary>
public class JoinAttendanceService : IJoinAttendanceService
{
    private const string PostgresUniqueViolationSqlState = "23505";

    private readonly MvTeachesDbContext _db;
    private readonly IClock _clock;

    public JoinAttendanceService(MvTeachesDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<JoinAttendanceResult> JoinAsync(JoinAttendanceRequest request, CancellationToken cancellationToken)
    {
        var session = await _db.ClassSessions
            .FirstOrDefaultAsync(s => s.Id == request.SessionId, cancellationToken);

        if (session is null)
        {
            return new JoinAttendanceResult(JoinOutcome.SessionNotFound);
        }

        var now = _clock.GetCurrentInstant();
        if (now < session.StartsAtUtc)
        {
            // The only time boundary this service enforces — see the interface's
            // remarks. Deliberately no upper bound: D-83 forbids an admin-configurable
            // "closing window" concept, so a late Join after the session's end is
            // still accepted, exactly as documented ("no time tracking").
            return new JoinAttendanceResult(JoinOutcome.SessionNotYetJoinable);
        }

        var isAuthorized = await IsAuthorizedToJoinAsync(request.ActingUserId, request.StudentId, cancellationToken);
        if (!isAuthorized)
        {
            return new JoinAttendanceResult(JoinOutcome.Unauthorized, "Acting user is neither the student nor an active guardian.");
        }

        var isEnrolled = await _db.SessionEnrollments.AnyAsync(
            e => e.SessionId == request.SessionId && e.StudentId == request.StudentId && e.State == EnrollmentState.Active,
            cancellationToken);
        if (!isEnrolled)
        {
            return new JoinAttendanceResult(JoinOutcome.Unauthorized, "Student has no active enrollment in this session.");
        }

        // Fast path: someone already pressed Join for this exact (session, student).
        var alreadyPresent = await _db.AttendanceRecords.AnyAsync(
            a => a.SessionId == request.SessionId && a.StudentId == request.StudentId, cancellationToken);
        if (alreadyPresent)
        {
            return new JoinAttendanceResult(JoinOutcome.AlreadyRecorded);
        }

        var subscription = await FindConsumableSubscriptionAsync(request.StudentId, session, cancellationToken);
        if (subscription is null)
        {
            return new JoinAttendanceResult(JoinOutcome.InsufficientBalance);
        }

        var attendance = new AttendanceRecord(request.SessionId, request.StudentId, request.ActingUserId, now);
        var consumption = EntitlementLedgerEntry.ForConsumption(
            request.StudentId, subscription.Id, session.CourseId, session.LevelId,
            session.DurationMinutes, request.SessionId, request.ActingUserId, now);

        _db.AttendanceRecords.Add(attendance);
        _db.EntitlementLedgerEntries.Add(consumption);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return new JoinAttendanceResult(JoinOutcome.Recorded);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Lost a genuine concurrent race against another request for the same
            // (session, student) — this is success, not failure (D-83: idempotent).
            _db.ChangeTracker.Clear();
            return new JoinAttendanceResult(JoinOutcome.AlreadyRecorded);
        }
    }

    /// <summary>D-83's guardian rule: the acting account must be the student's
    /// own login, OR a guardian with an active guardianship link to this
    /// student (covers a 5-12 child with no independent account). Every
    /// guardian-scoped check in this system must look like this one.</summary>
    private async Task<bool> IsAuthorizedToJoinAsync(long actingUserId, long studentId, CancellationToken ct)
    {
        var isTheStudentThemself = await _db.Students
            .AnyAsync(s => s.Id == studentId && s.UserId == actingUserId, ct);
        if (isTheStudentThemself)
        {
            return true;
        }

        var isAnActiveGuardian = await _db.Guardianships
            .Join(_db.Guardians, gs => gs.GuardianId, g => g.Id, (gs, g) => new { gs.StudentId, g.UserId })
            .AnyAsync(x => x.StudentId == studentId && x.UserId == actingUserId, ct);

        return isAnActiveGuardian;
    }

    /// <summary>
    /// Picks the oldest-expiring active subscription for this (student, course,
    /// level) that has enough remaining balance to cover the FULL session
    /// duration in one draw — the study does not specify a multi-subscription
    /// split, and §20.5 rule 5 requires sufficient balance to exist before the
    /// press is accepted at all, so a partial draw across two subscriptions is
    /// deliberately not implemented.
    /// </summary>
    private async Task<Domain.Subscriptions.Subscription?> FindConsumableSubscriptionAsync(
        long studentId, ClassSession session, CancellationToken ct)
    {
        var candidates = await _db.Subscriptions
            .Where(s => s.StudentId == studentId
                        && s.CourseId == session.CourseId
                        && s.LevelId == session.LevelId
                        && s.Status == Domain.Subscriptions.SubscriptionStatus.Active)
            .OrderBy(s => s.ExpiresOn)
            .ToListAsync(ct);

        foreach (var candidate in candidates)
        {
            var balance = await _db.EntitlementLedgerEntries
                .Where(l => l.SubscriptionId == candidate.Id)
                .SumAsync(l => (int?)l.DeltaMinutes, ct) ?? 0;

            if (balance >= session.DurationMinutes)
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresUniqueViolationSqlState };
}
