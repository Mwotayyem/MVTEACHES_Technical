namespace MVTeaches.Domain.Integrations;

/// <summary>
/// Owner clarification (2026-08-29): the centre never buys, assigns, or
/// reimburses a video-meeting licence — each teacher connects their OWN
/// account under whichever provider they actually have. This enum is the
/// provider-neutral seam everything else (connections, provisioned
/// meetings, OAuth state) is keyed on; a third provider is added here and
/// nowhere else needs to change its shape.
/// </summary>
public enum VideoProviderType
{
    Zoom,
    GoogleMeet,
}
