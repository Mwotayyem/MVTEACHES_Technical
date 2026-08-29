using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MVTeaches.Domain.Notifications;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;

namespace MVTeaches.Infrastructure.Notifications;

/// <summary>
/// Owner decision 2026-08-30 rule 9: "a 5-minute-before reminder (idempotent
/// job)". Registered as a recurring Hangfire job running every minute (see
/// Program.cs) — idempotency comes from checking NotificationOutboxItem's
/// own (Event, SessionId, RecipientUserId) before enqueuing, exactly the
/// index NotificationOutboxItemConfiguration adds for this purpose, so a
/// session sitting in the 5-minute window across two consecutive runs of
/// this job is never reminded twice. Only ACTIVE enrollments with a real
/// UserId are notified — the same "no independent login, nothing lost"
/// convention already established across every other notification wiring
/// this session (MeetingProvisioningService, PaymentService,
/// StudentBookingService, SessionCancellationService).
/// </summary>
public class SessionReminderJob
{
    private readonly MvTeachesDbContext _db;
    private readonly IClock _clock;
    private readonly ILogger<SessionReminderJob> _logger;

    public SessionReminderJob(MvTeachesDbContext db, IClock clock, ILogger<SessionReminderJob> logger)
    {
        _db = db;
        _clock = clock;
        _logger = logger;
    }

    public async Task SendFiveMinuteRemindersAsync(CancellationToken cancellationToken)
    {
        var now = _clock.GetCurrentInstant();
        // A window wide enough that a 1-minute job cadence cannot skip a
        // session entirely (e.g. a delayed run), narrow enough that nothing
        // is ever reminded more than once as "5 minutes before" in spirit —
        // the idempotency check below is what actually prevents a duplicate,
        // this window is only about catching every session at least once.
        var windowStart = now.Plus(Duration.FromMinutes(4));
        var windowEnd = now.Plus(Duration.FromMinutes(6));

        var upcomingSessions = await _db.ClassSessions
            .Where(s => s.Status == ClassSessionStatus.Scheduled && s.StartsAtUtc >= windowStart && s.StartsAtUtc < windowEnd)
            .ToListAsync(cancellationToken);

        if (upcomingSessions.Count == 0)
        {
            return;
        }

        var sessionIds = upcomingSessions.Select(s => s.Id).ToList();
        var alreadyRemindedPairs = (await _db.NotificationOutboxItems
                .Where(n => n.Event == NotificationEvent.ZoomLink5Min && n.SessionId != null && sessionIds.Contains(n.SessionId!.Value))
                .Select(n => new { n.SessionId, n.RecipientUserId })
                .ToListAsync(cancellationToken))
            .Select(x => (x.SessionId!.Value, x.RecipientUserId))
            .ToHashSet();

        var enrollments = await _db.SessionEnrollments
            .Where(e => sessionIds.Contains(e.SessionId) && e.State == EnrollmentState.Active)
            .ToListAsync(cancellationToken);
        var studentIds = enrollments.Select(e => e.StudentId).Distinct().ToList();
        var studentsById = await _db.Students
            .Where(s => studentIds.Contains(s.Id) && s.UserId != null)
            .ToDictionaryAsync(s => s.Id, cancellationToken);

        var queued = 0;
        foreach (var session in upcomingSessions)
        {
            foreach (var enrollment in enrollments.Where(e => e.SessionId == session.Id))
            {
                if (!studentsById.TryGetValue(enrollment.StudentId, out var student))
                {
                    continue; // no independent login to notify (a guardian-only child)
                }

                var recipientUserId = student.UserId!.Value;
                if (alreadyRemindedPairs.Contains((session.Id, recipientUserId)))
                {
                    continue; // idempotent: already queued for this exact (session, recipient)
                }

                var payload = JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["StudentName"] = student.FullName,
                    ["SessionId"] = session.Id.ToString(),
                });
                _db.NotificationOutboxItems.Add(new NotificationOutboxItem(
                    NotificationEvent.ZoomLink5Min, NotificationChannel.WhatsApp, recipientUserId, payload, now, session.Id));
                alreadyRemindedPairs.Add((session.Id, recipientUserId)); // guards against this same loop double-queuing
                queued++;
            }
        }

        if (queued > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Queued {Count} 5-minute-before reminders.", queued);
        }
    }
}
