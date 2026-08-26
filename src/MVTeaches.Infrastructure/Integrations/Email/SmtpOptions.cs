namespace MVTeaches.Infrastructure.Integrations.Email;

/// <summary>D-57: email is the documented backup channel for OTP and other
/// critical messages when WhatsApp is unavailable.</summary>
public class SmtpOptions
{
    public const string SectionName = "Smtp";

    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool UseSsl { get; set; } = true;
    public string FromAddress { get; set; } = "no-reply@mvteaches.local";
    public string FromDisplayName { get; set; } = "MVTEACHES";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);
}
