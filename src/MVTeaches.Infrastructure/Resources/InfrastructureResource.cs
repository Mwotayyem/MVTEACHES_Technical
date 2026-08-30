namespace MVTeaches.Infrastructure.Resources;

/// <summary>
/// Marker type for <c>IStringLocalizer&lt;InfrastructureResource&gt;</c> —
/// the resx-key convention mirrors <c>MVTeaches.Web.Resources.SharedResource</c>
/// exactly, but lives in this project because the services that need it
/// (MeetingProvisioningService, PlacementTestAdminService) are Infrastructure-
/// layer code that must not depend on the Web project.
/// </summary>
public sealed class InfrastructureResource
{
}
