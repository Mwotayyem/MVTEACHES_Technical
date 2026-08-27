using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Delivery;
using MVTeaches.Domain.People;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;
using Xunit;

namespace MVTeaches.Tests.Web;

/// <summary>
/// §22's security review gap this closes: an automated, real-HTTP-request
/// check that every Admin-only page actually enforces
/// [Authorize(Roles = Admin, SystemAdmin)] — not just that the attribute is
/// present in source, but that an unauthenticated request and a
/// wrong-role-but-authenticated request are both actually turned away, and
/// only the right role gets through. Runs a REAL ASP.NET Core host
/// (WebApplicationFactory) against the SAME real PostgreSQL test database
/// the rest of this project uses — no mocks, no in-memory provider.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class AuthorizationTests : IClassFixture<AuthorizationTests.Factory>, IAsyncLifetime
{
    private readonly Factory _factory;

    public AuthorizationTests(TestDatabaseFixture fixture, Factory factory)
    {
        // WebApplicationFactory's host-building machinery for a top-level-statements
        // Program.cs runs the app's own `WebApplication.CreateBuilder(args)` — which
        // already includes environment variables as a default configuration source —
        // so setting this here (before the host is ever lazily built, i.e. before the
        // first Services/CreateClient access below) is the reliable way to point the
        // real app at the shared test database instead of appsettings.json's empty
        // placeholder. This is the SAME mechanism already verified live in this
        // project's manual deployment testing (see docs/deployment/STATUS.md).
        Environment.SetEnvironmentVariable("ConnectionStrings__MvTeaches", fixture.ConnectionString);
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        // Ensure a known Admin, Teacher, Guardian, and Student login exist —
        // idempotent, since this DB is shared across the whole test run.
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();

        foreach (var role in RoleNames.All)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                await roleManager.CreateAsync(new ApplicationRole(role));
            }
        }

        await EnsureUserAsync(userManager, AdminEmail, RoleNames.Admin);
        await EnsureUserAsync(userManager, TeacherEmail, RoleNames.Teacher);
        await EnsureUserAsync(userManager, GuardianEmail, RoleNames.Guardian);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private const string Password = "CorrectHorse123!";
    private const string AdminEmail = "authtest-admin@test.mvteaches.local";
    private const string TeacherEmail = "authtest-teacher@test.mvteaches.local";
    private const string OtherTeacherEmail = "authtest-teacher2@test.mvteaches.local";
    private const string GuardianEmail = "authtest-guardian@test.mvteaches.local";

    private static async Task<Teacher> EnsureLinkedTeacherAsync(MvTeachesDbContext db, long userId, string fullName)
    {
        var existing = await db.Teachers.FirstOrDefaultAsync(t => t.UserId == userId);
        if (existing is not null)
        {
            return existing;
        }

        var teacher = new Teacher(userId, fullName, "Asia/Amman");
        db.Teachers.Add(teacher);
        await db.SaveChangesAsync();
        return teacher;
    }

    private static async Task EnsureUserAsync(UserManager<ApplicationUser> userManager, string email, string role)
    {
        var existing = await userManager.FindByEmailAsync(email);
        if (existing is not null)
        {
            return;
        }

        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
        var result = await userManager.CreateAsync(user, Password);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));
        await userManager.AddToRoleAsync(user, role);
    }

    /// <summary>A cookie-persisting client, not yet authenticated.</summary>
    private HttpClient CreateClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static readonly Regex AntiforgeryTokenPattern = new(
        "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");

    private async Task<HttpClient> CreateAuthenticatedClientAsync(string email)
    {
        var client = CreateClient();
        var loginPage = await client.GetStringAsync("/Account/Login");
        var token = AntiforgeryTokenPattern.Match(loginPage).Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token), "Could not find the antiforgery token on the login page.");

        var form = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Input.Email"] = email,
            ["Input.Password"] = Password,
        };
        var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode); // proves the login itself actually succeeded

        return client;
    }

    public static IEnumerable<object[]> AdminOnlyPages() => new[]
    {
        new object[] { "/Admin/Dashboard" },
        new object[] { "/Admin/Students" },
        new object[] { "/Admin/Teachers" },
        new object[] { "/Admin/Schedules" },
        new object[] { "/Admin/Subscriptions" },
        new object[] { "/Admin/MakeUpCredits" },
        new object[] { "/Admin/Payments" },
        new object[] { "/Admin/Payroll" },
        new object[] { "/Admin/Certificates" },
        new object[] { "/Admin/FinancialReport" },
    };

    [Theory]
    [MemberData(nameof(AdminOnlyPages))]
    public async Task Unauthenticated_request_is_redirected_not_shown_the_page(string path)
    {
        var client = CreateClient();
        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location?.ToString() ?? string.Empty);
    }

    [Theory]
    [MemberData(nameof(AdminOnlyPages))]
    public async Task Authenticated_wrong_role_is_turned_away_not_shown_the_page(string path)
    {
        var client = await CreateAuthenticatedClientAsync(TeacherEmail);
        var response = await client.GetAsync(path);

        // A logged-in Teacher is a real, valid account — but not Admin/SystemAdmin.
        // [Authorize(Roles=...)] must still turn them away from admin-only data.
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(AdminOnlyPages))]
    public async Task Authenticated_guardian_is_turned_away_not_shown_the_page(string path)
    {
        var client = await CreateAuthenticatedClientAsync(GuardianEmail);
        var response = await client.GetAsync(path);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(AdminOnlyPages))]
    public async Task Authenticated_admin_is_shown_the_page(string path)
    {
        var client = await CreateAuthenticatedClientAsync(AdminEmail);
        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // /Admin/StudentDetails/{id} takes a route parameter, so it can't share
    // AdminOnlyPages' "just assert 200 for admin" theory — that assertion
    // needs a real student id to exist. Role-gating is exercised the same
    // way as every other admin page; the data-dependent cases get their own tests.
    [Fact]
    public async Task Unauthenticated_request_to_student_details_is_redirected()
    {
        var client = CreateClient();
        var response = await client.GetAsync("/Admin/StudentDetails/1");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location?.ToString() ?? string.Empty);
    }

    [Fact]
    public async Task Authenticated_wrong_role_is_turned_away_from_student_details()
    {
        var client = await CreateAuthenticatedClientAsync(TeacherEmail);
        var response = await client.GetAsync("/Admin/StudentDetails/1");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Admin_viewing_a_nonexistent_student_gets_a_real_404_not_a_crash()
    {
        var client = await CreateAuthenticatedClientAsync(AdminEmail);
        var response = await client.GetAsync("/Admin/StudentDetails/999999999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Admin_viewing_a_real_student_sees_their_details()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();

        var countryId = 90_000_002;
        if (!await db.Countries.AnyAsync(c => c.Id == countryId))
        {
            db.Countries.Add(new Country(countryId, "ZX", "دولة", "Country", "JOD", "+962", "Asia/Amman"));
            await db.SaveChangesAsync();
        }

        var student = new Student(countryId, "Detail Test Student", new LocalDate(2012, 1, 1));
        db.Students.Add(student);
        await db.SaveChangesAsync();

        var client = await CreateAuthenticatedClientAsync(AdminEmail);
        var response = await client.GetAsync($"/Admin/StudentDetails/{student.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Detail Test Student", body);
    }

    [Fact]
    public async Task Unauthenticated_request_to_the_teacher_portal_is_redirected()
    {
        var client = CreateClient();
        var response = await client.GetAsync("/Teacher/MySessions");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location?.ToString() ?? string.Empty);
    }

    [Fact]
    public async Task Authenticated_admin_is_turned_away_from_the_teacher_portal()
    {
        // [Authorize(Roles = Teacher)] only — being Admin does not imply Teacher;
        // this is the one page where the admin roles are themselves the wrong role.
        var client = await CreateAuthenticatedClientAsync(AdminEmail);
        var response = await client.GetAsync("/Teacher/MySessions");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_guardian_is_turned_away_from_the_teacher_portal()
    {
        var client = await CreateAuthenticatedClientAsync(GuardianEmail);
        var response = await client.GetAsync("/Teacher/MySessions");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_teacher_is_shown_the_teacher_portal()
    {
        var client = await CreateAuthenticatedClientAsync(TeacherEmail);
        var response = await client.GetAsync("/Teacher/MySessions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_request_to_the_guardian_portal_is_redirected()
    {
        var client = CreateClient();
        var response = await client.GetAsync("/Guardian/MyChildren");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location?.ToString() ?? string.Empty);
    }

    [Fact]
    public async Task Authenticated_admin_is_turned_away_from_the_guardian_portal()
    {
        var client = await CreateAuthenticatedClientAsync(AdminEmail);
        var response = await client.GetAsync("/Guardian/MyChildren");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_teacher_is_turned_away_from_the_guardian_portal()
    {
        var client = await CreateAuthenticatedClientAsync(TeacherEmail);
        var response = await client.GetAsync("/Guardian/MyChildren");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_guardian_is_shown_the_guardian_portal()
    {
        var client = await CreateAuthenticatedClientAsync(GuardianEmail);
        var response = await client.GetAsync("/Guardian/MyChildren");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Regression test for a real IDOR found by a security review of
    /// this session's own new code: OnPostDeclareAsync took a bare sessionId
    /// with no check that it belonged to the calling teacher. Proves a Teacher
    /// account cannot declare delivery on a session that belongs to a
    /// DIFFERENT teacher.</summary>
    [Fact]
    public async Task A_teacher_cannot_declare_delivery_on_another_teachers_session()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        // The attacker (TeacherEmail) needs their OWN linked Teacher profile
        // too — otherwise the request is blocked one step earlier by "you have
        // no teacher profile at all" instead of exercising the actual
        // cross-teacher ownership check this test targets.
        var attackerUser = await userManager.FindByEmailAsync(TeacherEmail);
        await EnsureLinkedTeacherAsync(db, attackerUser!.Id, "Attacker Teacher");

        await EnsureUserAsync(userManager, OtherTeacherEmail, RoleNames.Teacher);
        var otherTeacherUser = await userManager.FindByEmailAsync(OtherTeacherEmail);
        var otherTeacher = await EnsureLinkedTeacherAsync(db, otherTeacherUser!.Id, "Other Teacher");

        var countryId = 90_000_001;
        if (!await db.Countries.AnyAsync(c => c.Id == countryId))
        {
            db.Countries.Add(new Country(countryId, "ZY", "دولة", "Country", "JOD", "+962", "Asia/Amman"));
            await db.SaveChangesAsync();
        }

        if (!await db.Courses.AnyAsync(c => c.Code == "AUTHTEST-COURSE"))
        {
            db.Courses.Add(new Course("AUTHTEST-COURSE", "دورة", "Course"));
            await db.SaveChangesAsync();
        }
        var courseId = await db.Courses.Where(c => c.Code == "AUTHTEST-COURSE").Select(c => c.Id).FirstAsync();

        var levelId = 90_000_001;
        if (!await db.Levels.AnyAsync(l => l.Id == levelId))
        {
            db.Levels.Add(new Level(levelId, "AUTHTEST-LVL", "مستوى", "Level", levelId));
            await db.SaveChangesAsync();
        }

        var ageGroupId = 90_000_001;
        if (!await db.AgeGroups.AnyAsync(a => a.Id == ageGroupId))
        {
            db.AgeGroups.Add(new AgeGroup(ageGroupId, "AUTHTEST-AGE", 5, 12, true));
            await db.SaveChangesAsync();
        }

        var now = SystemClock.Instance.GetCurrentInstant();
        var otherTeachersSession = new ClassSession(countryId, null, courseId, levelId, ageGroupId, otherTeacher.Id,
            now.Minus(Duration.FromHours(2)), now.Minus(Duration.FromHours(1)), "Asia/Amman", "10:00",
            SessionType.Group, capacity: 4, createdAtUtc: now);
        db.ClassSessions.Add(otherTeachersSession);
        await db.SaveChangesAsync();

        var attackerClient = await CreateAuthenticatedClientAsync(TeacherEmail);
        var declarePage = await attackerClient.GetStringAsync("/Teacher/MySessions");
        var token = AntiforgeryTokenPattern.Match(declarePage).Groups[1].Value;

        var form = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["sessionId"] = otherTeachersSession.Id.ToString(),
            ["declaredMinutes"] = "60",
        };
        var response = await attackerClient.PostAsync("/Teacher/MySessions?handler=Declare", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Session not found", body);

        // The real assertion: the OTHER teacher's session must be untouched —
        // no SessionDelivery row created by the attacker at all.
        var delivery = await db.SessionDeliveries.FirstOrDefaultAsync(d => d.SessionId == otherTeachersSession.Id);
        Assert.Null(delivery);
    }

    /// <summary>Hosts the real app. The Development environment suppresses the
    /// UseExceptionHandler/UseHsts branch (Program.cs's `if (!IsDevelopment())`)
    /// that would otherwise obscure a real error behind a generic error page
    /// during these tests.</summary>
    public class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseEnvironment("Development");
    }
}
