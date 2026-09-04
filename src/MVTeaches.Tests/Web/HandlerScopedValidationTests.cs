using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MVTeaches.Domain.People;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;
using Xunit;

namespace MVTeaches.Tests.Web;

/// <summary>
/// Owner report 2026-09-05, from a real Local Staging run. Pressing "confirm
/// this student's registration" on /Admin/Students worked — the student really
/// was marked verified — and the screen simultaneously showed thirteen errors
/// belonging to forms the admin had never touched: "enter an email address",
/// "choose a level", "write the reason for this decision", "enter a temporary
/// password", and so on.
///
/// <para>Razor Pages binds and auto-validates EVERY [BindProperty] group on a
/// page for EVERY POST, whichever named handler runs. A page with five forms
/// therefore arrives at its Verify handler with four forms' worth of unfilled
/// [Required] errors already in ModelState, and returning Page() renders them.
/// The form handlers on these pages had always cleared ModelState before
/// validating their own group; the ACTION handlers — the ones whose input is a
/// single id in a hidden field — had not. An audit found twenty such handlers
/// across six pages, including the two the owner hit (Students.Verify and
/// PlacementTests.RejectRetake).</para>
///
/// <para>These run over real HTTP because the defect is in what the rendered
/// page says after a successful POST, which is precisely what a service-level
/// test cannot see.</para>
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class HandlerScopedValidationTests : IClassFixture<AuthorizationTests.Factory>
{
    private readonly AuthorizationTests.Factory _factory;
    private const string Password = "CorrectHorse123!";

    public HandlerScopedValidationTests(TestDatabaseFixture fixture, AuthorizationTests.Factory factory)
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__MvTeaches", fixture.ConnectionString);
        _factory = factory;
    }

    private static readonly Regex AntiforgeryTokenPattern = new(
        "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");

    // The app renders Arabic by default, so every request here pins ?culture=en
    // (the same switch LocalizationAndShellTests uses). These assertions are
    // about WHICH messages appear, and pinning one language is what lets them
    // name the message instead of matching a translation.
    private HttpClient CreateClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private async Task<HttpClient> AdminClientAsync(string label, params string[] permissions)
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

            email = $"hsv-{label}-{Guid.NewGuid():N}@test.mvteaches.local";
            var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
            var created = await userManager.CreateAsync(user, Password);
            Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
            await userManager.AddToRoleAsync(user, RoleNames.Admin);
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

    private async Task<long> SeedUnverifiedStudentAsync(string label)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
        var countryId = await db.Countries.OrderBy(c => c.Id).Select(c => c.Id).FirstAsync();
        var student = new Student(countryId, $"Validation Scope {label}", new LocalDate(2011, 4, 2), userId: null);
        db.Students.Add(student);
        await db.SaveChangesAsync();
        Assert.Equal(StudentStatus.PendingVerification, student.Status);
        return student.Id;
    }

    /// <summary>The exact messages the owner saw. Each belongs to a DIFFERENT
    /// form on the page — create a student, register a guardian, link a
    /// guardian, assign a level — and none of them belongs to the confirm
    /// button. Asserted in English because these tests run under the
    /// invariant culture; the Arabic strings are the same keys.</summary>
    private static readonly string[] OtherFormsErrors =
    {
        "Enter an email address.",
        "This is not a valid email address.",
        "Write the reason for this decision.",
        "Choose a level.",
        "Choose a course.",
        "Enter the full name.",
        "Enter a temporary password.",
        "The password must be at least 8 characters.",
        "Choose a country.",
        "Choose a guardian.",
        "Enter the date of birth.",
        "Enter a phone number.",
        "This is not a valid phone number.",
    };

    /// <summary>The messages the page actually SHOWS, which is a different
    /// thing from the messages it mentions. Every [Required] message is also
    /// emitted into a data-val-required attribute on every page load, error or
    /// not — that is client-side validation metadata, not a complaint — so a
    /// plain substring search over the HTML can never tell the bug from the
    /// normal case. Only these two elements are rendered because ModelState
    /// holds an error: the per-field span, and the summary list.</summary>
    private static readonly Regex RenderedFieldError = new(
        "<span[^>]*class=\"[^\"]*field-validation-error[^\"]*\"[^>]*>(.*?)</span>",
        RegexOptions.Singleline);

    private static readonly Regex RenderedSummary = new(
        "<div[^>]*class=\"[^\"]*validation-summary-errors[^\"]*\"[^>]*>(.*?)</div>",
        RegexOptions.Singleline);

    private static string RenderedErrors(string html)
    {
        var shown = RenderedFieldError.Matches(html).Select(m => m.Groups[1].Value)
            .Concat(RenderedSummary.Matches(html).Select(m => m.Groups[1].Value));
        return string.Join(" | ", shown);
    }

    private static void AssertNoOtherFormErrors(string html)
    {
        var shown = RenderedErrors(html);
        var leaked = OtherFormsErrors.Where(shown.Contains).ToList();
        Assert.True(leaked.Count == 0,
            "The page displayed errors belonging to other forms: " + string.Join(" | ", leaked));
    }

    /// <summary>The owner's own scenario: confirming a registration must ask
    /// for nothing but the student, and must not report a single field of the
    /// email / password / level / course / phone forms it shares a page
    /// with.</summary>
    [Fact]
    public async Task Confirming_a_registration_asks_for_no_other_forms_fields()
    {
        var studentId = await SeedUnverifiedStudentAsync("verify");
        var client = await AdminClientAsync("verify", PermissionKeys.StudentsView, PermissionKeys.StudentsManage);

        var page = await client.GetStringAsync("/Admin/Students?culture=en");
        var response = await client.PostAsync("/Admin/Students?handler=Verify&culture=en", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AntiforgeryTokenPattern.Match(page).Groups[1].Value,
            ["studentId"] = studentId.ToString(),
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("Student marked verified.", html);
        AssertNoOtherFormErrors(html);

        // And it really did the thing, rather than merely staying quiet.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
        var student = await db.Students.SingleAsync(s => s.Id == studentId);
        Assert.NotEqual(StudentStatus.PendingVerification, student.Status);
    }

    /// <summary>The other half of the owner's rule: when this handler's OWN
    /// input is wrong, the message is about that input — not a 500, and still
    /// not the other forms' fields.</summary>
    [Fact]
    public async Task Confirming_with_an_unknown_student_reports_only_that()
    {
        var client = await AdminClientAsync("verify-bad", PermissionKeys.StudentsView, PermissionKeys.StudentsManage);

        var page = await client.GetStringAsync("/Admin/Students?culture=en");
        var response = await client.PostAsync("/Admin/Students?handler=Verify&culture=en", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AntiforgeryTokenPattern.Match(page).Groups[1].Value,
            ["studentId"] = "0", // the value a missing hidden field binds to
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("that student was not found", html);
        Assert.DoesNotContain("Student marked verified.", html);
        AssertNoOtherFormErrors(html);
    }

    /// <summary>The owner's second report, same root cause on a different page:
    /// rejecting a retake request reported "enter a title", "choose a course",
    /// "write the question" — three separate forms on /Admin/PlacementTests
    /// that the admin never opened.</summary>
    [Fact]
    public async Task Rejecting_a_retake_request_asks_for_no_other_forms_fields()
    {
        long studentId;
        long retakeRequestId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
            var countryId = await db.Countries.OrderBy(c => c.Id).Select(c => c.Id).FirstAsync();
            var student = new Student(countryId, "Retake Scope Student", new LocalDate(2009, 6, 6), userId: null);
            db.Students.Add(student);
            await db.SaveChangesAsync();
            studentId = student.Id;

            var request = new MVTeaches.Domain.Placement.PlacementRetakeRequest(
                studentId, 1L, SystemClock.Instance.GetCurrentInstant());
            db.PlacementRetakeRequests.Add(request);
            await db.SaveChangesAsync();
            retakeRequestId = request.Id;
        }

        var client = await AdminClientAsync("retake", PermissionKeys.PlacementTestsView, PermissionKeys.PlacementTestsManage);

        var page = await client.GetStringAsync("/Admin/PlacementTests?culture=en");
        var response = await client.PostAsync("/Admin/PlacementTests?handler=RejectRetake&culture=en", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AntiforgeryTokenPattern.Match(page).Groups[1].Value,
            ["retakeRequestId"] = retakeRequestId.ToString(),
            ["reason"] = "The first result stands.",
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        // This page's own three forms: a test version, a question, a score range.
        var shown = RenderedErrors(html);
        Assert.DoesNotContain("Enter a title.", shown);
        Assert.DoesNotContain("Write the question.", shown);
        Assert.DoesNotContain("Choose a course.", shown);
        Assert.DoesNotContain("Choose a level.", shown);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
        var rejected = await verifyDb.PlacementRetakeRequests.SingleAsync(r => r.Id == retakeRequestId);
        Assert.NotEqual(MVTeaches.Domain.Placement.PlacementRetakeStatus.Pending, rejected.Status);
    }
}
