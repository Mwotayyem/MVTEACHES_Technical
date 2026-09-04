using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.People;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;
using Xunit;

namespace MVTeaches.Tests.Web;

/// <summary>
/// Owner decision 2026-09-04, the /Admin/Teachers screen. Two changes are
/// asserted here because both are about what the FORM sends rather than what a
/// service does when asked correctly: teaching permission is now ticked as
/// courses × levels in one submission, and a pay rate no longer asks for a
/// start date because it always starts today.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class AdminTeacherFormTests : IClassFixture<AuthorizationTests.Factory>
{
    private readonly AuthorizationTests.Factory _factory;
    private const string Password = "CorrectHorse123!";
    private static long _idSeed = 55_000_000;

    public AdminTeacherFormTests(TestDatabaseFixture fixture, AuthorizationTests.Factory factory)
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__MvTeaches", fixture.ConnectionString);
        _factory = factory;
    }

    private static long NextId() => Interlocked.Increment(ref _idSeed);

    private static readonly Regex AntiforgeryTokenPattern = new(
        "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");

    private HttpClient CreateClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>A SystemAdmin, so the permission layer is out of the way — this
    /// class is about the forms, and AdminPermissionTests already covers who
    /// may submit them.</summary>
    private async Task<HttpClient> SystemAdminClientAsync(string label)
    {
        string email;
        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            if (!await roleManager.RoleExistsAsync(RoleNames.SystemAdmin))
            {
                await roleManager.CreateAsync(new ApplicationRole(RoleNames.SystemAdmin));
            }

            email = $"tform-{label}-{Guid.NewGuid():N}@test.mvteaches.local";
            var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
            var created = await userManager.CreateAsync(user, Password);
            Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
            await userManager.AddToRoleAsync(user, RoleNames.SystemAdmin);
        }

        var client = CreateClient();
        var loginHtml = await client.GetStringAsync("/Account/Login");
        var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AntiforgeryTokenPattern.Match(loginHtml).Groups[1].Value,
            ["Input.Email"] = email,
            ["Input.Password"] = Password,
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    private async Task<long> SeedTeacherAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
        var user = new ApplicationUser
        {
            UserName = $"tform-teacher-{Guid.NewGuid():N}",
            Email = $"tform-teacher-{Guid.NewGuid():N}@test.mvteaches.local",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var teacher = new Teacher(user.Id, $"Form Test Teacher {Guid.NewGuid():N}"[..30], "Asia/Amman");
        db.Teachers.Add(teacher);
        await db.SaveChangesAsync();
        return teacher.Id;
    }

    private async Task<(long CourseA, long CourseB, int LevelA, int LevelB)> SeedCoursesAndLevelsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();

        var a = new Course($"TFORM-A-{NextId()}", "دورة", "Course A");
        var b = new Course($"TFORM-B-{NextId()}", "دورة", "Course B");
        db.Courses.AddRange(a, b);

        var levelA = (int)NextId();
        var levelB = (int)NextId();
        db.Levels.Add(new Level(levelA, $"TF{levelA}", "مستوى", "Level", levelA));
        db.Levels.Add(new Level(levelB, $"TF{levelB}", "مستوى", "Level", levelB));
        await db.SaveChangesAsync();

        return (a.Id, b.Id, levelA, levelB);
    }

    /// <summary>Owner decision 2026-09-04: the course picker is multi-select.
    /// Two courses and two levels ticked once produce all four grants, because
    /// four (course, level) rows is what the table stores anyway — a teacher
    /// with the same levels in three subjects was filling this form in three
    /// times.</summary>
    [Fact]
    public async Task Granting_two_courses_and_two_levels_at_once_creates_all_four_pairs()
    {
        var teacherId = await SeedTeacherAsync();
        var (courseA, courseB, levelA, levelB) = await SeedCoursesAndLevelsAsync();
        var client = await SystemAdminClientAsync("grant");

        var html = await client.GetStringAsync("/Admin/Teachers");
        var form = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", AntiforgeryTokenPattern.Match(html).Groups[1].Value),
            new("LevelGrant.TeacherId", teacherId.ToString()),
            new("LevelGrant.CourseIds", courseA.ToString()),
            new("LevelGrant.CourseIds", courseB.ToString()),
            new("LevelGrant.LevelIds", levelA.ToString()),
            new("LevelGrant.LevelIds", levelB.ToString()),
        };

        var response = await client.PostAsync("/Admin/Teachers?handler=GrantLevel", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
        var grants = await db.TeacherLevelAssignments.Where(g => g.TeacherId == teacherId).ToListAsync();

        Assert.Equal(4, grants.Count);
        Assert.Contains(grants, g => g.CourseId == courseA && g.LevelId == levelA);
        Assert.Contains(grants, g => g.CourseId == courseA && g.LevelId == levelB);
        Assert.Contains(grants, g => g.CourseId == courseB && g.LevelId == levelA);
        Assert.Contains(grants, g => g.CourseId == courseB && g.LevelId == levelB);
    }

    /// <summary>Ticking no course is refused rather than silently granting
    /// nothing, and nothing is written.</summary>
    [Fact]
    public async Task Granting_with_no_course_ticked_writes_nothing()
    {
        var teacherId = await SeedTeacherAsync();
        var (_, _, levelA, _) = await SeedCoursesAndLevelsAsync();
        var client = await SystemAdminClientAsync("grant-nocourse");

        var html = await client.GetStringAsync("/Admin/Teachers");
        var form = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", AntiforgeryTokenPattern.Match(html).Groups[1].Value),
            new("LevelGrant.TeacherId", teacherId.ToString()),
            new("LevelGrant.LevelIds", levelA.ToString()),
        };

        var response = await client.PostAsync("/Admin/Teachers?handler=GrantLevel", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
        Assert.Empty(await db.TeacherLevelAssignments.Where(g => g.TeacherId == teacherId).ToListAsync());
    }

    /// <summary>Owner decision 2026-09-04: the rate form no longer asks when the
    /// rate starts. The column is still there and payroll still selects on it —
    /// a rate with no start date could never resolve at all — so the absence of
    /// the field has to become today, not null.</summary>
    [Fact]
    public async Task A_pay_rate_created_without_a_date_starts_today()
    {
        var teacherId = await SeedTeacherAsync();
        var client = await SystemAdminClientAsync("rate");

        var html = await client.GetStringAsync("/Admin/Teachers");

        // The field really is gone from the screen, not merely ignored.
        Assert.DoesNotContain("NewRate_EffectiveFrom", html);

        var response = await client.PostAsync("/Admin/Teachers?handler=CreateRate", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AntiforgeryTokenPattern.Match(html).Groups[1].Value,
            ["NewRate.TeacherId"] = teacherId.ToString(),
            ["NewRate.Amount"] = "17",
            ["NewRate.Currency"] = "JOD",
            ["NewRate.Unit"] = "PerHour",
        }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
        var rate = await db.TeacherRates.SingleAsync(r => r.TeacherId == teacherId);

        Assert.Equal(SystemClock.Instance.GetCurrentInstant().InUtc().Date, rate.EffectiveFrom);
        Assert.Equal(17m, rate.Rate.Amount);
        Assert.Null(rate.EffectiveTo); // the only one, and open
    }
}
