using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using Xunit;

namespace MVTeaches.Tests.Web;

/// <summary>
/// Owner decision 2026-09-05: managing discount codes needs its own key,
/// <see cref="PermissionKeys.PromoCodesManage"/>, and deliberately NOT
/// SubscriptionsManage — "من يبيع باقة ليس بالضرورة يقرر خصومات وأسعار".
///
/// <para>These run over real HTTP because the thing being asserted is that the
/// SERVER refuses, not that a button was hidden. Each POST is sent by an admin
/// who is missing the key and holds a neighbouring one, and the assertion is
/// always the same pair: refused, and nothing written.</para>
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class PromoCodePermissionTests : IClassFixture<AuthorizationTests.Factory>
{
    private readonly AuthorizationTests.Factory _factory;
    private const string Password = "CorrectHorse123!";

    public PromoCodePermissionTests(TestDatabaseFixture fixture, AuthorizationTests.Factory factory)
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__MvTeaches", fixture.ConnectionString);
        _factory = factory;
    }

    private static readonly Regex AntiforgeryTokenPattern = new(
        "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");

    private HttpClient CreateClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private async Task<HttpClient> SignInAsync(string label, bool systemAdmin, params string[] permissions)
    {
        string email;
        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            foreach (var role in new[] { RoleNames.Admin, RoleNames.SystemAdmin })
            {
                if (!await roleManager.RoleExistsAsync(role))
                {
                    await roleManager.CreateAsync(new ApplicationRole(role));
                }
            }

            email = $"promoperm-{label}-{Guid.NewGuid():N}@test.mvteaches.local";
            var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
            var created = await userManager.CreateAsync(user, Password);
            Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
            await userManager.AddToRoleAsync(user, RoleNames.Admin);
            if (systemAdmin)
            {
                await userManager.AddToRoleAsync(user, RoleNames.SystemAdmin);
            }

            foreach (var permission in permissions)
            {
                await userManager.AddClaimAsync(user, new Claim(PermissionKeys.ClaimType, permission));
            }
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

    /// <summary>The key point: SubscriptionsManage is not enough. Selling a
    /// package and setting the centre's discounts are different jobs.</summary>
    [Fact]
    public async Task An_admin_with_only_subscriptions_manage_cannot_open_promo_codes()
    {
        var client = await SignInAsync("subsonly", systemAdmin: false,
            PermissionKeys.SubscriptionsView, PermissionKeys.SubscriptionsManage);

        var response = await client.GetAsync("/Admin/PromoCodes");

        Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Redirect,
            $"Expected the page to be refused, got {(int)response.StatusCode}.");
    }

    /// <summary>And cannot create one by posting straight at the handler, which
    /// is the check that actually matters.</summary>
    [Fact]
    public async Task An_admin_without_the_key_cannot_create_a_promo_code()
    {
        var before = await CountCodesAsync();
        var client = await SignInAsync("nokey", systemAdmin: false,
            PermissionKeys.SubscriptionsView, PermissionKeys.SubscriptionsManage);

        var response = await client.PostAsync("/Admin/PromoCodes?handler=Create", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["NewCode.Code"] = "AB12CD",
                ["NewCode.DiscountPercent"] = "50",
                ["NewCode.IsActive"] = "true",
                ["NewCode.AppliesToAllPlans"] = "true",
            }));

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(before, await CountCodesAsync());
        Assert.Equal(0, await CountCodesAsync("AB12CD"));
    }

    [Fact]
    public async Task An_admin_without_the_key_cannot_disable_a_promo_code()
    {
        long codeId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
            var promo = new MVTeaches.Domain.Subscriptions.PromoCode("QQ11WW", 15, true, null, null, null, null, 1L,
                NodaTime.SystemClock.Instance.GetCurrentInstant());
            db.PromoCodes.Add(promo);
            await db.SaveChangesAsync();
            codeId = promo.Id;
        }

        var client = await SignInAsync("nodisable", systemAdmin: false, PermissionKeys.SubscriptionsManage);

        var response = await client.PostAsync("/Admin/PromoCodes?handler=SetActive", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["promoCodeId"] = codeId.ToString(),
                ["isActive"] = "false",
            }));

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);

        using var verify = _factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
        var stored = await verifyDb.PromoCodes.SingleAsync(p => p.Id == codeId);
        Assert.True(stored.IsActive, "The code was disabled by an admin who does not hold the key.");
    }

    /// <summary>A SystemAdmin holds it — the bypass that already governs every
    /// other key on this project, asserted here so "nobody can" is not the
    /// accidental outcome.</summary>
    [Fact]
    public async Task A_system_admin_can_open_and_create()
    {
        var client = await SignInAsync("sysadmin", systemAdmin: true);

        var page = await client.GetAsync("/Admin/PromoCodes?culture=en");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        var html = await page.Content.ReadAsStringAsync();
        var response = await client.PostAsync("/Admin/PromoCodes?handler=Create&culture=en", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = AntiforgeryTokenPattern.Match(html).Groups[1].Value,
                ["NewCode.Code"] = "SYS123",
                ["NewCode.DiscountPercent"] = "30",
                ["NewCode.IsActive"] = "true",
                ["NewCode.AppliesToAllPlans"] = "true",
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await CountCodesAsync("SYS123"));
    }

    /// <summary>An admin who holds the key, and no SystemAdmin role, can use
    /// it — otherwise the key would be decorative.</summary>
    [Fact]
    public async Task An_admin_holding_the_key_can_create()
    {
        var client = await SignInAsync("haskey", systemAdmin: false, PermissionKeys.PromoCodesManage);

        var page = await client.GetAsync("/Admin/PromoCodes?culture=en");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        var html = await page.Content.ReadAsStringAsync();
        var response = await client.PostAsync("/Admin/PromoCodes?handler=Create&culture=en", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = AntiforgeryTokenPattern.Match(html).Groups[1].Value,
                ["NewCode.Code"] = "KEY123",
                ["NewCode.DiscountPercent"] = "40",
                ["NewCode.IsActive"] = "true",
                ["NewCode.AppliesToAllPlans"] = "true",
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await CountCodesAsync("KEY123"));
    }

    private async Task<int> CountCodesAsync(string? code = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
        return code is null
            ? await db.PromoCodes.CountAsync()
            : await db.PromoCodes.CountAsync(p => p.Code == code);
    }
}
