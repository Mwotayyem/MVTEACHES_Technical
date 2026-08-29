using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Integrations;
using MVTeaches.Application.Scheduling;
using MVTeaches.Domain.Notifications;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;

namespace MVTeaches.Infrastructure.Scheduling;

/// <inheritdoc cref="ISessionCancellationService"/>
public class SessionCancellationService : ISessionCancellationService
{
    private readonly MvTeachesDbContext _db;
    private readonly IEnrollmentService _enrollments;
    private readonly IMeetingProvisioningService _meetings;
    private readonly IClock _clock;

    public SessionCancellationService(MvTeachesDbContext db, IEnrollmentService enrollments,
        IMeetingProvisioningService meetings, IClock clock)
    {
        _db = db;
        _enrollments = enrollments;
        _meetings = meetings;
        _clock = clock;
    }

    public async Task<CancelSessionResult> CancelAsync(long sessionId, string reason, long cancelledByUserId,
        long? replacementSessionId, CancellationToken cancellationToken)
    {
        var session = await _db.ClassSessions.FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is null)
        {
            return new CancelSessionResult(CancelSessionOutcome.SessionNotFound);
        }

        if (session.Status != ClassSessionStatus.Scheduled)
        {
            return new CancelSessionResult(CancelSessionOutcome.NotCancellable);
        }

        if (replacementSessionId == sessionId)
        {
            return new CancelSessionResult(CancelSessionOutcome.ReplacementSessionIsTheSameSession);
        }

        if (replacementSessionId is not null)
        {
            var replacementExists = await _db.ClassSessions.AnyAsync(s => s.Id == replacementSessionId, cancellationToken);
            if (!replacementExists)
            {
                return new CancelSessionResult(CancelSessionOutcome.ReplacementSessionNotFound);
            }
        }

        var activeEnrollments = await _db.SessionEnrollments
            .Where(e => e.SessionId == sessionId && e.State == EnrollmentState.Active)
            .ToListAsync(cancellationToken);

        var consumedStudentIds = (await _db.AttendanceRecords
            .Where(a => a.SessionId == sessionId)
            .Select(a => a.StudentId)
            .ToListAsync(cancellationToken)).ToHashSet();

        var movedOrCancelled = 0;
        var leftUntouched = 0;
        var couldNotMove = 0;
        var affectedStudentIds = new List<long>();

        foreach (var enrollment in activeEnrollments)
        {
            if (consumedStudentIds.Contains(enrollment.StudentId))
            {
                // §17.4/line 1018: already consumed via Join — D-83-final, never
                // touched automatically. The admin decides separately
                // (IEnrollmentService.ApproveReplacementLessonAsync, on /Admin/RescheduleSessions).
                leftUntouched++;
                continue;
            }

            if (replacementSessionId is not null)
            {
                var enrollResult = await _enrollments.EnrollInSessionAsync(
                    replacementSessionId.Value, enrollment.StudentId, cancelledByUserId, cancellationToken);
                if (enrollResult.Outcome == EnrollOutcome.Enrolled)
                {
                    enrollment.MarkTransferred();
                    movedOrCancelled++;
                    affectedStudentIds.Add(enrollment.StudentId);
                }
                else
                {
                    // e.g. the replacement is full — the admin resolves this
                    // student individually; no invented fallback here.
                    couldNotMove++;
                }
            }
            else
            {
                enrollment.Cancel();
                movedOrCancelled++;
                affectedStudentIds.Add(enrollment.StudentId);
            }
        }

        if (replacementSessionId is not null)
        {
            session.CancelAndReplace(reason, cancelledByUserId, replacementSessionId.Value);
        }
        else
        {
            session.Cancel(reason, cancelledByUserId);
        }

        // Owner decision 2026-08-30 rule 9: schedule change/cancellation —
        // the general case (MeetingProvisioningService already covers the
        // narrower teacher-reassignment case with the same event). Only
        // students actually moved or cancelled above are notified — someone
        // already consumed via Join, or who could not be moved, is a
        // separate admin follow-up, not a "your session changed" message.
        if (affectedStudentIds.Count > 0)
        {
            var now = _clock.GetCurrentInstant();
            var affectedStudents = await _db.Students
                .Where(s => affectedStudentIds.Contains(s.Id) && s.UserId != null)
                .ToListAsync(cancellationToken);
            foreach (var affectedStudent in affectedStudents)
            {
                var payload = JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["StudentName"] = affectedStudent.FullName,
                    ["SessionId"] = sessionId.ToString(),
                    ["Reason"] = reason,
                });
                _db.NotificationOutboxItems.Add(new NotificationOutboxItem(
                    NotificationEvent.SessionCancelledOrMoved, NotificationChannel.WhatsApp,
                    affectedStudent.UserId!.Value, payload, now));
            }
        }

        await _db.SaveChangesAsync(cancellationToken);

        // Owner clarification (2026-08-29): "A centre-cancelled or
        // administratively rescheduled session must not consume the
        // student's hours" — already true above (attendance/entitlement are
        // untouched here) — and its external meeting (if any) must stop
        // being usable too, best-effort, never blocking the cancellation itself.
        await _meetings.CancelForSessionAsync(sessionId, reason, cancellationToken);

        return new CancelSessionResult(CancelSessionOutcome.Cancelled, movedOrCancelled, leftUntouched, couldNotMove);
    }
}
