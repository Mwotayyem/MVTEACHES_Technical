using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Payments;
using MVTeaches.Domain.People;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;
using Xunit;

namespace MVTeaches.Tests.Web;

/// <summary>
/// Security review 2026-09-02 (Review Required — Authorization), Stage 1: a
/// real ASP.NET Core host (WebApplicationFactory, reusing AuthorizationTests'
/// Factory) exercising PermissionAuthorizationHandler end-to-end via real
/// HTTP requests against the real DB — nothing mocked. Covers exactly the
/// scenarios the owner's rollout instructions listed: SystemAdmin acting
/// with zero claims, a plain Admin refused with zero claims, a View-only
/// Admin blocked from every mutating handler with proof nothing was written,
/// a fully-permissioned Admin succeeding, /Admin/AdminUsers restricted to
/// SystemAdmin alone, and a revoked permission taking effect on the very
/// next request with no logout/login.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class AdminPermissionTests : IClassFixture<AuthorizationTests.Factory>, IAsyncLifetime
{
    private readonly AuthorizationTests.Factory _factory;
    private const string Password = "CorrectHorse123!";

    public AdminPermissionTests(TestDatabaseFixture fixture, AuthorizationTests.Factory factory)
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

        var email = $"permtest-{label}-{Guid.NewGuid():N}@test.mvteaches.local";
        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
        var result = await userManager.CreateAsync(user, Password);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));
        await userManager.AddToRoleAsync(user, role);
        return email;
    }

    private async Task GrantAsync(string email, params string[] keys)
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByEmailAsync(email);
        foreach (var key in keys)
        {
            await userManager.AddClaimAsync(user!, new Claim(PermissionKeys.ClaimType, key));
        }
    }

    private async Task RevokeAsync(string email, string key)
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByEmailAsync(email);
        await userManager.RemoveClaimAsync(user!, new Claim(PermissionKeys.ClaimType, key));
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path);
        var token = AntiforgeryTokenPattern.Match(html).Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token), $"Could not find the antiforgery token on {path}.");
        return token;
    }

    private static async Task<HttpClient> LoggedInClientAsync(HttpClient client, string email)
    {
        var token = await GetAntiforgeryTokenAsync(client, "/Account/Login");
        var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Input.Email"] = email,
            ["Input.Password"] = Password,
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    /// <summary>Seeds one real, standalone (no subscription) Pending payment
    /// via the DB directly — the same shape RecordManualPaymentAsync itself
    /// produces — so tests can attempt to confirm/reject a REAL row without
    /// needing a full subscription/pricing-plan graph.</summary>
    private async Task<(long StudentId, long PaymentId)> SeedPendingPaymentAsync(string label)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();

        var countryId = await SeedCountryAsync(db);
        var student = new Student(countryId, $"Permission Test Student {label}", new LocalDate(2005, 1, 1), userId: null);
        db.Students.Add(student);
        await db.SaveChangesAsync();

        var payment = new Payment(student.Id, subscriptionId: null, payerUserId: null, new MVTeaches.Domain.Common.Money(25m, "JOD"),
            PaymentMethod.Cash, providerKey: "manual", referenceCode: $"MVT-PERMTEST-{Guid.NewGuid():N}"[..20],
            SystemClock.Instance.GetCurrentInstant());
        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        return (student.Id, payment.Id);
    }

    private static long _idSeed = 80_000_000;
    private static long NextId() => Interlocked.Increment(ref _idSeed);

    private static string TwoLetterCode(long seed)
    {
        var n = seed % 676;
        return $"{(char)('A' + n / 26)}{(char)('A' + n % 26)}";
    }

    /// <summary>Same reason as PaymentServiceTests.SeedCountryAsync and
    /// SessionFinalizationServiceTests.GetOrSeedCountryAsync: the 2-letter
    /// country-code space is only 676 wide and shared by every test class in
    /// the same run through identical TwoLetterCode arithmetic, so a residue
    /// collision with another class's range is a real flake, not a
    /// theoretical one. Retrying on the actual unique violation is
    /// self-correcting; a hand-picked non-overlapping range is not.</summary>
    private static async Task<int> SeedCountryAsync(MvTeachesDbContext db)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var countryId = (int)NextId();
            db.Countries.Add(new Country(countryId, TwoLetterCode(countryId), "دولة", "Country", "JOD", "+962", "Asia/Amman"));
            try
            {
                await db.SaveChangesAsync();
                return countryId;
            }
            catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
            {
                db.ChangeTracker.Clear();
            }
        }

        throw new InvalidOperationException("Could not find a free 2-letter country code after 10 attempts.");
    }

    // ---------------------------------------------------------------
    // 1. SystemAdmin: sees and acts on everything with ZERO claims.
    // ---------------------------------------------------------------

    [Fact]
    public async Task SystemAdmin_views_and_records_a_payment_with_zero_permission_claims()
    {
        var email = await CreateUserAsync("sa-payments", RoleNames.SystemAdmin);
        var client = await LoggedInClientAsync(CreateClient(), email);

        var getResponse = await client.GetAsync("/Admin/Payments");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        var (studentId, _) = await SeedPendingPaymentAsync("sa-record-target");
        var token = await GetAntiforgeryTokenAsync(client, "/Admin/Payments");
        var response = await client.PostAsync("/Admin/Payments?handler=Record", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["NewPayment.StudentId"] = studentId.ToString(),
            ["NewPayment.Amount"] = "15",
            ["NewPayment.Currency"] = "JOD",
            ["NewPayment.Method"] = "Cash",
        }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
        Assert.True(await db.Payments.AnyAsync(p => p.StudentId == studentId && p.Amount.Amount == 15m));
    }

    [Fact]
    public async Task SystemAdmin_views_payroll_and_subscriptions_with_zero_permission_claims()
    {
        var email = await CreateUserAsync("sa-payroll-subs", RoleNames.SystemAdmin);
        var client = await LoggedInClientAsync(CreateClient(), email);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Admin/Payroll")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Admin/Subscriptions")).StatusCode);
    }

    // ---------------------------------------------------------------
    // 2. Plain Admin, zero claims: forbidden from all three Stage 1 pages.
    // ---------------------------------------------------------------

    [Fact]
    public async Task Admin_with_no_permission_claims_is_forbidden_from_payments_payroll_and_subscriptions()
    {
        var email = await CreateUserAsync("no-claims", RoleNames.Admin);
        var client = await LoggedInClientAsync(CreateClient(), email);

        Assert.NotEqual(HttpStatusCode.OK, (await client.GetAsync("/Admin/Payments")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await client.GetAsync("/Admin/Payroll")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await client.GetAsync("/Admin/Subscriptions")).StatusCode);
    }

    // ---------------------------------------------------------------
    // 3+4. Admin with only Payments.View: sees the page, but Confirm/Reject/
    // Record are all refused, and refusing writes nothing.
    // ---------------------------------------------------------------

    [Fact]
    public async Task Admin_with_only_payments_view_sees_the_page_but_every_mutation_is_refused_and_writes_nothing()
    {
        var email = await CreateUserAsync("payments-view-only", RoleNames.Admin);
        await GrantAsync(email, PermissionKeys.PaymentsView);
        var client = await LoggedInClientAsync(CreateClient(), email);

        var getResponse = await client.GetAsync("/Admin/Payments");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        var (studentId, paymentId) = await SeedPendingPaymentAsync("view-only-target");
        var token = await GetAntiforgeryTokenAsync(client, "/Admin/Payments");

        // Record is refused.
        var recordResponse = await client.PostAsync("/Admin/Payments?handler=Record", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["NewPayment.StudentId"] = studentId.ToString(),
            ["NewPayment.Amount"] = "99",
            ["NewPayment.Currency"] = "JOD",
            ["NewPayment.Method"] = "Cash",
        }));
        Assert.NotEqual(HttpStatusCode.OK, recordResponse.StatusCode);

        // Confirm on a REAL pending payment is refused and changes nothing.
        var confirmResponse = await client.PostAsync("/Admin/Payments?handler=Confirm", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["paymentId"] = paymentId.ToString(),
        }));
        Assert.NotEqual(HttpStatusCode.OK, confirmResponse.StatusCode);

        // Reject is refused too.
        var rejectResponse = await client.PostAsync("/Admin/Payments?handler=Reject", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["paymentId"] = paymentId.ToString(),
        }));
        Assert.NotEqual(HttpStatusCode.OK, rejectResponse.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
        Assert.False(await db.Payments.AnyAsync(p => p.StudentId == studentId && p.Amount.Amount == 99m)); // Record never wrote
        var stillPending = await db.Payments.FirstAsync(p => p.Id == paymentId);
        Assert.Equal(PaymentStatus.Pending, stillPending.Status); // Confirm/Reject never touched it
        Assert.Null(stillPending.ConfirmedAtUtc);
    }

    // ---------------------------------------------------------------
    // 5. Admin with Payments.Confirm: can actually confirm a real payment.
    // ---------------------------------------------------------------

    [Fact]
    public async Task Admin_with_payments_confirm_can_confirm_a_real_payment()
    {
        var email = await CreateUserAsync("payments-confirm", RoleNames.Admin);
        await GrantAsync(email, PermissionKeys.PaymentsView, PermissionKeys.PaymentsConfirm);
        var client = await LoggedInClientAsync(CreateClient(), email);

        var (_, paymentId) = await SeedPendingPaymentAsync("confirm-target");
        var token = await GetAntiforgeryTokenAsync(client, "/Admin/Payments");
        var response = await client.PostAsync("/Admin/Payments?handler=Confirm", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["paymentId"] = paymentId.ToString(),
        }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
        var confirmed = await db.Payments.FirstAsync(p => p.Id == paymentId);
        Assert.Equal(PaymentStatus.Confirmed, confirmed.Status);
    }

    // ---------------------------------------------------------------
    // 6+7. Payroll: no View => forbidden; View-only => sees page, cannot approve.
    // ---------------------------------------------------------------

    [Fact]
    public async Task Admin_without_payroll_view_cannot_open_payroll()
    {
        var email = await CreateUserAsync("no-payroll-view", RoleNames.Admin);
        var client = await LoggedInClientAsync(CreateClient(), email);

        Assert.NotEqual(HttpStatusCode.OK, (await client.GetAsync("/Admin/Payroll")).StatusCode);
    }

    [Fact]
    public async Task Admin_with_payroll_view_only_sees_the_page_but_cannot_open_a_period()
    {
        var email = await CreateUserAsync("payroll-view-only", RoleNames.Admin);
        await GrantAsync(email, PermissionKeys.PayrollView);
        var client = await LoggedInClientAsync(CreateClient(), email);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Admin/Payroll")).StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
        var countryId = await SeedCountryAsync(db);

        var token = await GetAntiforgeryTokenAsync(client, "/Admin/Payroll");
        var response = await client.PostAsync("/Admin/Payroll?handler=OpenPeriod", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["NewPeriod.CountryId"] = countryId.ToString(),
            ["NewPeriod.Start"] = "2026-01-01",
            ["NewPeriod.End"] = "2026-01-31",
        }));
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);

        Assert.False(await db.PayrollPeriods.AnyAsync(p => p.CountryId == countryId));
    }

    // ---------------------------------------------------------------
    // 8. Subscriptions: View-only cannot create/purchase/grant.
    // ---------------------------------------------------------------

    [Fact]
    public async Task Admin_with_subscriptions_view_only_cannot_create_a_pricing_plan()
    {
        var email = await CreateUserAsync("subs-view-only", RoleNames.Admin);
        await GrantAsync(email, PermissionKeys.SubscriptionsView);
        var client = await LoggedInClientAsync(CreateClient(), email);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Admin/Subscriptions")).StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
        var countryId = await SeedCountryAsync(db);
        var courseCode = $"PERMTEST-{countryId}";
        db.Courses.Add(new Course(courseCode, "دورة", "Course"));
        await db.SaveChangesAsync();
        var courseId = await db.Courses.Where(c => c.Code == courseCode).Select(c => c.Id).FirstAsync();

        var token = await GetAntiforgeryTokenAsync(client, "/Admin/Subscriptions");
        var response = await client.PostAsync("/Admin/Subscriptions?handler=CreatePlan", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["NewPlan.CountryId"] = countryId.ToString(),
            ["NewPlan.CourseId"] = courseId.ToString(),
            ["NewPlan.SessionType"] = "Group",
            ["NewPlan.SessionsCount"] = "10",
            ["NewPlan.MinutesPerSession"] = "60",
            ["NewPlan.Amount"] = "50",
            ["NewPlan.Currency"] = "JOD",
            ["NewPlan.ValidityDays"] = "90",
        }));
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);

        Assert.False(await db.PricingPlans.AnyAsync(p => p.CourseId == courseId));
    }

    // ---------------------------------------------------------------
    // 9. Only SystemAdmin can open /Admin/AdminUsers.
    // ---------------------------------------------------------------

    [Fact]
    public async Task Only_systemadmin_can_open_admin_users_page()
    {
        var adminEmail = await CreateUserAsync("plain-tries-adminusers", RoleNames.Admin);
        var adminClient = await LoggedInClientAsync(CreateClient(), adminEmail);
        Assert.NotEqual(HttpStatusCode.OK, (await adminClient.GetAsync("/Admin/AdminUsers")).StatusCode);

        var systemAdminEmail = await CreateUserAsync("real-owner", RoleNames.SystemAdmin);
        var systemAdminClient = await LoggedInClientAsync(CreateClient(), systemAdminEmail);
        Assert.Equal(HttpStatusCode.OK, (await systemAdminClient.GetAsync("/Admin/AdminUsers")).StatusCode);
    }

    /// <summary>End-to-end proof of the real grant flow through the actual UI,
    /// not just direct claim seeding: a SystemAdmin creates a plain Admin,
    /// grants exactly Payments.View through /Admin/AdminUsers?handler=SavePermissions,
    /// and that new Admin can then reach Payments (but nothing else).</summary>
    [Fact]
    public async Task SystemAdmin_creates_an_admin_and_grants_permissions_through_the_real_admin_users_page()
    {
        var systemAdminEmail = await CreateUserAsync("granting-owner", RoleNames.SystemAdmin);
        var client = await LoggedInClientAsync(CreateClient(), systemAdminEmail);

        var newAdminEmail = $"permtest-created-{Guid.NewGuid():N}@test.mvteaches.local";
        var createToken = await GetAntiforgeryTokenAsync(client, "/Admin/AdminUsers");
        var createResponse = await client.PostAsync("/Admin/AdminUsers?handler=CreateAdmin", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = createToken,
            ["NewAdmin.Email"] = newAdminEmail,
            ["NewAdmin.Password"] = Password,
        }));
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);

        long newAdminId;
        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var created = await userManager.FindByEmailAsync(newAdminEmail);
            Assert.NotNull(created);
            Assert.True(await userManager.IsInRoleAsync(created!, RoleNames.Admin));
            Assert.False(await userManager.IsInRoleAsync(created!, RoleNames.SystemAdmin));
            Assert.Empty(await userManager.GetClaimsAsync(created!)); // zero permissions by default
            newAdminId = created!.Id;
        }

        var savePage = await client.GetStringAsync("/Admin/AdminUsers");
        var saveToken = AntiforgeryTokenPattern.Match(savePage).Groups[1].Value;
        var saveResponse = await client.PostAsync("/Admin/AdminUsers?handler=SavePermissions", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = saveToken,
            ["EditingAdminId"] = newAdminId.ToString(),
            ["Granted"] = PermissionKeys.PaymentsView,
        }));
        Assert.Equal(HttpStatusCode.OK, saveResponse.StatusCode);

        var newAdminClient = await LoggedInClientAsync(CreateClient(), newAdminEmail);
        Assert.Equal(HttpStatusCode.OK, (await newAdminClient.GetAsync("/Admin/Payments")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await newAdminClient.GetAsync("/Admin/Payroll")).StatusCode);
    }

    // ---------------------------------------------------------------
    // 10. Revoking a permission takes effect on the very next request,
    // with no logout/login — the whole reason the check is live against
    // the database rather than the signed-in cookie's cached claims.
    // ---------------------------------------------------------------

    [Fact]
    public async Task Revoking_a_permission_blocks_the_very_next_request_with_no_relogin()
    {
        var email = await CreateUserAsync("revoke-target", RoleNames.Admin);
        await GrantAsync(email, PermissionKeys.PaymentsView);
        var client = await LoggedInClientAsync(CreateClient(), email);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Admin/Payments")).StatusCode);

        await RevokeAsync(email, PermissionKeys.PaymentsView);

        // Same client, same cookie, no logout/login in between.
        Assert.NotEqual(HttpStatusCode.OK, (await client.GetAsync("/Admin/Payments")).StatusCode);
    }
}
