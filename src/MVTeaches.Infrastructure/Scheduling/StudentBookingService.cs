using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Scheduling;
using MVTeaches.Domain.Notifications;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;
using Npgsql;

namespace MVTeaches.Infrastructure.Scheduling;

/// <inheritdoc cref="IStudentBookingService"/>
public class StudentBookingService : IStudentBookingService
{
    private const string PostgresUniqueViolationSqlState = "23505";

    private readonly MvTeachesDbContext _db;
    private readonly IClock _clock;

    public StudentBookingService(MvTeachesDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<BookSessionResult> BookSessionAsync(long studentId, long sessionId, long actingUserId, CancellationToken cancellationToken)
    {
        // Identity resolved entirely server-side: the acting account must be
        // THIS student's own login, or one of THIS student's guardians.
        // sessionId/studentId arriving together in one request is never
        // trusted on its own — this is what turns "some authenticated caller"
        // into "somebody entitled to act for this exact student, provably".
        //
        // Owner decision 2026-09-04: the guardian half is new. A child
        // registered through their guardian has no login by design, so
        // self-only meant such a child could be booked in by nobody but an
        // admin — a family could buy a package and never be able to use it.
        // This is the same rule IPaymentService and IPlacementAttemptService
        // already apply, deliberately written the same way rather than a
        // fourth variation of it.
        var student = await _db.Students.FirstOrDefaultAsync(s => s.Id == studentId, cancellationToken);
        if (student is null || !await IsAuthorizedForStudentAsync(studentId, actingUserId, cancellationToken))
        {
            return new BookSessionResult(BookSessionOutcome.Unauthorized);
        }

        // The session must be loaded BEFORE the level check now, because which
        // level to compare against depends on the session's own course.
        var session = await _db.ClassSessions.FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is null)
        {
            return new BookSessionResult(BookSessionOutcome.SessionNotFound);
        }

        // The student's level, resolved server-side from their own current
        // StudentLevel row — never accepted as a request parameter, and never
        // the session's own claimed level taken on faith.
        //
        // Owner decision 2026-09-04 (multi-course levels): the row read is the
        // one for THIS SESSION'S COURSE. A student with no level in that course
        // is refused with the same NoCurrentLevelAssigned they would get for
        // having no level at all — which is exactly right: for this course they
        // have not been placed, whatever they hold elsewhere.
        var currentLevelId = await _db.StudentLevels
            .Where(l => l.StudentId == studentId && l.CourseId == session.CourseId && l.IsCurrent)
            .Select(l => (int?)l.LevelId)
            .FirstOrDefaultAsync(cancellationToken);
        if (currentLevelId is null)
        {
            return new BookSessionResult(BookSessionOutcome.NoCurrentLevelAssigned);
        }

        if (session.LevelId != currentLevelId.Value)
        {
            return new BookSessionResult(BookSessionOutcome.SessionLevelMismatch);
        }

        var now = _clock.GetCurrentInstant();
        if (session.Status != ClassSessionStatus.Scheduled || session.StartsAtUtc <= now)
        {
            return new BookSessionResult(BookSessionOutcome.SessionNotBookable);
        }

        var alreadyBooked = await _db.SessionEnrollments.AnyAsync(
            e => e.SessionId == sessionId && e.StudentId == studentId && e.State == EnrollmentState.Active, cancellationToken);
        if (alreadyBooked)
        {
            return new BookSessionResult(BookSessionOutcome.AlreadyBooked);
        }

        var age = Period.Between(student.DateOfBirth, now.InUtc().Date, PeriodUnits.Years).Years;
        var ageGroup = await _db.AgeGroups.FirstOrDefaultAsync(
            a => a.MinAge <= age && (a.MaxAge == null || a.MaxAge >= age), cancellationToken);
        if (ageGroup is null)
        {
            return new BookSessionResult(BookSessionOutcome.NoApplicableAgeGroup);
        }

        // Owner correction (2026-08-28): "prevent the student from booking
        // more total lesson duration than the package can cover, including
        // hours already consumed or committed to other future bookings."
        // There is no per-booking ledger entry to check against (D-36:
        // booking itself moves nothing in the ledger, only a real Join or a
        // no-show finalization does) — this is a genuinely new, derived
        // "would-be-committed" quantity, computed the same read-time way the
        // ledger balance itself always is, never stored.
        //
        // The whole check-then-book sequence below runs inside one explicit
        // transaction with a row lock on this student's own row — a plain
        // read-then-write here would let two concurrent bookings for two
        // DIFFERENT sessions each individually pass the check and together
        // exceed the package, the same class of race §15.1's atomic seat
        // claim exists to prevent for capacity. There is no single-row CHECK
        // constraint that can express "sum across many rows must not exceed
        // X", so a row lock serializing this student's own concurrent
        // booking attempts is the correct tool here, not a second unique index.
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM students WHERE \"Id\" = {studentId} FOR UPDATE", cancellationToken);

        // Owner decision 2026-08-30 rule 4: a Group package's balance can never
        // cover a Private session's booking and vice versa — scoped by
        // SessionType alongside Course/Level, matching FindConsumableSubscriptionAsync.
        var ledgerBalance = await _db.EntitlementLedgerEntries
            .Where(l => l.StudentId == studentId && l.CourseId == session.CourseId && l.LevelId == session.LevelId
                        && l.SessionType == session.SessionType)
            .SumAsync(l => (int?)l.DeltaMinutes, cancellationToken) ?? 0;

        // "Committed": Active, ordinary (non-replacement) bookings in this
        // same course+level that haven't been resolved (Join or no-show) yet
        // — a replacement enrollment (CompensatesForSessionId set) is
        // deliberately excluded, since its Join is free and was never going
        // to draw from this balance either way.
        var committedMinutes = await _db.SessionEnrollments
            .Where(e => e.StudentId == studentId && e.State == EnrollmentState.Active && e.CompensatesForSessionId == null)
            .Join(_db.ClassSessions, e => e.SessionId, s => s.Id, (e, s) => new { Enrollment = e, Session = s })
            .Where(x => x.Session.CourseId == session.CourseId && x.Session.LevelId == session.LevelId
                        && x.Session.SessionType == session.SessionType
                        && x.Session.Status == ClassSessionStatus.Scheduled)
            .Where(x => !_db.AttendanceRecords.Any(a => a.SessionId == x.Session.Id && a.StudentId == studentId))
            .SumAsync(x => (int?)x.Session.DurationMinutes, cancellationToken) ?? 0;

        var remaining = ledgerBalance - committedMinutes;
        if (remaining < session.DurationMinutes)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new BookSessionResult(BookSessionOutcome.PackageLimitExceeded);
        }

        // §15.1's atomic conditional UPDATE — the same seat-claim SQL
        // EnrollmentService.EnrollInSessionAsync uses.
        var rowsAffected = await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE class_sessions SET seats_taken = seats_taken + 1 WHERE \"Id\" = {sessionId} AND status = 'Scheduled' AND seats_taken < capacity",
            cancellationToken);
        if (rowsAffected == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new BookSessionResult(BookSessionOutcome.SessionFull);
        }

        var enrollment = new SessionEnrollment(sessionId, studentId, ageGroup.Id, actingUserId, now);
        _db.SessionEnrollments.Add(enrollment);

        // Owner decision 2026-08-30 rule 9: booking confirmation.
        //
        // student.UserId used to be guaranteed non-null here, because this
        // method only ever loaded a Student row matched by
        // s.UserId == actingUserId. Owner decision 2026-09-04 removed that
        // guarantee along with the self-only check: a child registered by their
        // guardian has no login at all, and dereferencing it threw.
        //
        // The student's own account still gets the message whenever they have
        // one — including when their guardian did the booking, which is who
        // needs to know the lesson is now theirs to attend. A child with no
        // login has no inbox of their own, so it goes to whoever booked, which
        // in that case is by definition their guardian.
        var payload = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["StudentName"] = student.FullName,
            ["SessionId"] = sessionId.ToString(),
            ["StartsAtUtc"] = session.StartsAtUtc.ToString(),
        });
        _db.NotificationOutboxItems.Add(new NotificationOutboxItem(
            NotificationEvent.BookingConfirmed, NotificationChannel.WhatsApp,
            student.UserId ?? actingUserId, payload, now));

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new BookSessionResult(BookSessionOutcome.Booked);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresUniqueViolationSqlState })
        {
            // Lost a genuine race against a concurrent booking of the exact
            // same (session, student) pair — give the seat back and report
            // the same friendly outcome EnrollInSessionAsync uses for this case.
            await transaction.RollbackAsync(cancellationToken);
            _db.ChangeTracker.Clear();
            return new BookSessionResult(BookSessionOutcome.AlreadyBooked);
        }
    }

    /// <summary>Self, or one of this student's guardians — the same rule
    /// IPaymentService.IsAuthorizedForStudentAsync and
    /// PlacementAttemptService.IsAuthorizedAsync already enforce, resolved from
    /// the Student and Guardianship rows and never from anything in the
    /// request. A guardian link that has been removed stops authorising the
    /// very next call, because this is a live query rather than a cached
    /// claim.</summary>
    private async Task<bool> IsAuthorizedForStudentAsync(long studentId, long actingUserId, CancellationToken ct)
    {
        var isTheStudentThemself = await _db.Students.AnyAsync(s => s.Id == studentId && s.UserId == actingUserId, ct);
        if (isTheStudentThemself)
        {
            return true;
        }

        return await _db.Guardianships
            .Join(_db.Guardians, gs => gs.GuardianId, g => g.Id, (gs, g) => new { gs.StudentId, g.UserId })
            .AnyAsync(x => x.StudentId == studentId && x.UserId == actingUserId, ct);
    }
}
