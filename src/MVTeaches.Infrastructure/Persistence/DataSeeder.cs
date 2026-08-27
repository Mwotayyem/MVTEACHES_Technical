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
        await SeedCountriesAsync(db, cancellationToken);
        await SeedLevelsAsync(db, cancellationToken);
        await SeedCoursesAsync(db, cancellationToken);

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

    /// <summary>§24.1 (D-06/D-07/D-08/D-09, extended by D-53) — Jordan (JOD) and
    /// Palestine (ILS) are the two named markets, plus one mandatory "rest of
    /// world" USD row (IsDefaultIntl) that catches every other phone country
    /// code instead of requiring a row per country. The admin can promote any
    /// country to its own row later (§24.1's note) — this seed only guarantees
    /// the three the study names as day-one requirements.</summary>
    private static async Task SeedCountriesAsync(MvTeachesDbContext db, CancellationToken ct)
    {
        if (await db.Countries.AnyAsync(ct))
        {
            return;
        }

        db.Countries.AddRange(
            new Country(1, "JO", "الأردن", "Jordan", "JOD", "+962", "Asia/Amman"),
            new Country(2, "PS", "فلسطين", "Palestine", "ILS", "+970", "Asia/Hebron"),
            // §24.1: no single calling code identifies "the rest of the world" —
            // this row is matched by NOT being JO/PS, never by phone_country_code.
            // "ZZ" is an ISO-3166-1 user-assigned ("unspecified") code — deliberately
            // not a real country's code, since this row represents no single country.
            new Country(3, "ZZ", "بقية العالم", "Rest of world", "USD", "", "Etc/UTC", isDefaultIntl: true));
    }

    /// <summary>§11 (D-30's predecessor table) — the six CEFR levels, data not an
    /// enum so the admin can rename/reorder them without a code change. Deliberately
    /// no per-level hour requirement here (D-65 moved that to a single global
    /// setting — see the removed `required_minutes` column note on Level.cs).</summary>
    private static async Task SeedLevelsAsync(MvTeachesDbContext db, CancellationToken ct)
    {
        if (await db.Levels.AnyAsync(ct))
        {
            return;
        }

        db.Levels.AddRange(
            new Level(1, "A1", "مبتدئ", "Beginner", 1),
            new Level(2, "A2", "مبتدئ متقدم", "Elementary", 2),
            new Level(3, "B1", "متوسط", "Intermediate", 3),
            new Level(4, "B2", "فوق المتوسط", "Upper-intermediate", 4),
            new Level(5, "C1", "متقدم", "Advanced", 5),
            new Level(6, "C2", "محترف", "Proficient", 6));
    }

    /// <summary>D-41: exactly one course in MVP — "دورة واحدة بستة مستويات"
    /// (one course, six levels). No IELTS/TOEFL/Corporate. `Courses` stays a
    /// real entity (not hardcoded) purely so a future course costs nothing to add.</summary>
    private static async Task SeedCoursesAsync(MvTeachesDbContext db, CancellationToken ct)
    {
        if (await db.Courses.AnyAsync(ct))
        {
            return;
        }

        db.Courses.Add(new Course("GENERAL-ENGLISH", "تقوية إنجليزي عام", "General English"));
    }
}
