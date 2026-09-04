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

    /// <summary>One row of <see cref="CourseCatalogue"/>. A plain record rather
    /// than a <see cref="Course"/> so the list can be a shared static without
    /// handing the same tracked entity instance to two DbContexts.</summary>
    public sealed record CourseSeed(string Code, string NameAr, string NameEn);

    /// <summary>The owner's list, in the owner's order and wording — public so
    /// it can be asserted directly (see CourseCatalogueTests) without standing
    /// a database up. The English name is a plain translation of the Arabic,
    /// never a re-interpretation: the Arabic name is the one the centre
    /// advertises. Note there is no <c>isLeveled</c> column here at all —
    /// every course takes Course's default of true, which is the owner's
    /// decision expressed in the only place it cannot be forgotten.</summary>
    public static IReadOnlyList<CourseSeed> CourseCatalogue { get; } = new[]
    {
        // ---- English ------------------------------------------------
        new CourseSeed("ENG-CONV-KIDS", "المحادثة الإنجليزية للأطفال", "English Conversation - Kids"),
        new CourseSeed("ENG-CONV-ADULTS", "المحادثة الإنجليزية للكبار", "English Conversation - Adults"),
        new CourseSeed("ENG-GENERAL-KIDS", "الإنجليزي العام للأطفال", "General English - Kids"),
        // Keeps the original code on purpose: every course_id already in
        // Local Staging points at this row.
        new CourseSeed("GENERAL-ENGLISH", "الإنجليزي العام للكبار", "General English - Adults"),
        new CourseSeed("IELTS", "دورات تحضيرية للإيلتس IELTS", "IELTS Preparation"),
        new CourseSeed("IELTS-FOUNDATION", "دورات تأسيسية للإيلتس IELTS", "IELTS Foundation"),
        new CourseSeed("TOEFL", "دورات تحضيرية للتوفل TOEFL", "TOEFL Preparation"),
        new CourseSeed("TOEFL-FOUNDATION", "دورات تأسيسية للتوفل TOEFL", "TOEFL Foundation"),
        new CourseSeed("BUSINESS-ENGLISH", "دورات إدارة الأعمال باللغة الإنجليزية", "Business English"),
        new CourseSeed("SAT", "SAT لجميع الصفوف", "SAT - All Grades"),
        new CourseSeed("IG", "IG لجميع الصفوف", "IG - All Grades"),

        // ---- Arabic -------------------------------------------------
        new CourseSeed("ARB-CONV-KIDS", "المحادثة العربية للأطفال", "Arabic Conversation - Kids"),
        new CourseSeed("ARB-CONV-ADULTS", "المحادثة العربية للكبار", "Arabic Conversation - Adults"),
        new CourseSeed("ARB-GENERAL-KIDS", "اللغة العربية العامة للأطفال", "General Arabic - Kids"),
        new CourseSeed("ARABIC", "اللغة العربية العامة للكبار", "General Arabic - Adults"),
        new CourseSeed("QURAN-KIDS", "القرآن الكريم للأطفال", "Holy Quran - Kids"),
        new CourseSeed("QURAN", "القرآن الكريم للكبار", "Holy Quran - Adults"),

        // ---- Spanish ------------------------------------------------
        new CourseSeed("SPA-CONV-KIDS", "المحادثة الإسبانية للأطفال", "Spanish Conversation - Kids"),
        new CourseSeed("SPA-CONV-ADULTS", "المحادثة الإسبانية للكبار", "Spanish Conversation - Adults"),
        new CourseSeed("SPA-GENERAL-KIDS", "اللغة الإسبانية العامة للأطفال", "General Spanish - Kids"),
        new CourseSeed("SPANISH", "اللغة الإسبانية العامة للكبار", "General Spanish - Adults"),
    };

    /// <summary>Owner decision 2026-09-04 (revised the same day), superseding
    /// D-41's "exactly one course in MVP": the centre teaches twenty-one named
    /// courses, and recording all of them as General English made the
    /// catalogue, the levels and the teacher assignments all describe
    /// something untrue.
    ///
    /// <para><b>Every course is levelled.</b> An earlier draft of this list
    /// marked IELTS, TOEFL and Quran <c>isLeveled: false</c>, reasoning that no
    /// level ladder had been defined for them. The owner corrected that
    /// directly: every course uses the SAME existing A1-C2 ladder seeded by
    /// <see cref="SeedLevelsAsync"/>, and no course is level-less. No new level
    /// scheme is invented here, and none should be. A student therefore holds
    /// one current level PER COURSE, a package is published for a
    /// (course, level) pair, and a teacher is authorised for a (course, level)
    /// pair — see StudentLevel, PricingPlan and TeacherLevelAssignment.</para>
    ///
    /// <para><b>Why some codes look older than others.</b> Seven codes here
    /// (GENERAL-ENGLISH, ARABIC, SPANISH, BUSINESS-ENGLISH, IELTS, TOEFL,
    /// QURAN) already exist in databases seeded earlier today, and
    /// GENERAL-ENGLISH in particular is what every existing course_id in Local
    /// Staging points at. Reusing those codes rather than minting new ones is
    /// deliberate: no row moves, no foreign key is rewritten, and
    /// LocalDevelopmentSeeder/StagingSeeder — both of which look a course up by
    /// the literal code "GENERAL-ENGLISH" — keep working untouched. Their
    /// display names are brought onto the owner's list by the
    /// OwnerCourseCatalogue migration, a one-time data fix rather than
    /// something re-applied on every start-up, so an admin who renames a course
    /// afterwards keeps their name.</para>
    ///
    /// <para>Each row is added only if its code is missing, so this is safe to
    /// run against a database that already holds some of them, and adding a
    /// further course later is one more line rather than a migration. Nothing
    /// here ever deletes or deactivates a course: retiring one is an admin
    /// action, because a course carries subscriptions, sessions and payroll
    /// history behind it.</para></summary>
    private static async Task SeedCoursesAsync(MvTeachesDbContext db, CancellationToken ct)
    {
        var existingCodes = (await db.Courses.Select(c => c.Code).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var seed in CourseCatalogue.Where(c => !existingCodes.Contains(c.Code)))
        {
            db.Courses.Add(new Course(seed.Code, seed.NameAr, seed.NameEn));
        }
    }
}
