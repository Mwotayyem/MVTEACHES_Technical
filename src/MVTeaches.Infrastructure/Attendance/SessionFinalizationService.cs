using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Attendance;
using MVTeaches.Domain.Attendance;
using MVTeaches.Domain.Ledger;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;
using Npgsql;

namespace MVTeaches.Infrastructure.Attendance;

/// <inheritdoc cref="ISessionFinalizationService"/>
public class SessionFinalizationService : ISessionFinalizationService
{
    private const string PostgresUniqueViolationSqlState = "23505";

    private readonly MvTeachesDbContext _db;
    private readonly IClock _clock;

    public SessionFinalizationService(MvTeachesDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<SessionFinalizationSummary> FinalizeEndedSessionsAsync(CancellationToken cancellationToken)
    {
        var now = _clock.GetCurrentInstant();

        // Only Status == Scheduled — a centre-cancelled or NotDelivered session
        // is never in this query at all, which is exactly what keeps a
        // cancelled session's hours untouched (no separate "was it cancelled?"
        // check needed anywhere below).
        var endedSessionIds = await _db.ClassSessions
            .Where(s => s.Status == ClassSessionStatus.Scheduled && s.EndsAtUtc <= now)
            .OrderBy(s => s.EndsAtUtc)
            .Select(s => s.Id)
            .Take(200) // bounded batch — same discipline as NotificationDispatchJob
            .ToListAsync(cancellationToken);

        var sessionsFinalized = 0;
        var studentsMarkedNoShow = 0;
        var gaps = 0;

        foreach (var sessionId in endedSessionIds)
        {
            var (finalized, noShows, sessionGaps) = await FinalizeOneSessionAsync(sessionId, now, cancellationToken);
            if (finalized)
            {
                sessionsFinalized++;
            }

            studentsMarkedNoShow += noShows;
            gaps += sessionGaps;
        }

        return new SessionFinalizationSummary(sessionsFinalized, studentsMarkedNoShow, gaps);
    }

    private async Task<(bool Finalized, int NoShows, int Gaps)> FinalizeOneSessionAsync(long sessionId, Instant now, CancellationToken ct)
    {
        var session = await _db.ClassSessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);

        // Re-check status/time: a concurrent admin cancellation between the
        // batch query above and this row-level read must still win — never
        // finalize a session that just got cancelled out from under this run.
        if (session is null || session.Status != ClassSessionStatus.Scheduled || session.EndsAtUtc > now)
        {
            return (false, 0, 0);
        }

        var enrollments = await _db.SessionEnrollments
            .Where(e => e.SessionId == sessionId && e.State == EnrollmentState.Active)
            .ToListAsync(ct);

        var noShows = 0;
        var gaps = 0;

        foreach (var enrollment in enrollments)
        {
            var (didFinalize, hadGap) = await FinalizeOneEnrollmentAsync(session, enrollment, now, ct);
            if (didFinalize)
            {
                noShows++;
            }

            if (hadGap)
            {
                gaps++;
            }
        }

        // Re-fetch: a lost race inside FinalizeOneEnrollmentAsync clears the
        // change tracker (its own catch block), which would detach `session`
        // from tracking — the same "re-fetch after a possible ChangeTracker.Clear"
        // rule RescheduleUnattendedEnrollmentAsync's own remarks document.
        var freshSession = await _db.ClassSessions.FirstAsync(s => s.Id == sessionId, ct);
        if (freshSession.Status == ClassSessionStatus.Scheduled)
        {
            freshSession.MarkCompleted();
            await _db.SaveChangesAsync(ct);
        }

        return (true, noShows, gaps);
    }

    private async Task<(bool Finalized, bool HadGap)> FinalizeOneEnrollmentAsync(
        ClassSession session, SessionEnrollment enrollment, Instant now, CancellationToken ct)
    {
        // Fast path: a real Join (or an earlier finalization run) already
        // resolved this exact (session, student) — the ordinary, expected case
        // for every student who actually pressed Join. The database's own
        // ux_attendance_session_student unique index is the actual guarantee
        // below, not this pre-check.
        var alreadyResolved = await _db.AttendanceRecords.AnyAsync(
            a => a.SessionId == session.Id && a.StudentId == enrollment.StudentId, ct);
        if (alreadyResolved)
        {
            return (false, false);
        }

        _db.AttendanceRecords.Add(new AttendanceRecord(session.Id, enrollment.StudentId, null, now, isPresent: false));

        // Same rule JoinAttendanceService applies for a Join on a replacement
        // enrollment: a replacement's cost was already paid by the ORIGINAL
        // session's consumption, so a no-show on the replacement itself must
        // never write a second ledger entry either — there is nothing left to
        // consume a second time.
        var hadGap = false;
        if (enrollment.CompensatesForSessionId is null)
        {
            var subscription = await FindConsumableSubscriptionAsync(enrollment.StudentId, session, ct);
            if (subscription is not null)
            {
                _db.EntitlementLedgerEntries.Add(EntitlementLedgerEntry.ForConsumption(
                    enrollment.StudentId, subscription.Id, session.CourseId, session.LevelId, subscription.SessionType,
                    session.DurationMinutes, session.Id, performedByUserId: null, now));
            }
            else
            {
                // Genuinely rare: the subscription that made this booking
                // possible is gone by the time the session actually happened
                // (e.g. an admin cancelled/expired it after the student booked).
                // Still record the no-show itself — "one final attendance
                // outcome" is unconditional — there is simply nothing to debit.
                hadGap = true;
            }
        }

        try
        {
            await _db.SaveChangesAsync(ct);
            return (true, hadGap);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Lost the race to a real, concurrent Join for this exact
            // (session, student) — the loser here does nothing further,
            // exactly mirroring JoinAttendanceService's own race handling.
            // This IS the guarantee the owner's correction asks for: "exactly
            // one consumption and one final attendance outcome" even when
            // Join and finalization race at the session boundary.
            _db.ChangeTracker.Clear();
            return (false, false);
        }
    }

    /// <summary>Deliberately duplicated from JoinAttendanceService's own
    /// private method of the same purpose, not shared via DI — this
    /// selection algorithm must stay byte-for-byte identical wherever a
    /// consumption is drawn from a subscription, but JoinAttendanceService is
    /// this codebase's single most heavily-tested, most delicate file (the
    /// D-83 anchor); changing its constructor/DI shape for this refactor was
    /// judged a bigger risk than keeping this ~15-line method in sync by
    /// hand. If one changes, change the other identically.</summary>
    private async Task<Domain.Subscriptions.Subscription?> FindConsumableSubscriptionAsync(
        long studentId, ClassSession session, CancellationToken ct)
    {
        // Owner decision 2026-08-30 rule 4 — kept identical to
        // JoinAttendanceService's own copy per this method's own doc comment.
        var candidates = await _db.Subscriptions
            .Where(s => s.StudentId == studentId
                        && s.CourseId == session.CourseId
                        && s.LevelId == session.LevelId
                        && s.SessionType == session.SessionType
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
