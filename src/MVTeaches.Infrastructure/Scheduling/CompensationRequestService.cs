using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Scheduling;
using MVTeaches.Domain.Notifications;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;

namespace MVTeaches.Infrastructure.Scheduling;

/// <inheritdoc cref="ICompensationRequestService"/>
public class CompensationRequestService : ICompensationRequestService
{
    private readonly MvTeachesDbContext _db;
    private readonly IEnrollmentService _enrollments;
    private readonly IClock _clock;

    public CompensationRequestService(MvTeachesDbContext db, IEnrollmentService enrollments, IClock clock)
    {
        _db = db;
        _enrollments = enrollments;
        _clock = clock;
    }

    public async Task<SubmitCompensationRequestResult> RequestReplacementAsync(long studentId, long originalSessionId,
        string? reason, long actingUserId, CancellationToken cancellationToken)
    {
        // Self-service only — the student's own account, resolved server-side,
        // exactly like IStudentBookingService. This action is not delegated to
        // a guardian in the owner's own description ("appears in the Student
        // portal... the student submits").
        var isOwnAccount = await _db.Students.AnyAsync(s => s.Id == studentId && s.UserId == actingUserId, cancellationToken);
        if (!isOwnAccount)
        {
            return new SubmitCompensationRequestResult(SubmitCompensationRequestOutcome.Unauthorized);
        }

        var isConfirmedNoShow = await _db.AttendanceRecords.AnyAsync(
            a => a.SessionId == originalSessionId && a.StudentId == studentId && !a.IsPresent, cancellationToken);
        if (!isConfirmedNoShow)
        {
            return new SubmitCompensationRequestResult(SubmitCompensationRequestOutcome.NotANoShow);
        }

        var duplicateExists = await _db.CompensationRequests.AnyAsync(
            r => r.OriginalSessionId == originalSessionId && r.StudentId == studentId
                 && r.Status != CompensationRequestStatus.Rejected, cancellationToken);
        if (duplicateExists)
        {
            return new SubmitCompensationRequestResult(SubmitCompensationRequestOutcome.DuplicateRequest);
        }

        var request = new CompensationRequest(studentId, originalSessionId, reason, _clock.GetCurrentInstant());
        _db.CompensationRequests.Add(request);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return new SubmitCompensationRequestResult(SubmitCompensationRequestOutcome.Submitted, request.Id);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            // Lost a genuine race against a duplicate submission — ux_compensation_request_open
            // is the real backstop; the pre-check above is only the friendly path.
            _db.ChangeTracker.Clear();
            return new SubmitCompensationRequestResult(SubmitCompensationRequestOutcome.DuplicateRequest);
        }
    }

    public async Task<ResolveCompensationRequestResult> ApproveAsync(long requestId, long replacementSessionId,
        long approvedByUserId, CancellationToken cancellationToken)
    {
        var request = await _db.CompensationRequests.FirstOrDefaultAsync(r => r.Id == requestId, cancellationToken);
        if (request is null)
        {
            return new ResolveCompensationRequestResult(ResolveCompensationRequestOutcome.RequestNotFound);
        }

        if (request.Status != CompensationRequestStatus.Pending)
        {
            return new ResolveCompensationRequestResult(ResolveCompensationRequestOutcome.RequestNotPending);
        }

        // The actual granting mechanism is NOT duplicated here — this calls
        // the exact same method the admin-direct "student joined, then had a
        // problem" path already used, including its level-match, future-session,
        // and atomic-seat-claim checks.
        var approveResult = await _enrollments.ApproveReplacementLessonAsync(
            request.OriginalSessionId, replacementSessionId, request.StudentId, approvedByUserId, cancellationToken);

        if (approveResult.Outcome != ApproveReplacementOutcome.Approved)
        {
            return new ResolveCompensationRequestResult(MapApproveOutcome(approveResult.Outcome));
        }

        var now = _clock.GetCurrentInstant();
        request.Approve(replacementSessionId, approvedByUserId, now);
        await _db.SaveChangesAsync(cancellationToken);

        // Owner correction (2026-08-28): "Only after the Admin successfully
        // confirms the replacement session, create a durable notification/outbox
        // item" — deliberately a SEPARATE round trip after the approval above
        // commits, not one shared transaction, mirroring CertificateService's
        // own "recompute is a separate step after Verify" precedent. A failure
        // here (extremely unlikely — this is a single local insert) would leave
        // the replacement confirmed but the notification not yet queued, which
        // is the correct failure mode: the approval itself must never be undone
        // by a notification-side problem.
        await EnqueueReplacementApprovedNotificationAsync(request, cancellationToken);

        return new ResolveCompensationRequestResult(ResolveCompensationRequestOutcome.Approved);
    }

    public async Task<ResolveCompensationRequestResult> RejectAsync(long requestId, string reason, long rejectedByUserId,
        CancellationToken cancellationToken)
    {
        var request = await _db.CompensationRequests.FirstOrDefaultAsync(r => r.Id == requestId, cancellationToken);
        if (request is null)
        {
            return new ResolveCompensationRequestResult(ResolveCompensationRequestOutcome.RequestNotFound);
        }

        if (request.Status != CompensationRequestStatus.Pending)
        {
            return new ResolveCompensationRequestResult(ResolveCompensationRequestOutcome.RequestNotPending);
        }

        request.Reject(reason, rejectedByUserId, _clock.GetCurrentInstant());
        await _db.SaveChangesAsync(cancellationToken);
        return new ResolveCompensationRequestResult(ResolveCompensationRequestOutcome.Rejected);
    }

    private async Task EnqueueReplacementApprovedNotificationAsync(CompensationRequest request, CancellationToken ct)
    {
        var student = await _db.Students.AsNoTracking().FirstAsync(s => s.Id == request.StudentId, ct);
        // Self-service requests always come from the student's own account
        // (RequestReplacementAsync's own ownership check requires it), so a
        // real UserId is guaranteed here — no guardian-fallback case exists
        // for this specific notification.
        var recipientUserId = student.UserId!.Value;

        var replacementSession = await _db.ClassSessions.AsNoTracking().FirstAsync(s => s.Id == request.ReplacementSessionId, ct);
        var level = await _db.Levels.AsNoTracking().FirstOrDefaultAsync(l => l.Id == replacementSession.LevelId, ct);
        var zone = DateTimeZoneProviders.Tzdb[replacementSession.ScheduleTimeZone];
        var localStart = replacementSession.StartsAtUtc.InZone(zone);

        // "الطالب اسمه، المستوى، تاريخ التعويض، ووقته" — exactly these four
        // placeholders, nothing else. The real Meta template/rendering is not
        // built (no WhatsApp credentials exist yet — NotConfiguredWhatsAppSender);
        // this is the durable record of INTENT to send, which is what the
        // owner asked to have prepared and tested now.
        var payload = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["StudentName"] = student.FullName,
            ["LevelCode"] = level?.Code ?? "?",
            ["ReplacementDate"] = localStart.Date.ToString("yyyy-MM-dd", null),
            ["ReplacementTime"] = localStart.TimeOfDay.ToString("HH:mm", null),
        });

        _db.NotificationOutboxItems.Add(new NotificationOutboxItem(
            NotificationEvent.ReplacementLessonApproved, NotificationChannel.WhatsApp, recipientUserId, payload, _clock.GetCurrentInstant()));
        await _db.SaveChangesAsync(ct);
    }

    private static ResolveCompensationRequestOutcome MapApproveOutcome(ApproveReplacementOutcome outcome) => outcome switch
    {
        ApproveReplacementOutcome.OriginalNotYetConsumed => ResolveCompensationRequestOutcome.ReplacementSessionNotFound, // unreachable: RequestReplacementAsync already required a no-show record
        ApproveReplacementOutcome.OriginalSessionNotFound => ResolveCompensationRequestOutcome.ReplacementSessionNotFound,
        ApproveReplacementOutcome.ReplacementSessionNotFound => ResolveCompensationRequestOutcome.ReplacementSessionNotFound,
        ApproveReplacementOutcome.ReplacementSessionIsTheSameSession => ResolveCompensationRequestOutcome.ReplacementSessionIsTheSameSession,
        ApproveReplacementOutcome.ReplacementSessionFull => ResolveCompensationRequestOutcome.ReplacementSessionFull,
        ApproveReplacementOutcome.AlreadyEnrolledInReplacementSession => ResolveCompensationRequestOutcome.AlreadyEnrolledInReplacementSession,
        ApproveReplacementOutcome.NoApplicableAgeGroup => ResolveCompensationRequestOutcome.NoApplicableAgeGroup,
        ApproveReplacementOutcome.ReplacementSessionLevelMismatch => ResolveCompensationRequestOutcome.ReplacementSessionLevelMismatch,
        ApproveReplacementOutcome.ReplacementSessionNotInFuture => ResolveCompensationRequestOutcome.ReplacementSessionNotInFuture,
        _ => ResolveCompensationRequestOutcome.ReplacementSessionNotFound,
    };
}
