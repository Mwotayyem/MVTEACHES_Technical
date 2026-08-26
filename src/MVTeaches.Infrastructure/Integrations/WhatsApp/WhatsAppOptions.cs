namespace MVTeaches.Infrastructure.Integrations.WhatsApp;

/// <summary>Technical Study §30/D-57 (Meta Cloud API). Populated once the
/// owner's Meta Business verification completes (README: "بانتظار رد Meta").</summary>
public class WhatsAppOptions
{
    public const string SectionName = "WhatsApp";

    public string? PhoneNumberId { get; set; }
    public string? AccessToken { get; set; }
    public string? BusinessAccountId { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(PhoneNumberId) && !string.IsNullOrWhiteSpace(AccessToken);
}
