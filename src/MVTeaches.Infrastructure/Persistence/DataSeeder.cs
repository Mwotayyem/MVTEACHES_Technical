using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MVTeaches.Application.Settings;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Settings;
using MVTeaches.Infrastructure.Identity;

namespace MVTeaches.Infrastructure.Persistence;

/// <summary>
/// One-time reference data (§19.5's settings defaults, §12.1's three age
/// groups, §6's roles) — idempotent, safe to run on every startup. This is
/// seed DATA, not a migration, because it must remain editable by an admin
/// afterward (D-65) without a new deployment.
/// </summary>
public static class DataSeeder
{
    public static async Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var db = services.GetRequiredService<MvTeachesDbContext>();
        var roleManager = services.GetRequiredService<RoleManager<ApplicationRole>>();

        await SeedRolesAsync(roleManager);
        await SeedAgeGroupsAsync(db, cancellationToken);
        await SeedSettingsAsync(db, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        // A separate step, deliberately AFTER the roles above exist and are
        // committed — SeedBootstrapAdminAsync needs the Admin role to already
        // be present to add the user to it.
        await SeedBootstrapAdminAsync(services, cancellationToken);
    }

    /// <summary>See BootstrapAdminOptions's remarks. Only ever acts while the
    /// Admin role has zero members — on every later startup this is a no-op,
    /// even if BootstrapAdminOptions is still configured.</summary>
    private static async Task SeedBootstrapAdminAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var options = services.GetRequiredService<IOptions<BootstrapAdminOptions>>().Value;
        if (!options.IsConfigured)
        {
            return;
        }

        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("MVTeaches.BootstrapAdmin");

        var anyAdminExists = (await userManager.GetUsersInRoleAsync(RoleNames.Admin)).Count > 0;
        if (anyAdminExists)
        {
            return;
        }

        var user = new ApplicationUser
        {
            UserName = options.AdminEmail,
            Email = options.AdminEmail,
            EmailConfirmed = true,
        };

        var createResult = await userManager.CreateAsync(user, options.AdminPassword!);
        if (!createResult.Succeeded)
        {
            // Never silently swallowed — a misconfigured bootstrap password
            // (too short, etc.) must be visible in the logs, not a mystery
            // "I can't log in" report later.
            logger.LogError("Bootstrap admin creation failed: {Errors}",
                string.Join("; ", createResult.Errors.Select(e => e.Description)));
            return;
        }

        await userManager.AddToRoleAsync(user, RoleNames.Admin);
        logger.LogWarning(
            "Bootstrap admin account created for {Email}. Remove Bootstrap:AdminEmail/AdminPassword from configuration now that an Admin exists.",
            options.AdminEmail);
    }

    private static async Task SeedRolesAsync(RoleManager<ApplicationRole> roleManager)
    {
        foreach (var role in RoleNames.All)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                await roleManager.CreateAsync(new ApplicationRole(role));
            }
        }
    }

    private static async Task SeedAgeGroupsAsync(MvTeachesDbContext db, CancellationToken ct)
    {
        if (await db.AgeGroups.AnyAsync(ct))
        {
            return;
        }

        // §12.1 (D-04) — exactly these three, no more, no hardcoded thresholds
        // anywhere else in the codebase; every age-group check reads THESE rows.
        db.AgeGroups.AddRange(
            new AgeGroup(1, "Kids", 5, 12, isMinor: true),
            new AgeGroup(2, "Teens", 13, 17, isMinor: true),
            new AgeGroup(3, "Adults", 18, null, isMinor: false));
    }

    private static async Task SeedSettingsAsync(MvTeachesDbContext db, CancellationToken ct)
    {
        var existingKeys = await db.Settings.Select(s => s.Key).ToListAsync(ct);

        foreach (var (key, defaultValue) in SettingDefaults.Values)
        {
            if (!existingKeys.Contains(key))
            {
                db.Settings.Add(new Setting(key, defaultValue));
            }
        }
    }
}
