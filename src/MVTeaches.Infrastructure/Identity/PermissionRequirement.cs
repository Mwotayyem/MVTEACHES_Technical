using Microsoft.AspNetCore.Authorization;

namespace MVTeaches.Infrastructure.Identity;

/// <summary>
/// Carries exactly one <see cref="PermissionKeys"/> value — see
/// <see cref="PermissionAuthorizationHandler"/> for how it is checked. One
/// policy is registered per key (Program.cs loops <see cref="PermissionKeys.All"/>),
/// each wrapping one requirement, so a page or handler asks for a permission
/// by policy name and never touches this type directly.
/// </summary>
public sealed class PermissionRequirement : IAuthorizationRequirement
{
    public PermissionRequirement(string permissionKey)
    {
        PermissionKey = permissionKey;
    }

    public string PermissionKey { get; }
}
