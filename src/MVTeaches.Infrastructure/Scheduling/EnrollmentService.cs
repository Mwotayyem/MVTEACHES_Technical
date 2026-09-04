using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Scheduling;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;
using Npgsql;

namespace MVTeaches.Infrastructure.Scheduling;

/// <inheritdoc cref="IEnrollmentService"/>
public class EnrollmentService : IEnrollmentService
{
    private const string PostgresUniqueViolationSqlState = "23505";

    private readonly MvTeachesDbContext _db;
    private readonly IClock _clock;

    public EnrollmentService(MvTeachesDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<EnrollResult> EnrollInSessionAsync(long sessionId, long studentId, long enrolledByUserId, CancellationToken cancellationToken)
    {
        var session = await _db.ClassSessions.FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is null)
        {
            return new EnrollResult(EnrollOutcome.SessionNotFound);
        }

        var student = await _db.Students.FirstOrDefaultAsync(s => s.Id == studentId, cancellationToken);
        if (student is null)
        {
            return new EnrollResult(EnrollOutcome.StudentNotFound);
        }

        // UNIQUE (session_id, student_id) WHERE state <> 'Cancelled' — fast,
        // friendly pre-check; the real guard against a concurrent double
        // enrollment is the database constraint caught below.
        var alreadyEnrolled = await _db.SessionEnrollments.AnyAsync(
            e => e.SessionId == sessionId && e.StudentId == studentId && e.State == EnrollmentState.Active, cancellationToken);
        if (alreadyEnrolled)
        {
            return new EnrollResult(EnrollOutcome.AlreadyEnrolled);
        }

        var today = _clock.GetCurrentInstant().InUtc().Date;
        var age = Period.Between(student.DateOfBirth, today, PeriodUnits.Years).Years;
        var ageGroup = await _db.AgeGroups.FirstOrDefaultAsync(
            a => a.MinAge <= age && (a.MaxAge == null || a.MaxAge >= age), cancellationToken);
        if (ageGroup is null)
        {
            return new EnrollResult(EnrollOutcome.NoApplicableAgeGroup);
        }

        // §15.1's atomic conditional UPDATE — a plain read-then-write
        // (SELECT seats_taken ... IF ... UPDATE) fails under concurrency by
        // design; this is the one part of the check-and-write that must be a
        // single statement. Raw SQL because EF Core's fluent API has no
        // first-class "conditional increment" operation.
        var rowsAffected = await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE class_sessions SET seats_taken = seats_taken + 1 WHERE \"Id\" = {sessionId} AND status = 'Scheduled' AND seats_taken < capacity",
            cancellationToken);
        if (rowsAffected == 0)
        {
            return new EnrollResult(EnrollOutcome.SessionFull);
        }

        var enrollment = new SessionEnrollment(sessionId, studentId, ageGroup.Id, enrolledByUserId, _clock.GetCurrentInstant());
        _db.SessionEnrollments.Add(enrollment);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return new EnrollResult(EnrollOutcome.Enrolled);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresUniqueViolationSqlState })
        {
            // Lost a genuine race against a concurrent enrollment of the same
            // (session, student) — the seat we just atomically claimed above
            // is now one seat over-counted, since another request's own claim
            // already succeeded for this exact pair. Give the seat back.
            _db.ChangeTracker.Clear();
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE class_sessions SET seats_taken = seats_taken - 1 WHERE \"Id\" = {sessionId} AND seats_taken > 0",
                cancellationToken);
            return new EnrollResult(EnrollOutcome.AlreadyEnrolled);
        }
    }

    public async Task<int> EnrollInUpcomingSessionsAsync(long recurringScheduleId, long studentId, long enrolledByUserId, CancellationToken cancellationToken)
    {
        var now = _clock.GetCurrentInstant();
        var sessionIds = await _db.ClassSessions
            .Where(s => s.RecurringScheduleId == recurringScheduleId && s.StartsAtUtc > now && s.Status == ClassSessionStatus.Scheduled)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);

        var enrolledCount = 0;
        foreach (var sessionId in sessionIds)
        {
            var result = await EnrollInSessionAsync(sessionId, studentId, enrolledByUserId, cancellationToken);
            if (result.Outcome == EnrollOutcome.Enrolled)
            {
                enrolledCount++;
            }
        }

        return enrolledCount;
    }

    public async Task<RescheduleResult> RescheduleUnattendedEnrollmentAsync(long originalSessionId,
        long replacementSessionId, long studentId, long actingUserId, CancellationToken cancellationToken)
    {
        if (replacementSessionId == originalSessionId)
        {
            return new RescheduleResult(RescheduleOutcome.ReplacementSessionIsTheSameSession);
        }

        var originalEnrollment = await _db.SessionEnrollments.FirstOrDefaultAsync(
            e => e.SessionId == originalSessionId && e.StudentId == studentId && e.State == EnrollmentState.Active,
            cancellationToken);
        if (originalEnrollment is null)
        {
            return new RescheduleResult(RescheduleOutcome.OriginalEnrollmentNotFound);
        }

        var alreadyConsumed = await _db.AttendanceRecords.AnyAsync(
            a => a.SessionId == originalSessionId && a.StudentId == studentId, cancellationToken);
        if (alreadyConsumed)
        {
            return new RescheduleResult(RescheduleOutcome.OriginalSessionAlreadyConsumed);
        }

        var replacementExists = await _db.ClassSessions.AnyAsync(s => s.Id == replacementSessionId, cancellationToken);
        if (!replacementExists)
        {
            return new RescheduleResult(RescheduleOutcome.ReplacementSessionNotFound);
        }

        var enrollResult = await EnrollInSessionAsync(replacementSessionId, studentId, actingUserId, cancellationToken);
        var outcome = enrollResult.Outcome switch
        {
            EnrollOutcome.Enrolled or EnrollOutcome.AlreadyEnrolled => RescheduleOutcome.Rescheduled,
            EnrollOutcome.SessionFull => RescheduleOutcome.ReplacementSessionFull,
            EnrollOutcome.NoApplicableAgeGroup => RescheduleOutcome.NoApplicableAgeGroup,
            _ => RescheduleOutcome.ReplacementSessionNotFound,
        };

        if (outcome != RescheduleOutcome.Rescheduled)
        {
            return new RescheduleResult(outcome);
        }

        // Re-fetch: EnrollInSessionAsync's own SaveChangesAsync may have cleared
        // the change tracker (its race-recovery path does), so the tracked
        // instance from the query above cannot be trusted to still be attached.
        originalEnrollment = await _db.SessionEnrollments.FirstAsync(e => e.Id == originalEnrollment.Id, cancellationToken);
        originalEnrollment.MarkTransferred();
        await _db.SaveChangesAsync(cancellationToken);

        return new RescheduleResult(RescheduleOutcome.Rescheduled);
    }

    public async Task<ApproveReplacementResult> ApproveReplacementLessonAsync(long originalSessionId,
        long replacementSessionId, long studentId, long approvedByUserId, CancellationToken cancellationToken)
    {
        if (replacementSessionId == originalSessionId)
        {
            return new ApproveReplacementResult(ApproveReplacementOutcome.ReplacementSessionIsTheSameSession);
        }

        var originalSession = await _db.ClassSessions.FirstOrDefaultAsync(s => s.Id == originalSessionId, cancellationToken);
        if (originalSession is null)
        {
            return new ApproveReplacementResult(ApproveReplacementOutcome.OriginalSessionNotFound);
        }

        var wasConsumed = await _db.AttendanceRecords.AnyAsync(
            a => a.SessionId == originalSessionId && a.StudentId == studentId, cancellationToken);
        if (!wasConsumed)
        {
            return new ApproveReplacementResult(ApproveReplacementOutcome.OriginalNotYetConsumed);
        }

        var replacementSession = await _db.ClassSessions.FirstOrDefaultAsync(s => s.Id == replacementSessionId, cancellationToken);
        if (replacementSession is null)
        {
            return new ApproveReplacementResult(ApproveReplacementOutcome.ReplacementSessionNotFound);
        }

        // Owner correction (2026-08-28): a replacement must be the same level
        // and a session that hasn't happened yet — load-bearing now that this
        // method is reachable from a student's own compensation request, not
        // only a trusted admin picking freely.
        if (replacementSession.LevelId != originalSession.LevelId)
        {
            return new ApproveReplacementResult(ApproveReplacementOutcome.ReplacementSessionLevelMismatch);
        }

        // Owner report 2026-09-05: the same COURSE and the same lesson type as
        // the session being compensated. See ReplacementSessionCourseMismatch.
        if (replacementSession.CourseId != originalSession.CourseId
            || replacementSession.SessionType != originalSession.SessionType)
        {
            return new ApproveReplacementResult(ApproveReplacementOutcome.ReplacementSessionCourseMismatch);
        }

        if (replacementSession.StartsAtUtc <= _clock.GetCurrentInstant())
        {
            return new ApproveReplacementResult(ApproveReplacementOutcome.ReplacementSessionNotInFuture);
        }

        var alreadyEnrolledInReplacement = await _db.SessionEnrollments.AnyAsync(
            e => e.SessionId == replacementSessionId && e.StudentId == studentId && e.State == EnrollmentState.Active,
            cancellationToken);
        if (alreadyEnrolledInReplacement)
        {
            return new ApproveReplacementResult(ApproveReplacementOutcome.AlreadyEnrolledInReplacementSession);
        }

        // Reuse the original enrollment's age-group snapshot (§12.2) — this is
        // the same underlying lesson slot moved, not a fresh independent one.
        // Falls back to a live lookup only if the original row is somehow gone.
        var originalEnrollment = await _db.SessionEnrollments.FirstOrDefaultAsync(
            e => e.SessionId == originalSessionId && e.StudentId == studentId, cancellationToken);
        int ageGroupId;
        if (originalEnrollment is not null)
        {
            ageGroupId = originalEnrollment.AgeGroupAtEnrollment;
        }
        else
        {
            var student = await _db.Students.FirstOrDefaultAsync(s => s.Id == studentId, cancellationToken);
            if (student is null)
            {
                return new ApproveReplacementResult(ApproveReplacementOutcome.ReplacementSessionNotFound);
            }

            var today = _clock.GetCurrentInstant().InUtc().Date;
            var age = Period.Between(student.DateOfBirth, today, PeriodUnits.Years).Years;
            var ageGroup = await _db.AgeGroups.FirstOrDefaultAsync(
                a => a.MinAge <= age && (a.MaxAge == null || a.MaxAge >= age), cancellationToken);
            if (ageGroup is null)
            {
                return new ApproveReplacementResult(ApproveReplacementOutcome.NoApplicableAgeGroup);
            }

            ageGroupId = ageGroup.Id;
        }

        var rowsAffected = await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE class_sessions SET seats_taken = seats_taken + 1 WHERE \"Id\" = {replacementSessionId} AND status = 'Scheduled' AND seats_taken < capacity",
            cancellationToken);
        if (rowsAffected == 0)
        {
            return new ApproveReplacementResult(ApproveReplacementOutcome.ReplacementSessionFull);
        }

        var replacementEnrollment = SessionEnrollment.AsReplacementLesson(
            replacementSessionId, studentId, ageGroupId, originalSessionId, approvedByUserId, _clock.GetCurrentInstant());
        _db.SessionEnrollments.Add(replacementEnrollment);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return new ApproveReplacementResult(ApproveReplacementOutcome.Approved);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresUniqueViolationSqlState })
        {
            _db.ChangeTracker.Clear();
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE class_sessions SET seats_taken = seats_taken - 1 WHERE \"Id\" = {replacementSessionId} AND seats_taken > 0",
                cancellationToken);
            return new ApproveReplacementResult(ApproveReplacementOutcome.AlreadyEnrolledInReplacementSession);
        }
    }
}
