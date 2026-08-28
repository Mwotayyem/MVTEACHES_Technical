namespace MVTeaches.Infrastructure.Integrations.GoogleMeet;

/// <summary>
/// Owner clarification (2026-08-29): a teacher-authorized Google OAuth
/// client (a Google Cloud project with the Google Meet REST API enabled and
/// an OAuth consent screen configured for external users), used to create
/// meeting spaces in the AUTHORIZING TEACHER'S own Google account.
/// </summary>
public class GoogleOptions
{
    public const string SectionName = "GoogleMeet";

    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? RedirectUri { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret) && !string.IsNullOrWhiteSpace(RedirectUri);
}
