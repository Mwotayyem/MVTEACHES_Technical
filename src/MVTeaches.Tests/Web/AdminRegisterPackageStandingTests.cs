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
using MVTeaches.Domain.People;
using MVTeaches.Domain.Placement;
using MVTeaches.Domain.Subscriptions;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;
using Xunit;

namespace MVTeaches.Tests.Web;

/// <summary>
/// Owner report 2026-09-05, from a real Local Staging run. On
/// /Admin/Students?state=PaymentDue one row said "no package" and, in the same
/// row, "payment due — 60 JOD outstanding".
///
/// <para>Both halves were computed correctly and separately: the package column
/// from the ACTIVE subscription (there was none), the state chip and the money
/// from the Draft one (there was). A Draft is a package — it has a course, a
/// level and a price, and it is the reason the money is owed — so the register
/// now reports which of four things a student's package is doing rather than
/// only whether one is running.</para>
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class AdminRegisterPackageStandingTests : IClassFixture<AuthorizationTests.Factory>
{
    private readonly AuthorizationTests.Factory _factory;
    private const string Password = "CorrectHorse123!";
    private static long _idSeed = 880_000_000;

    public AdminRegisterPackageStandingTests(TestDatabaseFixture fixture, AuthorizationTests.Factory factory)
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__MvTeaches", fixture.ConnectionString);
        _factory = factory;
    }

    private static long NextId() => Interlocked.Increment(ref _idSeed);


    /// <summary>A level id that is actually free. Hand-picked seed ranges are
    /// not self-correcting - the next test class added takes the range, and the
    /// collision surfaces only in a FULL run, as this one did (PK_levels, 23505).
    /// Catching the real unique violation and retrying is, which is the same
    /// reasoning SubscriptionServiceTests records for country codes.</summary>
    private static async Task<int> SeedLevelAsync(MvTeachesDbContext db, string prefix)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var levelId = (int)NextId();
            db.Levels.Add(new MVTeaches.Domain.Catalog.Level(levelId, $"{prefix}{levelId}", "مستوى", "Level", levelId));
            try
            {
                await db.SaveChangesAsync();
                return levelId;
            }
            catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
            {
                db.ChangeTracker.Clear();
            }
        }

        throw new InvalidOperationException("Could not find a free level id after 10 attempts.");
    }

    private static readonly Regex AntiforgeryTokenPattern = new(
        "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");

    private async Task<HttpClient> StudentsAdminClientAsync(string label)
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

            email = $"standing-{label}-{Guid.NewGuid():N}@test.mvteaches.local";
            var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
            var created = await userManager.CreateAsync(user, Password);
            Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
            await userManager.AddToRoleAsync(user, RoleNames.Admin);
            await userManager.AddClaimAsync(user, new Claim(PermissionKeys.ClaimType, PermissionKeys.StudentsView));
        }

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
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

    /// <summary>The owner's row, seeded exactly: a student who has bought a
    /// package and not paid for it, so the only subscription they hold is a
    /// Draft.</summary>
    private async Task<(long StudentId, string FullName)> SeedStudentOwingForAPackageAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
        var subscriptions = scope.ServiceProvider.GetRequiredService<ISubscriptionService>();

        var countryId = await db.Countries.OrderBy(c => c.Id).Select(c => c.Id).FirstAsync();
        var course = new Course($"STAND-{NextId()}", "دورة", "Standing Course");
        db.Courses.Add(course);

        await db.SaveChangesAsync();
        var levelId = await SeedLevelAsync(db, "ST");

        var fullName = $"Standing Student {NextId()}";
        var student = new Student(countryId, fullName, new LocalDate(2010, 5, 5), userId: null);
        db.Students.Add(student);
        await db.SaveChangesAsync();

        var now = SystemClock.Instance.GetCurrentInstant();
        db.StudentLevels.Add(new StudentLevel(student.Id, course.Id, levelId, 1L, AssignedByRole.Admin,
            LevelAssignmentSource.AdminOverride, null, "seed", now));
        await db.SaveChangesAsync();

        var plan = await subscriptions.CreatePricingPlanAsync(countryId, course.Id, levelId, null,
            SessionType.Group, 10, 600, new Money(60m, "JOD"), 30, now.InUtc().Date, 1L, CancellationToken.None);

        var purchase = await subscriptions.PurchaseFromPlanAsync(student.Id, plan.PricingPlanId, 1L,
            SubscriptionOrigin.GuardianPurchase, isAdminInitiated: true, CancellationToken.None);
        Assert.Equal(PurchaseFromPlanOutcome.Purchased, purchase.Outcome);

        var draft = await db.Subscriptions.SingleAsync(s => s.Id == purchase.SubscriptionId);
        Assert.Equal(SubscriptionStatus.Draft, draft.Status); // the state the owner's row was in

        return (student.Id, fullName);
    }

    /// <summary>The contradiction, asserted directly: the row that says a
    /// payment is due must not also say there is no package.</summary>
    [Fact]
    public async Task A_student_with_an_unpaid_package_is_not_listed_as_having_no_package()
    {
        var (studentId, fullName) = await SeedStudentOwingForAPackageAsync();
        var client = await StudentsAdminClientAsync("draft");

        var html = await client.GetStringAsync("/Admin/Students?state=PaymentDue&culture=en");

        // The student really is on the filtered list — otherwise the rest of
        // this test would pass by looking at a page with no rows on it.
        Assert.Contains(fullName, html);

        var row = RowFor(html, fullName);
        Assert.Contains("Package awaiting payment", row);
        Assert.DoesNotContain("No package", row);
    }

    /// <summary>And the row offers the one action it needs: take the payment,
    /// on this student.</summary>
    [Fact]
    public async Task The_unpaid_package_row_links_straight_to_that_students_payments()
    {
        var (studentId, fullName) = await SeedStudentOwingForAPackageAsync();
        var client = await StudentsAdminClientAsync("draft-link");

        var html = await client.GetStringAsync("/Admin/Students?state=PaymentDue&culture=en");
        var row = RowFor(html, fullName);

        Assert.Contains($"/Admin/Payments?studentId={studentId}", row);
    }

    /// <summary>A student who has never bought anything still reads "no
    /// package" — the change must not simply relabel everyone.</summary>
    [Fact]
    public async Task A_student_who_never_bought_anything_still_reads_no_package()
    {
        string fullName;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
            var countryId = await db.Countries.OrderBy(c => c.Id).Select(c => c.Id).FirstAsync();
            fullName = $"No Package Student {NextId()}";
            db.Students.Add(new Student(countryId, fullName, new LocalDate(2010, 8, 8), userId: null));
            await db.SaveChangesAsync();
        }

        var client = await StudentsAdminClientAsync("nopackage");
        var html = await client.GetStringAsync("/Admin/Students?culture=en");
        var row = RowFor(html, fullName);

        Assert.Contains("No package", row);
        Assert.DoesNotContain("Package awaiting payment", row);
    }

    /// <summary>The one table row that names this student. Asserting over the
    /// whole page would let another test's row satisfy the assertion.</summary>
    private static string RowFor(string html, string fullName)
    {
        var nameAt = html.IndexOf(fullName, StringComparison.Ordinal);
        Assert.True(nameAt >= 0, $"No row for {fullName} on the page.");

        var rowStart = html.LastIndexOf("<tr", nameAt, StringComparison.Ordinal);
        var rowEnd = html.IndexOf("</tr>", nameAt, StringComparison.Ordinal);
        Assert.True(rowStart >= 0 && rowEnd > rowStart, "Could not isolate the row.");
        return html[rowStart..rowEnd];
    }
}
