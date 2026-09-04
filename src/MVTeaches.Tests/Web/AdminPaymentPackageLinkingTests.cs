using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MVTeaches.Application.Subscriptions;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Common;
using MVTeaches.Domain.Payments;
using MVTeaches.Domain.People;
using MVTeaches.Domain.Placement;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Domain.Subscriptions;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;
using Xunit;

namespace MVTeaches.Tests.Web;

/// <summary>
/// Owner decision 2026-09-04, from a real Local Staging run. An admin recorded
/// two payments of 50 on /Admin/Payments and confirmed both, and the package
/// they were for stayed Draft with the "packages awaiting payment" warning
/// still up. Nothing had gone wrong in the payment logic: both payments were
/// recorded with no subscription attached, because "Not paying for a package —
/// just record the money (rare)" was the FIRST and therefore default option in
/// the picker. 100 JOD sat confirmed against a 50 JOD package that could never
/// activate, because no payment pointed at it.
///
/// <para>These tests run over real HTTP against the real host, because the
/// defect was in what the screen let an admin do, not in what the service
/// would do when asked correctly — <c>PaymentServiceTests</c> already proves
/// the activation itself.</para>
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class AdminPaymentPackageLinkingTests : IClassFixture<AuthorizationTests.Factory>
{
    private readonly AuthorizationTests.Factory _factory;
    private const string Password = "CorrectHorse123!";

    public AdminPaymentPackageLinkingTests(TestDatabaseFixture fixture, AuthorizationTests.Factory factory)
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__MvTeaches", fixture.ConnectionString);
        _factory = factory;
    }

    private static readonly Regex AntiforgeryTokenPattern = new(
        "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");

    private HttpClient CreateClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path);
        var token = AntiforgeryTokenPattern.Match(html).Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token), $"Could not find the antiforgery token on {path}.");
        return token;
    }

    /// <summary>An Admin holding exactly the two payment keys — the account
    /// shape that produced the Local Staging state in the first place.</summary>
    private async Task<HttpClient> PaymentAdminClientAsync(string label)
    {
        string email;
        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            if (!await roleManager.RoleExistsAsync(RoleNames.Admin))
            {
                await roleManager.CreateAsync(new ApplicationRole(RoleNames.Admin));
            }

            email = $"paylink-{label}-{Guid.NewGuid():N}@test.mvteaches.local";
            var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
            var created = await userManager.CreateAsync(user, Password);
            Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
            await userManager.AddToRoleAsync(user, RoleNames.Admin);
            await userManager.AddClaimAsync(user, new Claim(PermissionKeys.ClaimType, PermissionKeys.PaymentsView));
            await userManager.AddClaimAsync(user, new Claim(PermissionKeys.ClaimType, PermissionKeys.PaymentsConfirm));
        }

        var client = CreateClient();
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

    /// <summary>The owner's own scenario, seeded exactly: a student with a
    /// level, a 50 JOD package published for it, and that package bought as a
    /// Draft awaiting payment. A dedicated course and level are created so this
    /// package can never be offered to another test's student.</summary>
    private async Task<(long StudentId, long SubscriptionId)> SeedStudentOwingFiftyAsync(string label)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
        var subscriptions = scope.ServiceProvider.GetRequiredService<ISubscriptionService>();

        var countryId = await db.Countries.OrderBy(c => c.Id).Select(c => c.Id).FirstAsync();

        var course = new Course($"PAYLINK-{label}-{Guid.NewGuid():N}"[..24], "دورة", "Course");
        db.Courses.Add(course);

        var levelId = 77_000_000 + Random.Shared.Next(1, 900_000);
        while (await db.Levels.AnyAsync(l => l.Id == levelId || l.SortOrder == levelId))
        {
            levelId = 77_000_000 + Random.Shared.Next(1, 900_000);
        }
        db.Levels.Add(new Level(levelId, $"PL{levelId}", "مستوى", "Level", levelId));

        var actingUserId = 1L;
        var student = new Student(countryId, $"Payment Linking Student {label}", new LocalDate(2010, 1, 1), userId: null);
        db.Students.Add(student);
        await db.SaveChangesAsync();

        db.StudentLevels.Add(new StudentLevel(student.Id, course.Id, levelId, actingUserId, AssignedByRole.Admin,
            LevelAssignmentSource.AdminOverride, null, "seed", SystemClock.Instance.GetCurrentInstant()));
        await db.SaveChangesAsync();

        var plan = await subscriptions.CreatePricingPlanAsync(countryId, course.Id, levelId, null, SessionType.Group,
            10, 600, new Money(50m, "JOD"), 90, new LocalDate(2026, 1, 1), actingUserId, CancellationToken.None);

        var purchase = await subscriptions.PurchaseFromPlanAsync(student.Id, plan.PricingPlanId, actingUserId,
            SubscriptionOrigin.GuardianPurchase, isAdminInitiated: true, CancellationToken.None);
        Assert.Equal(PurchaseFromPlanOutcome.Purchased, purchase.Outcome);

        return (student.Id, purchase.SubscriptionId!.Value);
    }

    /// <summary>The refusal that stops the Local Staging state from happening
    /// again. Recording money for a student who still owes on a package, with
    /// no package chosen, now writes nothing at all.</summary>
    [Fact]
    public async Task Recording_an_unattached_payment_for_a_student_who_owes_on_a_package_is_refused_and_writes_nothing()
    {
        var (studentId, subscriptionId) = await SeedStudentOwingFiftyAsync("refuse");
        var client = await PaymentAdminClientAsync("refuse");

        var token = await GetAntiforgeryTokenAsync(client, "/Admin/Payments");
        var response = await client.PostAsync("/Admin/Payments?handler=Record", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["NewPayment.StudentId"] = studentId.ToString(),
            ["NewPayment.Amount"] = "50",
            ["NewPayment.Currency"] = "JOD",
            ["NewPayment.Method"] = "Cash",
            // No NewPayment.SubscriptionId at all — exactly what the default
            // first option in the old picker submitted.
        }));

        // The page re-renders carrying the refusal; this is a business refusal,
        // not an authorization one.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
        Assert.Empty(await db.Payments.Where(p => p.StudentId == studentId).ToListAsync());

        // And the package is untouched — still Draft, still owed.
        var subscription = await db.Subscriptions.FirstAsync(s => s.Id == subscriptionId);
        Assert.Equal(SubscriptionStatus.Draft, subscription.Status);
    }

    /// <summary>The owner's acceptance path, end to end: 50 owed, 50 recorded
    /// ATTACHED, confirmed — the package activates, exactly one ledger entry is
    /// posted, and it stops being counted as awaiting payment (the dashboard's
    /// warning is a straight count of Draft subscriptions).</summary>
    [Fact]
    public async Task Confirming_a_payment_attached_to_the_package_activates_it_once_and_clears_the_awaiting_warning()
    {
        var (studentId, subscriptionId) = await SeedStudentOwingFiftyAsync("attach");
        var client = await PaymentAdminClientAsync("attach");

        var recordToken = await GetAntiforgeryTokenAsync(client, "/Admin/Payments");
        var recordResponse = await client.PostAsync("/Admin/Payments?handler=Record", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = recordToken,
            ["NewPayment.StudentId"] = studentId.ToString(),
            ["NewPayment.SubscriptionId"] = subscriptionId.ToString(),
            ["NewPayment.Amount"] = "50",
            ["NewPayment.Currency"] = "JOD",
            ["NewPayment.Method"] = "Cash",
        }));
        Assert.Equal(HttpStatusCode.OK, recordResponse.StatusCode);

        long paymentId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
            var payment = await db.Payments.SingleAsync(p => p.StudentId == studentId);
            Assert.Equal(subscriptionId, payment.SubscriptionId); // attached, which is the whole point
            Assert.Equal(PaymentStatus.Pending, payment.Status);
            paymentId = payment.Id;
        }

        var confirmToken = await GetAntiforgeryTokenAsync(client, "/Admin/Payments");
        var confirmResponse = await client.PostAsync("/Admin/Payments?handler=Confirm", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = confirmToken,
            ["paymentId"] = paymentId.ToString(),
        }));
        Assert.Equal(HttpStatusCode.OK, confirmResponse.StatusCode);

        using var verify = _factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<MvTeachesDbContext>();

        var settled = await verifyDb.Payments.FirstAsync(p => p.Id == paymentId);
        Assert.Equal(PaymentStatus.Confirmed, settled.Status);

        var subscription = await verifyDb.Subscriptions.FirstAsync(s => s.Id == subscriptionId);
        Assert.Equal(SubscriptionStatus.Active, subscription.Status);

        // Exactly one — the purchase credit, posted once and only once.
        Assert.Equal(1, await verifyDb.EntitlementLedgerEntries.CountAsync(l => l.SubscriptionId == subscriptionId));

        // The dashboard's "packages awaiting payment" figure counts Draft
        // subscriptions, so this package no longer appears in it.
        Assert.False(await verifyDb.Subscriptions
            .AnyAsync(s => s.StudentId == studentId && s.Status == SubscriptionStatus.Draft));
    }

    /// <summary>The escape hatch still exists where it should: a student with
    /// nothing owing may still have money recorded against no package — a
    /// correction, a refund reversal, or a deposit.</summary>
    [Fact]
    public async Task An_unattached_payment_is_still_allowed_for_a_student_who_owes_nothing()
    {
        long studentId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
            var countryId = await db.Countries.OrderBy(c => c.Id).Select(c => c.Id).FirstAsync();
            var student = new Student(countryId, $"No Package Student {Guid.NewGuid():N}", new LocalDate(2008, 3, 3), userId: null);
            db.Students.Add(student);
            await db.SaveChangesAsync();
            studentId = student.Id;
        }

        var client = await PaymentAdminClientAsync("nodraft");
        var token = await GetAntiforgeryTokenAsync(client, "/Admin/Payments");
        var response = await client.PostAsync("/Admin/Payments?handler=Record", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["NewPayment.StudentId"] = studentId.ToString(),
            ["NewPayment.Amount"] = "12",
            ["NewPayment.Currency"] = "JOD",
            ["NewPayment.Method"] = "Cash",
        }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope2 = _factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
        var recorded = await db2.Payments.SingleAsync(p => p.StudentId == studentId);
        Assert.Null(recorded.SubscriptionId);
        Assert.Equal(12m, recorded.Amount.Amount);
    }
}
