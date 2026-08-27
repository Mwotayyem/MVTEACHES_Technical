using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MVTeaches.Infrastructure.Identity;
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
    private const string GuardianEmail = "authtest-guardian@test.mvteaches.local";

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
