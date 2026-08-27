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

        var enrollment = await _db.SessionEnrollments.FirstOrDefaultAsync(
            e => e.SessionId == request.SessionId && e.StudentId == request.StudentId && e.State == EnrollmentState.Active,
            cancellationToken);
        if (enrollment is null)
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

        // Owner clarification (supersedes the earlier standalone-credit design):
        // a replacement lesson approved for a student who already consumed their
        // ORIGINAL session (see IEnrollmentService.ApproveReplacementLessonAsync)
        // must NOT deduct their ordinary purchased balance a second time. This
        // enrollment carries that link — CompensatesForSessionId — set only by
        // that explicit admin action, never inferred here. No ledger entry at
        // all is written for this Join; the attendance record alone (unique per
        // session+student, same as every other Join) is what makes it "usable
        // exactly once" — there is no separate spendable credit to track.
        if (enrollment.CompensatesForSessionId is not null)
        {
            _db.AttendanceRecords.Add(new AttendanceRecord(request.SessionId, request.StudentId, request.ActingUserId, now));
            try
            {
                await _db.SaveChangesAsync(cancellationToken);
                return new JoinAttendanceResult(JoinOutcome.Recorded);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                _db.ChangeTracker.Clear();
                return new JoinAttendanceResult(JoinOutcome.AlreadyRecorded);
            }
        }

        var subscription = await FindConsumableSubscriptionAsync(request.StudentId, session, cancellationToken);
        if (subscription is null)
        {
            // TOCTOU race: the "alreadyPresent" fast-path check above and this balance
            // read are two separate, independently-committed statements (no ambient
            // transaction spans them), so a concurrent Join for this exact
            // (session, student) can commit its consumption in between — draining the
            // very balance we're about to read as insufficient. That is not a real
            // shortfall; it is the other half of this same idempotent Join succeeding.
            // Re-check immediately before reporting InsufficientBalance: if attendance
            // now exists, this request lost the race, not the balance check (D-83:
            // the loser must be reported as present/idempotent, never as an error).
            var wonByAConcurrentRequest = await _db.AttendanceRecords.AnyAsync(
                a => a.SessionId == request.SessionId && a.StudentId == request.StudentId, cancellationToken);
            return new JoinAttendanceResult(wonByAConcurrentRequest ? JoinOutcome.AlreadyRecorded : JoinOutcome.InsufficientBalance);
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
