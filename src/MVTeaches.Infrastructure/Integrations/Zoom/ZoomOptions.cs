namespace MVTeaches.Infrastructure.Integrations.Zoom;

/// <summary>
/// Technical Study §28.3/§28.4/§1.9 of the Video & Child Data study: each
/// teacher has their own Zoom account and pays their own subscription — the
/// institute does not carry the licence cost. What THIS options class
/// captures is only the Server-to-Server OAuth app the platform itself needs
/// to automate meeting creation on a teacher's behalf (creating the meeting
/// shell, retrieving a join URL) — never a hosted-conferencing UI.
/// </summary>
public class ZoomOptions
{
    public const string SectionName = "Zoom";

    public string? AccountId { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AccountId) && !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}
