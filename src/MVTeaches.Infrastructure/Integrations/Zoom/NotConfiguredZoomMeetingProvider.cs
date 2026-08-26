using Microsoft.Extensions.Logging;
using MVTeaches.Application.Integrations;

namespace MVTeaches.Infrastructure.Integrations.Zoom;

/// <summary>
/// PREPARED, NOT IMPLEMENTED (see the final engineering report's vocabulary).
/// This is the ONLY IZoomMeetingProvider registered until real Server-to-Server
/// OAuth credentials exist AND the concrete Zoom REST API client has been
/// written against Zoom's actual, currently-published API documentation.
///
/// That client is deliberately NOT included here: writing it now, without a
/// live Zoom account to verify against and without fetching Zoom's current
/// API reference, would mean guessing endpoint shapes, field names, and auth
/// flow details from training data that may be stale — exactly what the
/// master engineering prompt's §29/§44 forbid ("never fabricate provider
/// behavior", "do not guess APIs ... verify by reading docs before
/// asserting"). The correct next step, once ZoomOptions.IsConfigured is true,
/// is to implement a ZoomServerToServerMeetingProvider against Zoom's current
/// documentation and swap the DI registration in Program.cs — this interface
/// and every caller of it stay unchanged.
/// </summary>
public class NotConfiguredZoomMeetingProvider : IZoomMeetingProvider
{
    private readonly ILogger<NotConfiguredZoomMeetingProvider> _logger;

    public NotConfiguredZoomMeetingProvider(ILogger<NotConfiguredZoomMeetingProvider> logger)
    {
        _logger = logger;
    }

    public Task<ZoomMeetingHandle> CreateMeetingAsync(ZoomMeetingRequest request, CancellationToken cancellationToken)
    {
        _logger.LogWarning("Zoom meeting creation requested for session {SessionId} but Zoom is not configured.", request.SessionId);
        throw new IntegrationNotConfiguredException("Zoom", "no Server-to-Server OAuth credentials configured (ZoomOptions).");
    }

    public Task<string> GetJoinUrlAsync(long sessionId, CancellationToken cancellationToken) =>
        throw new IntegrationNotConfiguredException("Zoom", "no Server-to-Server OAuth credentials configured (ZoomOptions).");

    public Task CancelMeetingAsync(long sessionId, CancellationToken cancellationToken) =>
        throw new IntegrationNotConfiguredException("Zoom", "no Server-to-Server OAuth credentials configured (ZoomOptions).");
}
