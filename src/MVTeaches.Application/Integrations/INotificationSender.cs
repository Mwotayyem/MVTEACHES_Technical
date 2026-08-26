using MVTeaches.Domain.Notifications;

namespace MVTeaches.Application.Integrations;

public record NotificationSendResult(bool Success, string? ProviderMessageId, string? Error);

/// <summary>
/// §7 of the master engineering prompt / Technical Study §30. One
/// implementation per channel (WhatsApp via Meta Cloud API, Email as the
/// documented backup for OTP — D-57). The outbox (NotificationOutboxItem)
/// is the durable record; this interface is only the "make the actual call"
/// step a Hangfire job invokes for one outbox item at a time.
/// </summary>
public interface INotificationSender
{
    NotificationChannel Channel { get; }

    Task<NotificationSendResult> SendAsync(NotificationOutboxItem item, CancellationToken cancellationToken);
}
