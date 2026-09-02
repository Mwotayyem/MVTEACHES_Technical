using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MVTeaches.Infrastructure.Identity;
using Xunit;

namespace MVTeaches.Tests.Web;

/// <summary>
/// Security review 2026-09-02 (Review Required — Auth): closes the gap that
/// blocked handing a real admin account over to the platform owner — until
/// this page existed, NOTHING in the app let any signed-in account (Admin
/// included) change its own password after signing in with a temporary one.
/// A real ASP.NET Core host (WebApplicationFactory, reusing AuthorizationTests'
/// Factory), exercising ChangePasswordAsync end-to-end via real HTTP requests
/// against the real DB — nothing mocked.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class ChangePasswordTests : IClassFixture<AuthorizationTests.Factory>, IAsyncLifetime
{
    private readonly AuthorizationTests.Factory _factory;
    private const string Password = "CorrectHorse123!";
    private const string NewPassword = "NewCorrectHorse456!";

    public ChangePasswordTests(TestDatabaseFixture fixture, AuthorizationTests.Factory factory)
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__MvTeaches", fixture.ConnectionString);
        _factory = factory;
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly Regex AntiforgeryTokenPattern = new(
        "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");

    private HttpClient CreateClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private async Task<string> CreateUserAsync(string label, string role)
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        if (!await roleManager.RoleExistsAsync(role))
        {
            await roleManager.CreateAsync(new ApplicationRole(role));
        }

        var email = $"changepwtest-{label}-{Guid.NewGuid():N}@test.mvteaches.local";
        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
        var result = await userManager.CreateAsync(user, Password);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));
        await userManager.AddToRoleAsync(user, role);
        return email;
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path);
        var token = AntiforgeryTokenPattern.Match(html).Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token), $"Could not find the antiforgery token on {path}.");
        return token;
    }

    private static async Task<HttpResponseMessage> LoginAsync(HttpClient client, string email, string password)
    {
        var token = await GetAntiforgeryTokenAsync(client, "/Account/Login");
        return await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Input.Email"] = email,
            ["Input.Password"] = password,
        }));
    }

    [Fact]
    public async Task Unauthenticated_request_is_redirected_to_login()
    {
        var client = CreateClient();
        var response = await client.GetAsync("/Account/ChangePassword");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location?.ToString() ?? string.Empty);
    }

    [Fact]
    public async Task An_admin_changes_their_own_password_and_stays_signed_in()
    {
        var email = await CreateUserAsync("admin", RoleNames.Admin);
        var client = CreateClient();
        var loginResponse = await LoginAsync(client, email, Password);
        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);

        var pageToken = await GetAntiforgeryTokenAsync(client, "/Account/ChangePassword?culture=en");
        var changeResponse = await client.PostAsync("/Account/ChangePassword?culture=en", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = pageToken,
            ["Input.CurrentPassword"] = Password,
            ["Input.NewPassword"] = NewPassword,
            ["Input.ConfirmNewPassword"] = NewPassword,
        }));

        Assert.Equal(HttpStatusCode.OK, changeResponse.StatusCode); // Page() re-render, not a redirect
        var body = await changeResponse.Content.ReadAsStringAsync();
        Assert.Contains("Your password has been changed.", body);

        // Requirement: the user stays inside their own account right after
        // success — proven by a follow-up authenticated request succeeding
        // with the SAME client/cookie, not by redirecting to login again.
        var stillSignedIn = await client.GetAsync("/Account/ChangePassword");
        Assert.Equal(HttpStatusCode.OK, stillSignedIn.StatusCode);

        // And the change is real: the OLD password no longer works, the NEW one does.
        var freshClient = CreateClient();
        var oldPasswordAttempt = await LoginAsync(freshClient, email, Password);
        Assert.Equal(HttpStatusCode.OK, oldPasswordAttempt.StatusCode); // re-rendered login page with an error, not a redirect
        Assert.DoesNotContain("/Account/", oldPasswordAttempt.Headers.Location?.ToString() ?? string.Empty);

        var newPasswordAttempt = await LoginAsync(freshClient, email, NewPassword);
        Assert.Equal(HttpStatusCode.Redirect, newPasswordAttempt.StatusCode);
    }

    [Fact]
    public async Task Wrong_current_password_is_refused_and_the_real_password_is_unchanged()
    {
        var email = await CreateUserAsync("wrongcurrent", RoleNames.Teacher);
        var client = CreateClient();
        await LoginAsync(client, email, Password);

        var pageToken = await GetAntiforgeryTokenAsync(client, "/Account/ChangePassword?culture=en");
        var changeResponse = await client.PostAsync("/Account/ChangePassword?culture=en", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = pageToken,
            ["Input.CurrentPassword"] = "TotallyWrongPassword999!",
            ["Input.NewPassword"] = NewPassword,
            ["Input.ConfirmNewPassword"] = NewPassword,
        }));

        Assert.Equal(HttpStatusCode.OK, changeResponse.StatusCode);
        var body = await changeResponse.Content.ReadAsStringAsync();
        Assert.Contains("Incorrect password.", body);
        Assert.DoesNotContain("Your password has been changed.", body);

        // The original password still works — nothing was written.
        var freshClient = CreateClient();
        var loginWithOriginal = await LoginAsync(freshClient, email, Password);
        Assert.Equal(HttpStatusCode.Redirect, loginWithOriginal.StatusCode);
    }

    [Fact]
    public async Task Mismatched_confirmation_is_refused_and_the_real_password_is_unchanged()
    {
        var email = await CreateUserAsync("mismatch", RoleNames.Teacher);
        var client = CreateClient();
        await LoginAsync(client, email, Password);

        var pageToken = await GetAntiforgeryTokenAsync(client, "/Account/ChangePassword?culture=en");
        var changeResponse = await client.PostAsync("/Account/ChangePassword?culture=en", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = pageToken,
            ["Input.CurrentPassword"] = Password,
            ["Input.NewPassword"] = NewPassword,
            ["Input.ConfirmNewPassword"] = "SomethingElseEntirely789!",
        }));

        Assert.Equal(HttpStatusCode.OK, changeResponse.StatusCode);
        var body = await changeResponse.Content.ReadAsStringAsync();
        Assert.Contains("The new password and its confirmation do not match.", body);
        Assert.DoesNotContain("Your password has been changed.", body);

        var freshClient = CreateClient();
        var loginWithOriginal = await LoginAsync(freshClient, email, Password);
        Assert.Equal(HttpStatusCode.Redirect, loginWithOriginal.StatusCode);
    }

    /// <summary>The acting user is always resolved server-side from the
    /// authenticated principal — there is no user id/email input anywhere on
    /// this page, so there is no way to reach it and change a DIFFERENT
    /// account's password. Proven concretely: A changes their own password;
    /// B's original password is completely unaffected.</summary>
    [Fact]
    public async Task Changing_one_accounts_password_never_touches_another_accounts()
    {
        var emailA = await CreateUserAsync("scopeA", RoleNames.Teacher);
        var emailB = await CreateUserAsync("scopeB", RoleNames.Teacher);

        var clientA = CreateClient();
        await LoginAsync(clientA, emailA, Password);
        var pageToken = await GetAntiforgeryTokenAsync(clientA, "/Account/ChangePassword?culture=en");
        var changeResponse = await clientA.PostAsync("/Account/ChangePassword?culture=en", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = pageToken,
            ["Input.CurrentPassword"] = Password,
            ["Input.NewPassword"] = NewPassword,
            ["Input.ConfirmNewPassword"] = NewPassword,
        }));
        Assert.Equal(HttpStatusCode.OK, changeResponse.StatusCode);

        // B was never touched — their original password still works.
        var clientB = CreateClient();
        var loginB = await LoginAsync(clientB, emailB, Password);
        Assert.Equal(HttpStatusCode.Redirect, loginB.StatusCode);
    }
}
