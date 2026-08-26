using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MVTeaches.Application.Integrations;
using MVTeaches.Domain.Notifications;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;

namespace MVTeaches.Infrastructure.Notifications;

/// <summary>
/// §25 of the master engineering prompt: a genuinely async/scheduled job,
/// idempotent (each outbox item is only ever moved Pending → Sent/Failed;
/// re-running the job never double-sends because a Sent/Failed item is no
/// longer picked up by the WHERE clause), with failures fully visible
/// (LastError/AttemptCount on the outbox row, never a swallowed exception).
///
/// Registered as a recurring Hangfire job — see Program.cs.
/// </summary>
public class NotificationDispatchJob
{
    private const int MaxAttempts = 5;

    private readonly MvTeachesDbContext _db;
    private readonly IEnumerable<INotificationSender> _senders;
    private readonly IClock _clock;
    private readonly ILogger<NotificationDispatchJob> _logger;

    public NotificationDispatchJob(MvTeachesDbContext db, IEnumerable<INotificationSender> senders, IClock clock,
        ILogger<NotificationDispatchJob> logger)
    {
        _db = db;
        _senders = senders;
        _clock = clock;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = _clock.GetCurrentInstant();

        // §33.4's dispatcher scan index: WHERE status = 'Pending'.
        var due = await _db.NotificationOutboxItems
            .Where(n => n.Status == NotificationOutboxStatus.Pending && n.ScheduledForUtc <= now)
            .OrderBy(n => n.ScheduledForUtc)
            .Take(100) // bounded batch — never an unbounded scan per §38 performance review
            .ToListAsync(cancellationToken);

        foreach (var item in due)
        {
            var sender = _senders.FirstOrDefault(s => s.Channel == item.Channel);
            if (sender is null)
            {
                item.MarkPermanentlyFailed($"No sender registered for channel {item.Channel}.");
                continue;
            }

            item.MarkSending();
            await _db.SaveChangesAsync(cancellationToken);

            var result = await sender.SendAsync(item, cancellationToken);
            if (result.Success)
            {
                item.MarkSent(_clock.GetCurrentInstant());
            }
            else if (item.AttemptCount + 1 >= MaxAttempts)
            {
                item.MarkPermanentlyFailed(result.Error ?? "Unknown failure.");
                _logger.LogError("Notification {Id} permanently failed after {Attempts} attempts: {Error}",
                    item.Id, item.AttemptCount + 1, result.Error);
            }
            else
            {
                item.MarkFailedAndRetry(result.Error ?? "Unknown failure.");
            }

            await _db.SaveChangesAsync(cancellationToken);
        }
    }
}
