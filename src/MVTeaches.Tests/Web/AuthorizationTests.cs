using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Common;
using MVTeaches.Domain.Delivery;
using MVTeaches.Domain.Payroll;
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
        await EnsureUserAsync(userManager, SystemAdminEmail, RoleNames.SystemAdmin);
        await EnsureUserAsync(userManager, TeacherEmail, RoleNames.Teacher);
        await EnsureUserAsync(userManager, GuardianEmail, RoleNames.Guardian);
        await EnsureUserAsync(userManager, StudentEmail, RoleNames.Student);

        // Security review 2026-09-02/2026-09-03 (Stage 1 + Stage 2D admin
        // permissions): this shared AdminEmail account represents "a plain
        // Admin" across dozens of unrelated tests in this file (AdminOnlyPages()
        // alone lists 12 pages, plus the dedicated StudentDetails GET tests
        // below). Its own AuthorizationTests never exercise Payments/Payroll/
        // Subscriptions/Students/Teachers/Schedules/Compensation/
        // PlacementTests/Certificates/FinancialReport/Dashboard mutation, only
        // that the pages load — so it only needs the View permissions to keep
        // meaning what it always meant here ("a plain Admin can reach admin
        // pages"), not every key. Idempotent: checks existing claims first,
        // since InitializeAsync can run again against this same shared test DB.
        var adminUser = await userManager.FindByEmailAsync(AdminEmail);
        if (adminUser is not null)
        {
            var existingKeys = (await userManager.GetClaimsAsync(adminUser)).Select(c => c.Value).ToHashSet();
            foreach (var key in new[]
            {
                PermissionKeys.PaymentsView, PermissionKeys.PayrollView, PermissionKeys.SubscriptionsView,
                PermissionKeys.StudentsView, PermissionKeys.TeachersView, PermissionKeys.SchedulesView,
                PermissionKeys.CompensationView, PermissionKeys.PlacementTestsView, PermissionKeys.CertificatesView,
                PermissionKeys.DashboardView, PermissionKeys.FinancialReportView,
            })
            {
                if (!existingKeys.Contains(key))
                {
                    await userManager.AddClaimAsync(adminUser, new Claim(PermissionKeys.ClaimType, key));
                }
            }
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private const string Password = "CorrectHorse123!";
    private const string AdminEmail = "authtest-admin@test.mvteaches.local";
    private const string SystemAdminEmail = "authtest-systemadmin@test.mvteaches.local";
    private const string TeacherEmail = "authtest-teacher@test.mvteaches.local";
    private const string OtherTeacherEmail = "authtest-teacher2@test.mvteaches.local";
    private const string GuardianEmail = "authtest-guardian@test.mvteaches.local";
    private const string StudentEmail = "authtest-student@test.mvteaches.local";

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
        new object[] { "/Admin/RescheduleSessions" },
        new object[] { "/Admin/CompensationRequests" },
        new object[] { "/Admin/Payments" },
        new object[] { "/Admin/Payroll" },
        new object[] { "/Admin/Certificates" },
        new object[] { "/Admin/FinancialReport" },
        new object[] { "/Admin/PlacementTests" },
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
    public async Task Unauthenticated_request_to_the_pay_history_page_is_redirected()
    {
        var client = CreateClient();
        var response = await client.GetAsync("/Teacher/MyPayHistory");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location?.ToString() ?? string.Empty);
    }

    [Fact]
    public async Task Authenticated_admin_is_turned_away_from_the_pay_history_page()
    {
        var client = await CreateAuthenticatedClientAsync(AdminEmail);
        var response = await client.GetAsync("/Teacher/MyPayHistory");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_guardian_is_turned_away_from_the_pay_history_page()
    {
        var client = await CreateAuthenticatedClientAsync(GuardianEmail);
        var response = await client.GetAsync("/Teacher/MyPayHistory");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_teacher_is_shown_the_pay_history_page()
    {
        var client = await CreateAuthenticatedClientAsync(TeacherEmail);
        var response = await client.GetAsync("/Teacher/MyPayHistory");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>The one thing that actually matters on this read-only report:
    /// a teacher must see their own earnings only. Seeds a distinctive-amount
    /// PayrollLine for a DIFFERENT teacher and proves it never appears on the
    /// calling teacher's own page, while their own line does.</summary>
    [Fact]
    public async Task A_teacher_only_sees_their_own_payroll_lines_not_another_teachers()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var ownUser = await userManager.FindByEmailAsync(TeacherEmail);
        var ownTeacher = await EnsureLinkedTeacherAsync(db, ownUser!.Id, "Pay History Owner");

        await EnsureUserAsync(userManager, OtherTeacherEmail, RoleNames.Teacher);
        var otherUser = await userManager.FindByEmailAsync(OtherTeacherEmail);
        var otherTeacher = await EnsureLinkedTeacherAsync(db, otherUser!.Id, "Pay History Stranger");

        var countryId = 90_000_003;
        if (!await db.Countries.AnyAsync(c => c.Id == countryId))
        {
            db.Countries.Add(new Country(countryId, "ZW", "دولة", "Country", "JOD", "+962", "Asia/Amman"));
            await db.SaveChangesAsync();
        }

        if (!await db.Courses.AnyAsync(c => c.Code == "PAYHIST-COURSE"))
        {
            db.Courses.Add(new Course("PAYHIST-COURSE", "دورة", "Course"));
            await db.SaveChangesAsync();
        }
        var courseId = await db.Courses.Where(c => c.Code == "PAYHIST-COURSE").Select(c => c.Id).FirstAsync();

        var levelId = 90_000_002;
        if (!await db.Levels.AnyAsync(l => l.Id == levelId))
        {
            db.Levels.Add(new Level(levelId, "PAYHIST-LVL", "مستوى", "Level", levelId));
            await db.SaveChangesAsync();
        }

        var ageGroupId = 90_000_002;
        if (!await db.AgeGroups.AnyAsync(a => a.Id == ageGroupId))
        {
            db.AgeGroups.Add(new AgeGroup(ageGroupId, "PAYHIST-AGE", 5, 12, true));
            await db.SaveChangesAsync();
        }

        // Distinct from A_teacher_cannot_declare_delivery_on_another_teachers_session's
        // own time window for this SAME (idempotently reused) otherTeacher row —
        // no_teacher_overlap is a real EXCLUDE constraint, so two tests scheduling
        // the same teacher across overlapping times collide for real.
        var now = SystemClock.Instance.GetCurrentInstant();
        var ownSession = new ClassSession(countryId, null, courseId, levelId, ageGroupId, ownTeacher.Id,
            now.Minus(Duration.FromHours(54)), now.Minus(Duration.FromHours(53)), "Asia/Amman", "10:00",
            SessionType.Group, createdAtUtc: now);
        var otherSession = new ClassSession(countryId, null, courseId, levelId, ageGroupId, otherTeacher.Id,
            now.Minus(Duration.FromHours(52)), now.Minus(Duration.FromHours(51)), "Asia/Amman", "12:00",
            SessionType.Group, createdAtUtc: now);
        db.ClassSessions.AddRange(ownSession, otherSession);
        await db.SaveChangesAsync();

        var period = new PayrollPeriod(countryId, new LocalDate(2020, 1, 1), new LocalDate(2020, 1, 31));
        db.PayrollPeriods.Add(period);
        await db.SaveChangesAsync();

        db.PayrollLines.Add(new PayrollLine(period.Id, ownTeacher.Id, ownSession.Id, 60, 12.34m, "JOD", 12.34m));
        // A distinctive amount that must NEVER show up on the owning teacher's page.
        db.PayrollLines.Add(new PayrollLine(period.Id, otherTeacher.Id, otherSession.Id, 60, 99.99m, "JOD", 99.99m));
        await db.SaveChangesAsync();

        var client = await CreateAuthenticatedClientAsync(TeacherEmail);
        var response = await client.GetAsync("/Teacher/MyPayHistory");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("12.34", body);
        Assert.DoesNotContain("99.99", body);
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

    [Fact]
    public async Task Unauthenticated_request_to_the_student_portal_is_redirected()
    {
        var client = CreateClient();
        var response = await client.GetAsync("/Student/MySessions");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location?.ToString() ?? string.Empty);
    }

    [Fact]
    public async Task Authenticated_admin_is_turned_away_from_the_student_portal()
    {
        var client = await CreateAuthenticatedClientAsync(AdminEmail);
        var response = await client.GetAsync("/Student/MySessions");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_guardian_is_turned_away_from_the_student_portal()
    {
        var client = await CreateAuthenticatedClientAsync(GuardianEmail);
        var response = await client.GetAsync("/Student/MySessions");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_student_is_shown_the_student_portal()
    {
        var client = await CreateAuthenticatedClientAsync(StudentEmail);
        var response = await client.GetAsync("/Student/MySessions");

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
            SessionType.Group, createdAtUtc: now);
        db.ClassSessions.Add(otherTeachersSession);
        await db.SaveChangesAsync();

        // ?culture=en pins the assertion below to a known language — the
        // page is now fully localized and defaults to ar-JO.
        var attackerClient = await CreateAuthenticatedClientAsync(TeacherEmail);
        var declarePage = await attackerClient.GetStringAsync("/Teacher/MySessions?culture=en");
        var token = AntiforgeryTokenPattern.Match(declarePage).Groups[1].Value;

        var form = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["sessionId"] = otherTeachersSession.Id.ToString(),
            ["declaredMinutes"] = "60",
        };
        var response = await attackerClient.PostAsync("/Teacher/MySessions?handler=Declare&culture=en", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Session not found", body);

        // The real assertion: the OTHER teacher's session must be untouched —
        // no SessionDelivery row created by the attacker at all.
        var delivery = await db.SessionDeliveries.FirstOrDefaultAsync(d => d.SessionId == otherTeachersSession.Id);
        Assert.Null(delivery);
    }

    /// <summary>Release-readiness audit finding: AdminOnlyDashboardAuthorizationFilter
    /// originally checked only the literal "Admin" role — a SystemAdmin-only
    /// account (RoleNames.cs's own doc comment: "elevated over Admin") could
    /// not reach /hangfire at all, unlike every other admin surface in this
    /// app. No test exercised this filter before, which is exactly how the
    /// gap went unnoticed.</summary>
    [Fact]
    public async Task Unauthenticated_request_to_the_hangfire_dashboard_is_rejected()
    {
        var client = CreateClient();
        var response = await client.GetAsync("/hangfire");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_teacher_is_rejected_from_the_hangfire_dashboard()
    {
        var client = await CreateAuthenticatedClientAsync(TeacherEmail);
        var response = await client.GetAsync("/hangfire");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_admin_can_reach_the_hangfire_dashboard()
    {
        var client = await CreateAuthenticatedClientAsync(AdminEmail);
        var response = await client.GetAsync("/hangfire");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_systemadmin_can_reach_the_hangfire_dashboard()
    {
        var client = await CreateAuthenticatedClientAsync(SystemAdminEmail);
        var response = await client.GetAsync("/hangfire");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- Teacher slot publishing (owner decision 2026-08-30 rule 7) ----

    [Fact]
    public async Task Unauthenticated_request_to_the_publish_slots_page_is_redirected()
    {
        var client = CreateClient();
        var response = await client.GetAsync("/Teacher/PublishSlots");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location?.ToString() ?? string.Empty);
    }

    [Fact]
    public async Task Authenticated_admin_is_turned_away_from_the_publish_slots_page()
    {
        var client = await CreateAuthenticatedClientAsync(AdminEmail);
        var response = await client.GetAsync("/Teacher/PublishSlots");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_guardian_is_turned_away_from_the_publish_slots_page()
    {
        var client = await CreateAuthenticatedClientAsync(GuardianEmail);
        var response = await client.GetAsync("/Teacher/PublishSlots");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_teacher_is_shown_the_publish_slots_page()
    {
        var client = await CreateAuthenticatedClientAsync(TeacherEmail);
        var response = await client.GetAsync("/Teacher/PublishSlots");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>End-to-end through the real page, not just the service:
    /// only levels TeacherLevelAssignment actually grants this teacher show
    /// up in the "Level" dropdown at all — an authorized level the teacher
    /// does NOT hold must never even be offered as a choice.</summary>
    [Fact]
    public async Task The_publish_slots_page_only_offers_levels_the_teacher_is_actually_authorized_for()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var teacherUser = await userManager.FindByEmailAsync(TeacherEmail);
        var teacher = await EnsureLinkedTeacherAsync(db, teacherUser!.Id, "Slot Publisher");

        // A range clear of both this class's own 90_000_00x literals AND every
        // NextId()-walked test class sharing this database (the highest base
        // any of them currently starts from is 96_000_000) — an id collision
        // here would silently leave this test's own Level row unwritten
        // (the "if not exists" seed below would find someone ELSE'S level
        // already occupying that id and skip creating this one), exactly the
        // class of bug this session already root-caused once for country
        // codes.
        var grantedLevelId = 97_000_010;
        var ungrantedLevelId = 97_000_011;
        if (!await db.Levels.AnyAsync(l => l.Id == grantedLevelId))
        {
            db.Levels.Add(new Level(grantedLevelId, "PUBSLOT-GRANTED", "مستوى", "Level", grantedLevelId));
        }
        if (!await db.Levels.AnyAsync(l => l.Id == ungrantedLevelId))
        {
            db.Levels.Add(new Level(ungrantedLevelId, "PUBSLOT-UNGRANTED", "مستوى", "Level", ungrantedLevelId));
        }
        await db.SaveChangesAsync();

        if (!await db.TeacherLevelAssignments.AnyAsync(a => a.TeacherId == teacher.Id && a.LevelId == grantedLevelId))
        {
            db.TeacherLevelAssignments.Add(new TeacherLevelAssignment(teacher.Id, grantedLevelId, teacherUser.Id, SystemClock.Instance.GetCurrentInstant()));
            await db.SaveChangesAsync();
        }

        var client = await CreateAuthenticatedClientAsync(TeacherEmail);
        var body = await client.GetStringAsync("/Teacher/PublishSlots");

        Assert.Contains("PUBSLOT-GRANTED", body);
        Assert.DoesNotContain("PUBSLOT-UNGRANTED", body);
    }

    // ---- Placement test (owner decision 2026-08-30, reversing D-48) ----

    [Fact]
    public async Task Unauthenticated_request_to_the_placement_test_page_is_redirected()
    {
        var client = CreateClient();
        var response = await client.GetAsync("/PlacementTest");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location?.ToString() ?? string.Empty);
    }

    [Fact]
    public async Task Authenticated_admin_is_turned_away_from_the_placement_test_page()
    {
        var client = await CreateAuthenticatedClientAsync(AdminEmail);
        var response = await client.GetAsync("/PlacementTest");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_student_is_shown_the_placement_test_page()
    {
        var client = await CreateAuthenticatedClientAsync(StudentEmail);
        var response = await client.GetAsync("/PlacementTest");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_guardian_is_shown_the_placement_test_page()
    {
        var client = await CreateAuthenticatedClientAsync(GuardianEmail);
        var response = await client.GetAsync("/PlacementTest");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Rule 3/IDOR: a guardian must never be able to act on a child
    /// that is not actually theirs by tampering the studentId form value —
    /// IPlacementAttemptService's own IsAuthorizedAsync check is the real
    /// guard; this proves the page surfaces its refusal rather than the
    /// child's data.</summary>
    [Fact]
    public async Task A_guardian_cannot_start_a_placement_attempt_for_a_child_that_is_not_theirs()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();

        // Reuses this file's own already-established country (see
        // A_teacher_cannot_declare_delivery_on_another_teachers_session)
        // rather than adding a new hand-picked 2-letter code — a fresh
        // literal code risks exactly the cross-test-class collision this
        // session already root-caused once for TwoLetterCode-generated
        // codes (IX_countries_code), and this id/code pair is already
        // proven collision-free across this file's own repeated runs.
        var countryId = 90_000_001;
        if (!await db.Countries.AnyAsync(c => c.Id == countryId))
        {
            db.Countries.Add(new Country(countryId, "ZY", "دولة", "Country", "JOD", "+962", "Asia/Amman"));
            await db.SaveChangesAsync();
        }

        // A real student that belongs to nobody the acting guardian is linked to.
        var strangerChild = new Student(countryId, "Someone Else's Child", new LocalDate(2013, 1, 1));
        db.Students.Add(strangerChild);
        await db.SaveChangesAsync();

        // ?culture=en pins the assertion below to a known language — the
        // page is now fully localized and defaults to ar-JO.
        var client = await CreateAuthenticatedClientAsync(GuardianEmail);
        var page = await client.GetStringAsync("/PlacementTest?culture=en");
        var token = AntiforgeryTokenPattern.Match(page).Groups[1].Value;

        var form = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["studentId"] = strangerChild.Id.ToString(),
        };
        var response = await client.PostAsync("/PlacementTest?handler=Start&culture=en", new FormUrlEncodedContent(form));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Not authorized", body);

        // The real assertion: no attempt was ever created for the stranger's child.
        Assert.False(await db.PlacementAttempts.AnyAsync(a => a.StudentId == strangerChild.Id));
    }

    // ---- Purchase package (owner decision 2026-08-30 rules 1/4) ----

    [Fact]
    public async Task Unauthenticated_request_to_the_purchase_package_page_is_redirected()
    {
        var client = CreateClient();
        var response = await client.GetAsync("/PurchasePackage");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location?.ToString() ?? string.Empty);
    }

    [Fact]
    public async Task Authenticated_admin_is_turned_away_from_the_purchase_package_page()
    {
        var client = await CreateAuthenticatedClientAsync(AdminEmail);
        var response = await client.GetAsync("/PurchasePackage");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_student_is_shown_the_purchase_package_page()
    {
        var client = await CreateAuthenticatedClientAsync(StudentEmail);
        var response = await client.GetAsync("/PurchasePackage");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Rule 1: "Until a placement result exists, the student must
    /// not purchase a package — show a clear CTA to take the free test."
    /// This proves the CTA appears and no plan list is ever offered to a
    /// student with no assigned level, and — the actual guarantee — that a
    /// direct POST to Purchase is refused server-side regardless of the UI.</summary>
    [Fact]
    public async Task A_student_with_no_placement_result_is_shown_the_test_cta_and_cannot_purchase()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        const string email = "authtest-nolevel-student@test.mvteaches.local";
        await EnsureUserAsync(userManager, email, RoleNames.Student);
        var user = await userManager.FindByEmailAsync(email);
        if (!await db.Students.AnyAsync(s => s.UserId == user!.Id))
        {
            db.Students.Add(new Student(90_000_001, "No Level Student", new LocalDate(2013, 1, 1), user!.Id));
            await db.SaveChangesAsync();
        }

        // A real, published plan — otherwise PurchaseFromPlanAsync's own
        // earlier PlanNotFound check would fire first and this test would
        // never actually reach the StudentHasNoAssignedLevel check it means
        // to exercise.
        if (!await db.Courses.AnyAsync(c => c.Code == "NOLEVEL-COURSE"))
        {
            db.Courses.Add(new Course("NOLEVEL-COURSE", "دورة", "Course"));
            await db.SaveChangesAsync();
        }
        var courseId = await db.Courses.Where(c => c.Code == "NOLEVEL-COURSE").Select(c => c.Id).FirstAsync();
        var levelId = 90_000_004;
        if (!await db.Levels.AnyAsync(l => l.Id == levelId))
        {
            db.Levels.Add(new Level(levelId, "NOLEVEL-LVL", "مستوى", "Level", levelId));
            await db.SaveChangesAsync();
        }
        var plan = new PricingPlan(90_000_001, courseId, levelId, null, SessionType.Group, 10, 600,
            new Money(50m, "JOD"), 90, new LocalDate(2026, 1, 1), 1);
        db.PricingPlans.Add(plan);
        await db.SaveChangesAsync();

        // ?culture=en pins the assertions below to a known language — the
        // page is now fully localized (Section 7) and defaults to ar-JO,
        // same convention as LocalizationAndShellTests.
        var client = await CreateAuthenticatedClientAsync(email);
        var page = await client.GetStringAsync("/PurchasePackage?culture=en");
        Assert.Contains("placement test is required", page);

        // Even a direct, form-tampered POST must be refused — the CTA is a
        // convenience, PurchaseFromPlanAsync's own check is the real guard.
        var token = AntiforgeryTokenPattern.Match(page).Groups[1].Value;
        var student = await db.Students.FirstAsync(s => s.UserId == user!.Id);
        var form = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["studentId"] = student.Id.ToString(),
            ["pricingPlanId"] = plan.Id.ToString(),
        };
        var response = await client.PostAsync("/PurchasePackage?handler=Purchase&culture=en", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("placement result is required", body);
        Assert.False(await db.Subscriptions.AnyAsync(s => s.StudentId == student.Id));
    }

    /// <summary>Section 7's own explicit requirement: "verify language
    /// switching doesn't break price/date entry or decimal interpretation."
    /// .NET's ar-JO culture renders a plain `decimal.ToString("N3", null)`
    /// with Arabic-Indic separators (٬/٫) — a real, reproduced bug this
    /// session found and fixed by switching every money-formatting call site
    /// to CultureInfo.InvariantCulture explicitly. This proves a price still
    /// renders with an ASCII "." decimal point under the Arabic UI, so a
    /// payer reading "50.000 JOD" never has to interpret a locale-specific
    /// separator for money.</summary>
    [Fact]
    public async Task A_price_still_renders_with_an_ascii_decimal_point_under_the_arabic_culture()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        const string email = "authtest-priceformat-student@test.mvteaches.local";
        await EnsureUserAsync(userManager, email, RoleNames.Student);
        var user = await userManager.FindByEmailAsync(email);
        if (!await db.Students.AnyAsync(s => s.UserId == user!.Id))
        {
            db.Students.Add(new Student(90_000_002, "Price Format Student", new LocalDate(2013, 1, 1), user!.Id));
            await db.SaveChangesAsync();
        }

        var student = await db.Students.FirstAsync(s => s.UserId == user!.Id);

        if (!await db.Courses.AnyAsync(c => c.Code == "PRICEFMT-COURSE"))
        {
            db.Courses.Add(new Course("PRICEFMT-COURSE", "دورة", "Course"));
            await db.SaveChangesAsync();
        }
        var courseId = await db.Courses.Where(c => c.Code == "PRICEFMT-COURSE").Select(c => c.Id).FirstAsync();
        var levelId = 90_000_005;
        if (!await db.Levels.AnyAsync(l => l.Id == levelId))
        {
            db.Levels.Add(new Level(levelId, "PRICEFMT-LVL", "مستوى", "Level", levelId));
            await db.SaveChangesAsync();
        }
        if (!await db.StudentLevels.AnyAsync(l => l.StudentId == student.Id && l.IsCurrent))
        {
            db.StudentLevels.Add(new MVTeaches.Domain.Placement.StudentLevel(student.Id, levelId, user!.Id,
                MVTeaches.Domain.Placement.AssignedByRole.Admin, MVTeaches.Domain.Placement.LevelAssignmentSource.AdminOverride,
                null, "seed", NodaTime.SystemClock.Instance.GetCurrentInstant()));
            await db.SaveChangesAsync();
        }
        if (!await db.PricingPlans.AnyAsync(p => p.CourseId == courseId && p.LevelId == levelId))
        {
            db.PricingPlans.Add(new PricingPlan(90_000_002, courseId, levelId, null, SessionType.Group, 10, 600,
                new Money(1234.567m, "JOD"), 90, new LocalDate(2026, 1, 1), 1));
            await db.SaveChangesAsync();
        }

        var client = await CreateAuthenticatedClientAsync(email);
        var page = await client.GetStringAsync("/PurchasePackage?culture=ar-JO");

        Assert.Contains("1,234.567 JOD", page);
        Assert.DoesNotContain("1234٫567", page);
        Assert.DoesNotContain("1٬234", page);
    }

    // ---- Video-meeting connections (owner clarification 2026-08-29) ----

    [Fact]
    public async Task Unauthenticated_request_to_the_teacher_connections_page_is_redirected()
    {
        var client = CreateClient();
        var response = await client.GetAsync("/Teacher/Connections");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location?.ToString() ?? string.Empty);
    }

    [Fact]
    public async Task Authenticated_admin_cannot_reach_a_teachers_connections_page()
    {
        // Admins never see or manage a teacher's provider credentials — the
        // connection belongs to the teacher's own account, and nothing in
        // this app should hand an admin a route to it.
        var client = await CreateAuthenticatedClientAsync(AdminEmail);
        var response = await client.GetAsync("/Teacher/Connections");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_student_cannot_reach_the_teacher_connections_page()
    {
        var client = await CreateAuthenticatedClientAsync(StudentEmail);
        var response = await client.GetAsync("/Teacher/Connections");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_teacher_can_reach_their_own_connections_page()
    {
        var client = await CreateAuthenticatedClientAsync(TeacherEmail);
        var response = await client.GetAsync("/Teacher/Connections");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task An_unauthenticated_oauth_callback_cannot_complete_a_connection()
    {
        // The callback is teacher-authenticated on purpose: it is the
        // "initiating browser session" half of the OAuth state binding.
        var client = CreateClient();
        var response = await client.GetAsync("/oauth/zoom/callback?code=stolen&state=stolen");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location?.ToString() ?? string.Empty);
    }

    [Fact]
    public async Task A_non_teacher_cannot_complete_an_oauth_callback()
    {
        var client = await CreateAuthenticatedClientAsync(AdminEmail);
        var response = await client.GetAsync("/oauth/google/callback?code=stolen&state=stolen");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_zoom_webhook_rejects_an_unsigned_request()
    {
        // Zoom is unconfigured in the test host, so the endpoint must not
        // exist as far as any caller is concerned — and must certainly never
        // return a success that implies the payload was trusted.
        var client = CreateClient();
        var response = await client.PostAsync("/webhooks/zoom",
            new StringContent("{\"event\":\"meeting.deleted\"}", System.Text.Encoding.UTF8, "application/json"));

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
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
