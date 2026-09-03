using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.People;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;
using Xunit;

namespace MVTeaches.Tests.Web;

[Collection(nameof(DatabaseCollection))]
public class LocalizationAndShellTests : IClassFixture<LocalizationAndShellTests.Factory>, IAsyncLifetime
{
    private static readonly Regex AntiforgeryTokenPattern = new(
        "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");

    private const string Password = "CorrectHorse123!";
    private const string AdminEmail = "shell-admin@test.mvteaches.local";
    private const string TeacherEmail = "shell-teacher@test.mvteaches.local";
    private const string GuardianEmail = "shell-guardian@test.mvteaches.local";
    private const string StudentEmail = "shell-student@test.mvteaches.local";

    private readonly Factory _factory;

    public LocalizationAndShellTests(TestDatabaseFixture fixture, Factory factory)
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__MvTeaches", fixture.ConnectionString);
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
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
        await EnsureUserAsync(userManager, StudentEmail, RoleNames.Student);

        // Security review 2026-09-02/2026-09-03 (Stage 1 + Stage 2A/2B admin
        // permissions): this file's AdminEmail account exercises the FULL
        // Subscriptions, Payments, Students, Teachers, and Schedules screens
        // (localization text on every form, plus recording a payment) — it
        // represents "an ordinary admin using the page", not a
        // permission-restriction scenario (that is ChangePasswordTests'
        // sibling, AdminPermissionTests), so it gets full access to those
        // screens rather than View-only — in particular Students.Manage,
        // since Admin_students_page_renders_... asserts on the "Register a
        // guardian only" / "Register a student only" correction-card text;
        // Teachers.Manage, since Admin_teachers_page_renders_... asserts on
        // the "Register a teacher" form and "How much is this teacher
        // paid?" pay-rate card; and Schedules.Manage, since
        // Admin_schedules_page_renders_... asserts on the "Create a weekly
        // class" tab, all of which only a Manage-holding admin sees.
        // Idempotent, same reasoning as AuthorizationTests' own copy of
        // this fix.
        var adminUser = await userManager.FindByEmailAsync(AdminEmail);
        if (adminUser is not null)
        {
            var existingKeys = (await userManager.GetClaimsAsync(adminUser)).Select(c => c.Value).ToHashSet();
            foreach (var key in new[]
            {
                PermissionKeys.PaymentsView, PermissionKeys.PaymentsConfirm,
                PermissionKeys.SubscriptionsView, PermissionKeys.SubscriptionsManage,
                PermissionKeys.StudentsView, PermissionKeys.StudentsManage,
                PermissionKeys.TeachersView, PermissionKeys.TeachersManage,
                PermissionKeys.SchedulesView, PermissionKeys.SchedulesManage,
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

    [Fact]
    public async Task New_visitor_gets_arabic_rtl_public_landing_page()
    {
        var client = CreateClient();
        var body = await client.GetStringAsync("/");
        var decodedBody = WebUtility.HtmlDecode(body);

        Assert.Contains("<html lang=\"ar-JO\" dir=\"rtl\">", body);
        Assert.Contains("دروس أونلاين", decodedBody);
        Assert.DoesNotContain("Learn about", body);
    }

    [Fact]
    public async Task English_culture_switch_persists_cookie_and_rejects_open_redirects()
    {
        var client = CreateClient();
        var home = await client.GetStringAsync("/");
        var token = AntiforgeryTokenPattern.Match(home).Groups[1].Value;

        var response = await client.PostAsync("/culture", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["culture"] = "en",
            ["returnUrl"] = "https://evil.example/path",
        }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.ToString());
        Assert.Contains(".AspNetCore.Culture", response.Headers.GetValues("Set-Cookie").Single());
    }

    [Fact]
    public async Task Query_culture_sets_ltr_english_layout()
    {
        var client = CreateClient();
        var body = await client.GetStringAsync("/?culture=en");

        Assert.Contains("<html lang=\"en\" dir=\"ltr\">", body);
        Assert.Contains("Online lessons organized around real student progress.", body);
    }

    [Fact]
    public async Task Teacher_navigation_excludes_admin_links()
    {
        var client = await CreateAuthenticatedClientAsync(TeacherEmail);
        var body = await client.GetStringAsync("/Teacher/MySessions?culture=en");

        Assert.Contains("Teacher workspace", body);
        Assert.Contains("Publish Slots", body);
        Assert.DoesNotContain("Students and Guardians", body);
        Assert.DoesNotContain("/Admin/Students", body);
    }

    [Fact]
    public async Task Admin_subscriptions_page_localizes_system_owned_labels()
    {
        var client = await CreateAuthenticatedClientAsync(AdminEmail);
        var response = await client.GetAsync("/Admin/Subscriptions?culture=ar-JO");
        var body = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Page copy was reworded after this test was written ("Pricing plans
        // and subscriptions" -> "Packages and subscriptions", etc.) — the
        // strings below match the current screen; the resx translations
        // themselves were already correct and unchanged.
        Assert.Contains("الباقات والاشتراكات", body);
        Assert.Contains("نشر سعر باقة جديد", body);
        Assert.Contains("منح طالبًا باقة مدفوعة", body);
        Assert.Contains("نوع الحصة", body);
        Assert.Contains("جماعية", body);
        Assert.DoesNotContain("Publish a new package price", body);
        Assert.DoesNotContain("Give a student a package they paid for", body);
        Assert.DoesNotContain("Session type", body);
    }

    [Theory]
    [InlineData(AdminEmail, "/Admin/Dashboard")]
    [InlineData(TeacherEmail, "/Teacher/MySessions")]
    [InlineData(GuardianEmail, "/Guardian/MyChildren")]
    [InlineData(StudentEmail, "/Student/MySessions")]
    public async Task Authenticated_root_redirects_to_the_role_workspace(string email, string expectedPath)
    {
        var client = await CreateAuthenticatedClientAsync(email);
        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(expectedPath, response.Headers.Location?.ToString());
    }

    /// <summary>Owner decision 2026-08-30 (bilingual amount/date entry): a
    /// real, reproduced bug this session found — under ar-JO,
    /// CultureInfo.CurrentCulture used Arabic decimal/group separators, so
    /// ASP.NET Core's default model binder rejected a perfectly valid
    /// "3.500" submitted from an HTML5 number input (which always emits a
    /// period, regardless of the browser's own locale) as invalid. Fixed by
    /// decoupling SupportedCultures (pinned to en-US) from SupportedUICultures
    /// (still ar-JO/en) in Program.cs. This proves the fix, not just the
    /// display-formatting side already covered elsewhere.</summary>
    private static long _cultureTestIdSeed = 91_000_000;

    private static string TwoLetterCode(long seed)
    {
        var n = (int)(seed % 676);
        return string.Concat((char)('A' + n / 26), (char)('A' + n % 26));
    }

    // Same retry-on-collision fix as SessionCancellationServiceTests/
    // PaymentReceiptAccessTests: the 2-letter code space (676 combinations)
    // is shared with every other test class in the same run.
    private static async Task<int> SeedCountryAsync(MvTeachesDbContext db)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var id = (int)Interlocked.Increment(ref _cultureTestIdSeed);
            db.Countries.Add(new Country(id, TwoLetterCode(id), "دولة", "Country", "JOD", "+962", "Asia/Amman"));
            try
            {
                await db.SaveChangesAsync();
                return id;
            }
            catch (Microsoft.EntityFrameworkCore.DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
            {
                db.ChangeTracker.Clear();
            }
        }

        throw new InvalidOperationException("Could not find a free 2-letter country code after 10 attempts.");
    }

    [Fact]
    public async Task A_decimal_amount_submitted_from_an_arabic_language_page_still_binds_correctly()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();

        var countryId = await SeedCountryAsync(db);
        var student = new Student(countryId, "Bilingual Entry Student", new LocalDate(2012, 1, 1), userId: null);
        db.Students.Add(student);
        await db.SaveChangesAsync();

        var client = await CreateAuthenticatedClientAsync(AdminEmail);
        var page = await client.GetStringAsync("/Admin/Payments?culture=ar-JO");
        var token = AntiforgeryTokenPattern.Match(page).Groups[1].Value;

        var form = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["NewPayment.StudentId"] = student.Id.ToString(),
            ["NewPayment.Amount"] = "3.500",
            ["NewPayment.Currency"] = "JOD",
            ["NewPayment.Method"] = "BankTransfer",
        };
        var response = await client.PostAsync("/Admin/Payments?handler=Record&culture=ar-JO", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payment = await db.Payments.Where(p => p.StudentId == student.Id).OrderByDescending(p => p.Id).FirstOrDefaultAsync();
        Assert.NotNull(payment);
        Assert.Equal(3.500m, payment!.Amount.Amount);
    }

    /// <summary>Owner instruction (Part 4): "تطابق عدد مفاتيح الترجمة وحده لا
    /// يكفي" — a resx key-count match proves nothing about whether a page
    /// actually renders translated text. This asserts REAL Arabic content
    /// appears under ar-JO and REAL English content appears under en, for a
    /// page fully rewritten this turn (PublishSlots) — not just that some
    /// @T[...] call exists somewhere in the source.</summary>
    [Fact]
    public async Task PublishSlots_page_renders_real_translated_content_in_both_languages()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var teacherUser = await userManager.FindByEmailAsync(TeacherEmail);
        if (!await db.Teachers.AnyAsync(t => t.UserId == teacherUser!.Id))
        {
            db.Teachers.Add(new Teacher(teacherUser!.Id, "Shell Teacher", "Asia/Amman"));
            await db.SaveChangesAsync();
        }

        var client = await CreateAuthenticatedClientAsync(TeacherEmail);

        var arabicBody = WebUtility.HtmlDecode(await client.GetStringAsync("/Teacher/PublishSlots?culture=ar-JO"));
        Assert.Contains("نشر الأوقات", arabicBody); // nav label
        Assert.Contains("موعد جديد", arabicBody);
        Assert.Contains("وقت البدء", arabicBody);
        Assert.Contains("مواعيدك القادمة", arabicBody);
        Assert.DoesNotContain("New slot", arabicBody);
        Assert.DoesNotContain("Start time", arabicBody);

        var englishBody = await client.GetStringAsync("/Teacher/PublishSlots?culture=en");
        Assert.Contains("New slot", englishBody);
        Assert.Contains("Start time", englishBody);
        Assert.Contains("Your upcoming slots", englishBody);
        Assert.DoesNotContain("موعد جديد", englishBody);
        Assert.DoesNotContain("وقت البدء", englishBody);
    }

    /// <summary>Owner instruction (Part 2, final push): the five remaining
    /// large Admin pages (Students, StudentDetails, Teachers, Schedules,
    /// PlacementTests) were localized this turn — this proves real Arabic
    /// and real English content renders on each, not just that @T[...] calls
    /// exist in the source (a resx key-count match alone was explicitly
    /// rejected as sufficient proof).</summary>
    [Fact]
    public async Task Admin_students_page_renders_real_translated_content_in_both_languages()
    {
        var client = await CreateAuthenticatedClientAsync(AdminEmail);

        // The register's header was shortened to plain "Students" and the
        // stand-alone guardian/student registration cards were reworded to
        // "... only" (the everyday path is now "Register a family step by
        // step" further up) — the strings below match the current screen.
        var arabicBody = WebUtility.HtmlDecode(await client.GetStringAsync("/Admin/Students?culture=ar-JO"));
        Assert.Contains("كل طالب يعرفه المركز", arabicBody);
        Assert.Contains("تسجيل وليّ أمر فقط", arabicBody);
        Assert.Contains("تسجيل طالب فقط", arabicBody);
        Assert.DoesNotContain("Register a guardian only", arabicBody);

        var englishBody = await client.GetStringAsync("/Admin/Students?culture=en");
        Assert.Contains("Every student the centre knows about", englishBody);
        Assert.Contains("Register a guardian only", englishBody);
        Assert.DoesNotContain("تسجيل وليّ أمر فقط", englishBody);
    }

    [Fact]
    public async Task Admin_teachers_page_renders_real_translated_content_in_both_languages()
    {
        var client = await CreateAuthenticatedClientAsync(AdminEmail);

        // "Set a pay rate" was reworded to the question-style heading "How
        // much is this teacher paid?" — the string below matches the
        // current screen; the resx translation was already correct.
        var arabicBody = WebUtility.HtmlDecode(await client.GetStringAsync("/Admin/Teachers?culture=ar-JO"));
        Assert.Contains("تسجيل معلم", arabicBody);
        Assert.Contains("كم يتقاضى هذا المعلم؟", arabicBody);
        Assert.DoesNotContain("Register a teacher", arabicBody);

        var englishBody = await client.GetStringAsync("/Admin/Teachers?culture=en");
        Assert.Contains("Register a teacher", englishBody);
        Assert.Contains("How much is this teacher paid?", englishBody);
        Assert.DoesNotContain("تسجيل معلم", englishBody);
    }

    [Fact]
    public async Task Admin_schedules_page_renders_real_translated_content_in_both_languages()
    {
        var client = await CreateAuthenticatedClientAsync(AdminEmail);

        // "Recurring schedules" / "Create a recurring schedule" were
        // reworded to "Weekly schedules" / "Create a weekly class" — the
        // strings below match the current screen.
        var arabicBody = WebUtility.HtmlDecode(await client.GetStringAsync("/Admin/Schedules?culture=ar-JO"));
        Assert.Contains("الجداول الأسبوعية", arabicBody);
        Assert.Contains("إنشاء صف أسبوعي", arabicBody);
        Assert.Contains("تسجيل طالب", arabicBody);
        Assert.DoesNotContain("Weekly schedules", arabicBody);

        var englishBody = await client.GetStringAsync("/Admin/Schedules?culture=en");
        Assert.Contains("Weekly schedules", englishBody);
        Assert.Contains("Create a weekly class", englishBody);
        Assert.DoesNotContain("الجداول الأسبوعية", englishBody);
    }

    /// <summary>Also proves a server-generated POST message (not just a GET
    /// page load) renders in both languages, per the owner's explicit
    /// requirement.</summary>
    [Fact]
    public async Task Admin_placement_tests_page_renders_real_content_and_localizes_the_post_response_message()
    {
        var client = await CreateAuthenticatedClientAsync(AdminEmail);

        var rawArabicBody = await client.GetStringAsync("/Admin/PlacementTests?culture=ar-JO");
        var arabicBody = WebUtility.HtmlDecode(rawArabicBody);
        // "Create a new draft version" was reworded to "Start a new draft
        // test" / button "Create draft" — the strings below match the
        // current screen.
        Assert.Contains("اختبارات تحديد المستوى", arabicBody);
        Assert.Contains("ابدأ مسودة اختبار جديدة", arabicBody);
        Assert.DoesNotContain("Placement Tests", arabicBody);

        var token = AntiforgeryTokenPattern.Match(rawArabicBody).Groups[1].Value;

        var response = await client.PostAsync("/Admin/PlacementTests?handler=CreateVersion&culture=ar-JO", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["NewVersion.Title"] = "اختبار تحديد المستوى - إنجليزي",
        }));
        var postBody = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Current message key is "Draft version #{0} created." — its resx
        // translation ("تم إنشاء المسودة...") rather than the old wording.
        Assert.Contains("تم إنشاء المسودة", postBody);

        var englishBody = await client.GetStringAsync("/Admin/PlacementTests?culture=en");
        Assert.Contains("Placement Tests", englishBody);
        Assert.Contains("Start a new draft test", englishBody);
    }

    /// <summary>Owner instruction (deeper pass, follow-up to Part 2): proves
    /// an Application/Infrastructure-layer message — PlacementTestAdminService's
    /// own publish-validation errors, not a Web PageModel literal — also
    /// renders in the request's actual language. This is the service-layer
    /// half of the localization sweep: the message text lives in
    /// MVTeaches.Infrastructure's own resx (InfrastructureResource), not the
    /// Web project's SharedResource, proving IStringLocalizer resolves
    /// correctly across project boundaries at request time.</summary>
    [Fact]
    public async Task An_infrastructure_layer_validation_message_localizes_through_a_real_request()
    {
        var client = await CreateAuthenticatedClientAsync(AdminEmail);

        var rawPage = await client.GetStringAsync("/Admin/PlacementTests?culture=ar-JO");
        var token = AntiforgeryTokenPattern.Match(rawPage).Groups[1].Value;

        var createResponse = await client.PostAsync("/Admin/PlacementTests?handler=CreateVersion&culture=ar-JO", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["NewVersion.Title"] = "نسخة بلا أسئلة",
        }));
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);

        // Reading the new version's id back from the database rather than
        // scraping it out of the rendered HTML — simpler and not coupled to
        // markup structure.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
        var version = await db.PlacementTestVersions.Where(v => v.Title == "نسخة بلا أسئلة").OrderByDescending(v => v.Id).FirstAsync();

        var publishResponse = await client.PostAsync($"/Admin/PlacementTests?handler=Publish&culture=ar-JO", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["versionId"] = version.Id.ToString(),
        }));
        var arabicBody = WebUtility.HtmlDecode(await publishResponse.Content.ReadAsStringAsync());
        Assert.Contains("مطلوب سؤال واحد على الأقل", arabicBody); // "At least one question is required." — from Infrastructure's own resx
        Assert.Contains("مطلوب نطاق درجات واحد على الأقل", arabicBody); // "At least one score range is required."
        Assert.DoesNotContain("At least one question is required", arabicBody);

        var englishToken = AntiforgeryTokenPattern.Match(await client.GetStringAsync("/Admin/PlacementTests?culture=en")).Groups[1].Value;
        var englishPublish = await client.PostAsync("/Admin/PlacementTests?handler=Publish&culture=en", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = englishToken,
            ["versionId"] = version.Id.ToString(),
        }));
        var englishBody = await englishPublish.Content.ReadAsStringAsync();
        Assert.Contains("At least one question is required.", englishBody);
    }

    /// <summary>Owner instruction (Part 3): "اختبار إدخال المبالغ والتواريخ
    /// وإرسالها وحفظها تحت اللغتين؛ اختبار تنسيق العرض وحده غير كافٍ" — this
    /// exercises Admin/FinancialReport's expense form (decimal amount +
    /// DateOnly) submitted from an Arabic-culture page, and asserts what was
    /// actually stored in PostgreSQL, not merely what the page displays back.</summary>
    [Fact]
    public async Task An_operating_expense_submitted_from_an_arabic_language_page_binds_and_stores_correctly()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
        var countryId = await SeedCountryAsync(db);

        var client = await CreateAuthenticatedClientAsync(AdminEmail);
        var page = await client.GetStringAsync("/Admin/FinancialReport?culture=ar-JO");
        var token = AntiforgeryTokenPattern.Match(page).Groups[1].Value;

        var form = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["NewExpense.CountryId"] = countryId.ToString(),
            ["NewExpense.Category"] = "Rent",
            ["NewExpense.Amount"] = "125.750",
            ["NewExpense.Currency"] = "JOD",
            ["NewExpense.IncurredOn"] = "2026-03-15",
        };
        var response = await client.PostAsync("/Admin/FinancialReport?handler=RecordExpense&culture=ar-JO", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var postBody = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains("تم تسجيل المصروف", postBody);

        var expense = await db.OperatingExpenses
            .Where(e => e.CountryId == countryId && e.Category == "Rent")
            .OrderByDescending(e => e.Id)
            .FirstOrDefaultAsync();
        Assert.NotNull(expense);
        Assert.Equal(125.750m, expense!.Amount.Amount);
        Assert.Equal(new LocalDate(2026, 3, 15), expense.IncurredOn);
    }

    /// <summary>A real gap this sweep found: Login.cshtml.cs's two failure
    /// messages ("locked out" / "invalid credentials") were hardcoded English
    /// literals never routed through the localizer, even though Login.cshtml
    /// itself was already localized — the page's OWN server-generated POST
    /// response was the one place still leaking English under Arabic. Proves
    /// the fix in both languages.</summary>
    [Fact]
    public async Task Login_failure_message_is_localized_in_both_languages()
    {
        var client = CreateClient();

        var arabicLoginPage = await client.GetStringAsync("/Account/Login?culture=ar-JO");
        var arabicToken = AntiforgeryTokenPattern.Match(arabicLoginPage).Groups[1].Value;
        var arabicResponse = await client.PostAsync("/Account/Login?culture=ar-JO", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = arabicToken,
            ["Input.Email"] = "no-such-user@test.mvteaches.local",
            ["Input.Password"] = "WrongPassword123!",
        }));
        var arabicBody = WebUtility.HtmlDecode(await arabicResponse.Content.ReadAsStringAsync());
        Assert.Contains("البريد الإلكتروني أو كلمة المرور غير صحيحة", arabicBody);
        Assert.DoesNotContain("Invalid email or password", arabicBody);

        var englishLoginPage = await client.GetStringAsync("/Account/Login?culture=en");
        var englishToken = AntiforgeryTokenPattern.Match(englishLoginPage).Groups[1].Value;
        var englishResponse = await client.PostAsync("/Account/Login?culture=en", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = englishToken,
            ["Input.Email"] = "no-such-user@test.mvteaches.local",
            ["Input.Password"] = "WrongPassword123!",
        }));
        var englishBody = await englishResponse.Content.ReadAsStringAsync();
        Assert.Contains("Invalid email or password.", englishBody);
    }

    private HttpClient CreateClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private async Task<HttpClient> CreateAuthenticatedClientAsync(string email)
    {
        var client = CreateClient();
        var loginPage = await client.GetStringAsync("/Account/Login");
        var token = AntiforgeryTokenPattern.Match(loginPage).Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token), "Could not find the antiforgery token on the login page.");

        var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Input.Email"] = email,
            ["Input.Password"] = Password,
        }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    private static async Task EnsureUserAsync(UserManager<ApplicationUser> userManager, string email, string role)
    {
        var user = await userManager.FindByEmailAsync(email);
        if (user is null)
        {
            user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
            var createResult = await userManager.CreateAsync(user, Password);
            Assert.True(createResult.Succeeded, string.Join("; ", createResult.Errors.Select(e => e.Description)));
        }

        if (!await userManager.IsInRoleAsync(user, role))
        {
            await userManager.AddToRoleAsync(user, role);
        }
    }

    public class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseEnvironment("Development");
    }
}
