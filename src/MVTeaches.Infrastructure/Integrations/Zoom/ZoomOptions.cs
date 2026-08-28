namespace MVTeaches.Infrastructure.Integrations.Zoom;

/// <summary>
/// Owner clarification (2026-08-29), superseding the earlier Server-to-Server
/// OAuth design entirely: MVTeaches is a user-authorized Zoom OAuth app (a
/// Zoom "General App" configured for OAuth, or the current Marketplace
/// equivalent that lets independent external teachers authorize it). There
/// is no centre-level AccountId here on purpose — every meeting is created
/// under the AUTHORIZING TEACHER'S own Zoom account, never a shared one.
/// </summary>
public class ZoomOptions
{
    public const string SectionName = "Zoom";

    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }

    /// <summary>Must exactly match one of the OAuth app's registered redirect
    /// URIs in the Zoom Marketplace/App configuration.</summary>
    public string? RedirectUri { get; set; }

    /// <summary>The Webhook "Secret Token" from the app's Feature &gt; Event
    /// Subscriptions page — used to validate x-zm-signature, never sent
    /// anywhere, never logged.</summary>
    public string? WebhookSecretToken { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret) && !string.IsNullOrWhiteSpace(RedirectUri);
}
