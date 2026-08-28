using Microsoft.Extensions.Logging;
using MVTeaches.Application.Integrations;
using MVTeaches.Domain.Notifications;

namespace MVTeaches.Infrastructure.Integrations.WhatsApp;

/// <summary>
/// PREPARED, NOT IMPLEMENTED. (The Zoom stub this used to point at is gone —
/// the 2026-08-29 owner clarification replaced it with real, written Zoom and
/// Google Meet clients; WhatsApp is now the only provider still stubbed.)
/// The real Meta Cloud API client is not written yet: no verified
/// WhatsApp Business account exists (README: "بانتظار رد Meta"), and Meta's
/// exact template/messaging API shape must be read from Meta's current
/// documentation once that account exists, not guessed now (D-88's sibling
/// rule for WhatsApp — §7 of the master engineering prompt explicitly warns
/// against inventing Meta pricing or behavior).
///
/// Returns a clean failure (not an exception) so the outbox dispatcher's
/// retry/failure-visibility logic — which IS fully built — can be exercised
/// and tested today, without pretending a message was actually delivered.
/// </summary>
public class NotConfiguredWhatsAppSender : INotificationSender
{
    private readonly ILogger<NotConfiguredWhatsAppSender> _logger;

    public NotConfiguredWhatsAppSender(ILogger<NotConfiguredWhatsAppSender> logger)
    {
        _logger = logger;
    }

    public NotificationChannel Channel => NotificationChannel.WhatsApp;

    public Task<NotificationSendResult> SendAsync(NotificationOutboxItem item, CancellationToken cancellationToken)
    {
        _logger.LogWarning("WhatsApp send requested for outbox item {Id} but WhatsApp is not configured.", item.Id);
        return Task.FromResult(new NotificationSendResult(false, null, "WhatsApp is not configured (WhatsAppOptions)."));
    }
}
