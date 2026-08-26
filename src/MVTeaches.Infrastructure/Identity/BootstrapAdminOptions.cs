namespace MVTeaches.Infrastructure.Identity;

/// <summary>
/// Solves the ordinary chicken-and-egg problem every fresh install has: D-28
/// says only an existing SystemAdmin can create the Teacher/Guardian/etc.
/// accounts, and only an existing Admin/SystemAdmin can create another admin
/// — but the very first deployment has none. This is a ONE-TIME bootstrap,
/// not a standing account-creation feature: see BootstrapAdminSeeder, which
/// only ever acts while the Admin role has zero members.
///
/// Deliberately never defaulted to a real value anywhere in source — see
/// appsettings.json's placeholder section and /docs/deployment/README.md.
/// </summary>
public class BootstrapAdminOptions
{
    public const string SectionName = "Bootstrap";

    public string? AdminEmail { get; set; }
    public string? AdminPassword { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(AdminEmail) && !string.IsNullOrWhiteSpace(AdminPassword);
}
