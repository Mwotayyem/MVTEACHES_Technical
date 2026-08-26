using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MVTeaches.Application.Integrations;
using MVTeaches.Domain.Notifications;
using System.Text.Json;

namespace MVTeaches.Infrastructure.Integrations.Email;

/// <summary>
/// A REAL, working implementation (unlike the Zoom/WhatsApp stubs) — SMTP is a
/// stable, standard protocol this doesn't need live external docs to
/// implement correctly, and D-57 relies on email actually working as the OTP
/// backup channel. Uses the framework's built-in SmtpClient; for production
/// hardening consider migrating to MailKit (Microsoft's own recommendation
/// for new development), which is a drop-in swap behind this same interface.
/// </summary>
public class SmtpEmailSender : INotificationSender
{
    private readonly SmtpOptions _options;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IOptions<SmtpOptions> options, ILogger<SmtpEmailSender> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public NotificationChannel Channel => NotificationChannel.Email;

    public async Task<NotificationSendResult> SendAsync(NotificationOutboxItem item, CancellationToken cancellationToken)
    {
        if (!_options.IsConfigured)
        {
            _logger.LogWarning("Email send requested for outbox item {Id} but SMTP is not configured.", item.Id);
            return new NotificationSendResult(false, null, "SMTP is not configured (SmtpOptions).");
        }

        try
        {
            var payload = JsonSerializer.Deserialize<Dictionary<string, string>>(item.PayloadJson) ?? new();
            if (!payload.TryGetValue("to", out var to) || !payload.TryGetValue("subject", out var subject)
                || !payload.TryGetValue("body", out var body))
            {
                return new NotificationSendResult(false, null, "Payload missing required 'to'/'subject'/'body' fields.");
            }

            using var client = new SmtpClient(_options.Host, _options.Port)
            {
                EnableSsl = _options.UseSsl,
                Credentials = string.IsNullOrWhiteSpace(_options.Username)
                    ? null
                    : new NetworkCredential(_options.Username, _options.Password),
            };

            using var message = new MailMessage(
                new MailAddress(_options.FromAddress, _options.FromDisplayName),
                new MailAddress(to))
            {
                Subject = subject,
                Body = body,
                IsBodyHtml = false,
            };

            await client.SendMailAsync(message, cancellationToken);
            return new NotificationSendResult(true, ProviderMessageId: null, Error: null);
        }
        catch (Exception ex)
        {
            // Never rethrow into the dispatcher — a failed send must become a
            // recorded outbox failure/retry, not an unhandled job exception.
            _logger.LogError(ex, "Failed to send email for outbox item {Id}.", item.Id);
            return new NotificationSendResult(false, null, ex.Message);
        }
    }
}
