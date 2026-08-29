using NodaTime;

namespace MVTeaches.Domain.Notifications;

public enum NotificationOutboxStatus
{
    Pending,
    Sending,
    Sent,
    Failed,
}

/// <summary>
/// Technical Study §30/§33.4 (`notification_outbox WHERE status='Pending'` is
/// the index Hangfire's dispatcher scans). A durable outbox, not a fire-and-forget
/// call — every send attempt, success, and failure is recorded so a WhatsApp/Meta
/// outage never silently drops a message (§7 of the master engineering prompt:
/// build the surrounding logic and failure states even without live credentials).
/// </summary>
public class NotificationOutboxItem
{
    public long Id { get; private set; }

    public NotificationEvent Event { get; private set; }
    public NotificationChannel Channel { get; private set; }

    /// <summary>Recipient's user id — resolved to phone/email by the provider at send time.</summary>
    public long RecipientUserId { get; private set; }

    /// <summary>Owner decision 2026-08-30 rule 9: set only for a session-tied
    /// event (e.g. ZoomLink5Min) so a periodic job can check "has this
    /// (Event, SessionId, RecipientUserId) already been queued" before
    /// enqueuing another one — the idempotency key a repeating Hangfire job
    /// needs to never double-remind the same student for the same
    /// session. Null for every event that isn't about one specific session
    /// (a purchase, a compensation decision).</summary>
    public long? SessionId { get; private set; }

    /// <summary>Template placeholders as JSON — the template itself is a separate,
    /// admin-editable record (NotificationTemplate), never a hardcoded string.</summary>
    public string PayloadJson { get; private set; } = string.Empty;

    public NotificationOutboxStatus Status { get; private set; } = NotificationOutboxStatus.Pending;
    public Instant ScheduledForUtc { get; private set; }
    public Instant? SentAtUtc { get; private set; }
    public int AttemptCount { get; private set; }
    public string? LastError { get; private set; }

    private NotificationOutboxItem() { }

    public NotificationOutboxItem(NotificationEvent @event, NotificationChannel channel, long recipientUserId,
        string payloadJson, Instant scheduledForUtc, long? sessionId = null)
    {
        Event = @event;
        Channel = channel;
        RecipientUserId = recipientUserId;
        PayloadJson = payloadJson;
        ScheduledForUtc = scheduledForUtc;
        SessionId = sessionId;
    }

    public void MarkSending() => Status = NotificationOutboxStatus.Sending;

    public void MarkSent(Instant nowUtc)
    {
        Status = NotificationOutboxStatus.Sent;
        SentAtUtc = nowUtc;
    }

    /// <summary>Idempotent retry: caller decides the backoff; this only records
    /// the attempt and resets to Pending for a future retry.</summary>
    public void MarkFailedAndRetry(string error)
    {
        AttemptCount++;
        LastError = error;
        Status = NotificationOutboxStatus.Pending;
    }

    public void MarkPermanentlyFailed(string error)
    {
        AttemptCount++;
        LastError = error;
        Status = NotificationOutboxStatus.Failed;
    }
}
