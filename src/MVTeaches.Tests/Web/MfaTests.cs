using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MVTeaches.Infrastructure.Identity;
using Xunit;

namespace MVTeaches.Tests.Web;

/// <summary>
/// §22's "MFA إلزامي لـ SystemAdmin وReadOnlyAdmin" — a real ASP.NET Core host
/// (WebApplicationFactory, reusing AuthorizationTests' Factory) exercising the
/// TOTP enrollment and challenge flow end-to-end via real HTTP requests, with
/// a standard RFC 6238 code generator standing in for an authenticator app —
/// the same algorithm Identity's own AuthenticatorTokenProvider uses, not a
/// mock of the verification itself (VerifyTwoFactorTokenAsync/
/// TwoFactorAuthenticatorSignInAsync run for real against the real DB).
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class MfaTests : IClassFixture<AuthorizationTests.Factory>, IAsyncLifetime
{
    private readonly AuthorizationTests.Factory _factory;
    private const string Password = "CorrectHorse123!";

    public MfaTests(TestDatabaseFixture fixture, AuthorizationTests.Factory factory)
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__MvTeaches", fixture.ConnectionString);
        _factory = factory;
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly Regex AntiforgeryTokenPattern = new(
        "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");
    private static readonly Regex SharedKeyPattern = new("<code>([^<]+)</code>");

    private HttpClient CreateClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private async Task<(ApplicationUser User, string Email)> CreateUserAsync(string label, string role)
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        if (!await roleManager.RoleExistsAsync(role))
        {
            await roleManager.CreateAsync(new ApplicationRole(role));
        }

        var email = $"mfatest-{label}-{Guid.NewGuid():N}@test.mvteaches.local";
        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
        var result = await userManager.CreateAsync(user, Password);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));
        await userManager.AddToRoleAsync(user, role);
        return (user, email);
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path);
        var token = AntiforgeryTokenPattern.Match(html).Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token), $"Could not find the antiforgery token on {path}.");
        return token;
    }

    /// <summary>RFC 6238, matching Identity's own AuthenticatorTokenProvider —
    /// a real code from a real (test) secret, not a stubbed verification.</summary>
    private static string GenerateTotpCode(string base32Secret)
    {
        var key = Base32Decode(base32Secret);
        var timestep = (long)(DateTimeOffset.UtcNow - DateTimeOffset.UnixEpoch).TotalSeconds / 30;
        var counter = BitConverter.GetBytes(timestep);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(counter);
        }

        using var hmac = new HMACSHA1(key);
        var hash = hmac.ComputeHash(counter);
        var offset = hash[^1] & 0xf;
        var binaryCode = ((hash[offset] & 0x7f) << 24) | ((hash[offset + 1] & 0xff) << 16)
                          | ((hash[offset + 2] & 0xff) << 8) | (hash[offset + 3] & 0xff);
        return (binaryCode % 1_000_000).ToString("D6");
    }

    private static byte[] Base32Decode(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        input = input.Replace(" ", string.Empty).TrimEnd('=').ToUpperInvariant();
        var bits = new StringBuilder();
        foreach (var c in input)
        {
            var index = alphabet.IndexOf(c);
            if (index < 0)
            {
                continue;
            }

            bits.Append(Convert.ToString(index, 2).PadLeft(5, '0'));
        }

        var bytes = new List<byte>();
        for (var i = 0; i + 8 <= bits.Length; i += 8)
        {
            bytes.Add(Convert.ToByte(bits.ToString(i, 8), 2));
        }

        return bytes.ToArray();
    }

    [Fact]
    public async Task An_admin_without_mfa_is_redirected_to_set_it_up_right_after_login()
    {
        var (_, email) = await CreateUserAsync("admin", RoleNames.Admin);
        var client = CreateClient();

        var token = await GetAntiforgeryTokenAsync(client, "/Account/Login");
        var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Input.Email"] = email,
            ["Input.Password"] = Password,
        }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/ManageMfa", response.Headers.Location?.ToString() ?? string.Empty);
    }

    [Fact]
    public async Task A_teacher_without_mfa_is_not_forced_to_set_it_up()
    {
        var (_, email) = await CreateUserAsync("teacher", RoleNames.Teacher);
        var client = CreateClient();

        var token = await GetAntiforgeryTokenAsync(client, "/Account/Login");
        var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Input.Email"] = email,
            ["Input.Password"] = Password,
        }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.DoesNotContain("/Account/ManageMfa", response.Headers.Location?.ToString() ?? string.Empty);
    }

    [Fact]
    public async Task Navigating_directly_to_the_2fa_challenge_with_no_pending_login_redirects_to_the_login_page()
    {
        var client = CreateClient();

        var response = await client.GetAsync("/Account/LoginWith2fa");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location?.ToString() ?? string.Empty);
    }

    [Fact]
    public async Task Full_enrollment_and_challenge_round_trip_with_a_real_totp_code()
    {
        var (_, email) = await CreateUserAsync("full", RoleNames.Admin);
        // Stage 2D (2026-09-03): Step 6 below proves a real admin page is
        // reachable after full sign-in by loading /Admin/Dashboard, which now
        // requires Admin.Dashboard.View — grant only that one key so the step
        // keeps proving what it always proved (MFA completes, an admin page
        // opens), not a permissions scenario. Looked up fresh in its own
        // scope (not the ApplicationUser returned by CreateUserAsync, which
        // is tracked by that call's own now-disposed DbContext).
        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var freshUser = await userManager.FindByEmailAsync(email);
            await userManager.AddClaimAsync(freshUser!, new Claim(PermissionKeys.ClaimType, PermissionKeys.DashboardView));
        }

        var client = CreateClient();

        // --- Step 1: log in with password only, land on the mandatory MFA page ---
        var loginToken = await GetAntiforgeryTokenAsync(client, "/Account/Login");
        var loginResponse = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = loginToken,
            ["Input.Email"] = email,
            ["Input.Password"] = Password,
        }));
        Assert.Contains("/Account/ManageMfa", loginResponse.Headers.Location?.ToString() ?? string.Empty);

        // --- Step 2: fetch the enrollment page, extract the real shared key ---
        // ?culture=en pins the assertions below to a known language — the
        // page is now fully localized and defaults to ar-JO, same convention
        // as AuthorizationTests/LocalizationAndShellTests.
        var mfaPageHtml = await client.GetStringAsync("/Account/ManageMfa?culture=en");
        var sharedKey = SharedKeyPattern.Match(mfaPageHtml).Groups[1].Value;
        Assert.False(string.IsNullOrWhiteSpace(sharedKey), "Could not find the shared key on the MFA enrollment page.");
        var verifyToken = AntiforgeryTokenPattern.Match(mfaPageHtml).Groups[1].Value;

        // --- Step 3: verify with a REAL RFC 6238 code computed from that key ---
        var code = GenerateTotpCode(sharedKey);
        var verifyResponse = await client.PostAsync("/Account/ManageMfa?handler=Verify&culture=en", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = verifyToken,
            ["Verify.Code"] = code,
        }));
        Assert.Equal(HttpStatusCode.OK, verifyResponse.StatusCode); // Page() re-render, not a redirect
        var afterVerifyHtml = await verifyResponse.Content.ReadAsStringAsync();
        Assert.Contains("now enabled", afterVerifyHtml);
        Assert.Contains("recovery code", afterVerifyHtml, StringComparison.OrdinalIgnoreCase);

        // --- Step 4: log out, log back in — this time land on the 2FA challenge, not ManageMfa ---
        // Stage 2D (2026-09-03): was "/Admin/Dashboard", which now requires
        // Admin.Dashboard.View — this test's Admin user holds no permission
        // claims at all (it exists only to exercise the MFA round-trip), so
        // any already-reachable authenticated page works just as well as a
        // source of a fresh antiforgery token; ManageMfa (already visited
        // above) needs no permission at all.
        var logoutToken = await GetAntiforgeryTokenAsync(client, "/Account/ManageMfa?culture=en");
        await client.PostAsync("/Account/Logout", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = logoutToken,
        }));

        var secondLoginToken = await GetAntiforgeryTokenAsync(client, "/Account/Login");
        var secondLoginResponse = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = secondLoginToken,
            ["Input.Email"] = email,
            ["Input.Password"] = Password,
        }));
        Assert.Contains("/Account/LoginWith2fa", secondLoginResponse.Headers.Location?.ToString() ?? string.Empty);

        // --- Step 5: complete the challenge with a fresh real code ---
        var challengeHtml = await client.GetStringAsync("/Account/LoginWith2fa");
        var challengeToken = AntiforgeryTokenPattern.Match(challengeHtml).Groups[1].Value;
        var challengeCode = GenerateTotpCode(sharedKey);
        var challengeResponse = await client.PostAsync("/Account/LoginWith2fa", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = challengeToken,
            ["Input.Code"] = challengeCode,
        }));

        Assert.Equal(HttpStatusCode.Redirect, challengeResponse.StatusCode);
        Assert.DoesNotContain("/Account/Login", challengeResponse.Headers.Location?.ToString() ?? string.Empty);

        // --- Step 6: a real admin page is now reachable — full sign-in actually completed ---
        var dashboardResponse = await client.GetAsync("/Admin/Dashboard");
        Assert.Equal(HttpStatusCode.OK, dashboardResponse.StatusCode);
    }
}
