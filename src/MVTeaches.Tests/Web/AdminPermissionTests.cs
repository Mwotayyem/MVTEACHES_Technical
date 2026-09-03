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
using MVTeaches.Domain.Scheduling;
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

    /// <summary>Stage 2: a bare student with no package/payment/note of any
    /// kind — the default StudentStatus is PendingVerification (see
    /// Student's own constructor), which is exactly the state
    /// OnPostVerifyAsync needs to act on.</summary>
    private async Task<long> SeedStudentAsync(string label)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();

        var countryId = await SeedCountryAsync(db);
        var student = new Student(countryId, $"Permission Test Student {label}", new LocalDate(2005, 1, 1), userId: null);
        db.Students.Add(student);
        await db.SaveChangesAsync();
        return student.Id;
    }

    /// <summary>Writes a StudentNote directly, bypassing OnPostAddNoteAsync —
    /// so a "does this admin see an EXISTING note" test never depends on the
    /// very handler a sibling test is busy proving is blocked.</summary>
    private async Task SeedStudentNoteAsync(long studentId, string text)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
        db.StudentNotes.Add(new StudentNote(studentId, StudentNoteCategory.Learning, text,
            authorUserId: 1, authorName: "Seed", SystemClock.Instance.GetCurrentInstant()));
        await db.SaveChangesAsync();
    }

    /// <summary>A bare login row with no role/password of its own — Teacher's
    /// own UserId column just needs a real AspNetUsers row to satisfy the FK;
    /// nothing here ever signs in as this user. Same pattern
    /// ScheduleGenerationServiceTests already uses for the same reason.</summary>
    private static async Task<long> SeedBareUserAsync(MvTeachesDbContext db, string label)
    {
        var user = new ApplicationUser
        {
            UserName = $"{label}-{Guid.NewGuid():N}",
            NormalizedUserName = $"{label}-{Guid.NewGuid():N}".ToUpperInvariant(),
            Email = $"{Guid.NewGuid():N}@test.mvteaches.local",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>Stage 2B: a real, active Teacher row with no rate/level/
    /// meeting-connection of its own — exactly what OnPostDeactivateAsync
    /// needs to act on.</summary>
    private async Task<long> SeedTeacherAsync(string label)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
        var userId = await SeedBareUserAsync(db, label);
        var teacher = new Teacher(userId, $"Permission Test Teacher {label}", "Asia/Amman");
        db.Teachers.Add(teacher);
        await db.SaveChangesAsync();
        return teacher.Id;
    }

    /// <summary>Stage 2B: a real, Active RecurringSchedule row — enough for
    /// OnPostPauseAsync/OnPostResumeAsync to act on, without needing the
    /// teacher-meeting-connection/level-authorization graph OnPostCreateAsync
    /// itself would require.</summary>
    private async Task<long> SeedRecurringScheduleAsync(string label)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();

        var countryId = await SeedCountryAsync(db);
        var courseCode = $"PERMTEST-{NextId()}";
        db.Courses.Add(new Course(courseCode, "دورة", "Course"));
        var levelId = (int)NextId();
        db.Levels.Add(new Level(levelId, $"L{levelId}", "مستوى", "Level", levelId));
        var ageGroupId = (int)NextId();
        db.AgeGroups.Add(new AgeGroup(ageGroupId, $"A{ageGroupId}", 5, 12, true));
        var userId = await SeedBareUserAsync(db, label);
        var teacher = new Teacher(userId, $"Permission Test Teacher {label}", "Asia/Amman");
        db.Teachers.Add(teacher);
        await db.SaveChangesAsync();
        var courseId = await db.Courses.Where(c => c.Code == courseCode).Select(c => c.Id).FirstAsync();

        var schedule = new RecurringSchedule(countryId, courseId, levelId, ageGroupId, teacher.Id,
            new[] { NodaTime.IsoDayOfWeek.Monday }, new LocalTime(18, 0), 60, "Asia/Amman",
            new LocalDate(2026, 1, 5), capacity: 4, createdByUserId: 0);
        db.RecurringSchedules.Add(schedule);
        await db.SaveChangesAsync();
        return schedule.Id;
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

    // =================================================================
    // Stage 2 (2026-09-03, Review Required — Authorization): Students,
    // StudentDetails, AssistedRegistration, and the Student Notes written
    // record inside StudentDetails. Same PermissionAuthorizationHandler,
    // same live-per-request DB check, same SystemAdmin bypass — these tests
    // exercise the four new keys the same way the block above exercised the
    // Stage 1 ones.
    // =================================================================

    // ---------------------------------------------------------------
    // 11. SystemAdmin: sees and acts on Students/StudentDetails/
    // AssistedRegistration with ZERO claims.
    // ---------------------------------------------------------------

    [Fact]
    public async Task SystemAdmin_views_and_manages_students_with_zero_permission_claims()
    {
        var email = await CreateUserAsync("sa-students", RoleNames.SystemAdmin);
        var client = await LoggedInClientAsync(CreateClient(), email);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Admin/Students")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Admin/AssistedRegistration")).StatusCode);

        var studentId = await SeedStudentAsync("sa-verify-target");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/Admin/StudentDetails/{studentId}")).StatusCode);

        var token = await GetAntiforgeryTokenAsync(client, "/Admin/Students");
        var response = await client.PostAsync("/Admin/Students?handler=Verify", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["studentId"] = studentId.ToString(),
        }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
        var verified = await db.Students.FirstAsync(s => s.Id == studentId);
        Assert.Equal(StudentStatus.PendingLevel, verified.Status);
    }

    // ---------------------------------------------------------------
    // 12. Plain Admin, zero claims: forbidden from all three Stage 2 pages.
    // ---------------------------------------------------------------

    [Fact]
    public async Task Admin_with_no_permission_claims_is_forbidden_from_students_pages()
    {
        var email = await CreateUserAsync("no-claims-students", RoleNames.Admin);
        var client = await LoggedInClientAsync(CreateClient(), email);

        Assert.NotEqual(HttpStatusCode.OK, (await client.GetAsync("/Admin/Students")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await client.GetAsync("/Admin/StudentDetails/1")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await client.GetAsync("/Admin/AssistedRegistration")).StatusCode);
    }

    // ---------------------------------------------------------------
    // 13. Admin with only Students.View: sees the register and the profile,
    // cannot mutate, and a direct POST writes nothing. AssistedRegistration
    // (Manage-gated even for GET) stays refused too.
    // ---------------------------------------------------------------

    [Fact]
    public async Task Admin_with_students_view_only_sees_pages_but_cannot_verify_a_student_and_writes_nothing()
    {
        var email = await CreateUserAsync("students-view-only", RoleNames.Admin);
        await GrantAsync(email, PermissionKeys.StudentsView);
        var client = await LoggedInClientAsync(CreateClient(), email);

        var studentId = await SeedStudentAsync("view-only-verify-target");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Admin/Students")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/Admin/StudentDetails/{studentId}")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await client.GetAsync("/Admin/AssistedRegistration")).StatusCode);

        var token = await GetAntiforgeryTokenAsync(client, "/Admin/Students");
        var response = await client.PostAsync("/Admin/Students?handler=Verify", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["studentId"] = studentId.ToString(),
        }));
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
        var stillPending = await db.Students.FirstAsync(s => s.Id == studentId);
        Assert.Equal(StudentStatus.PendingVerification, stillPending.Status); // Verify never touched it
    }

    // ---------------------------------------------------------------
    // 14. Admin with Students.View + Students.Manage: can actually verify a
    // real student, and can reach AssistedRegistration.
    // ---------------------------------------------------------------

    [Fact]
    public async Task Admin_with_students_manage_can_verify_a_student_and_open_assisted_registration()
    {
        var email = await CreateUserAsync("students-manage", RoleNames.Admin);
        await GrantAsync(email, PermissionKeys.StudentsView, PermissionKeys.StudentsManage);
        var client = await LoggedInClientAsync(CreateClient(), email);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Admin/AssistedRegistration")).StatusCode);

        var studentId = await SeedStudentAsync("manage-verify-target");
        var token = await GetAntiforgeryTokenAsync(client, "/Admin/Students");
        var response = await client.PostAsync("/Admin/Students?handler=Verify", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["studentId"] = studentId.ToString(),
        }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
        var verified = await db.Students.FirstAsync(s => s.Id == studentId);
        Assert.Equal(StudentStatus.PendingLevel, verified.Status);
    }

    // ---------------------------------------------------------------
    // 15. Admin without AssistedRegistration Manage cannot open it, and a
    // direct POST to one of its handlers is refused too — proving the
    // single page-level policy really does cover every handler, not only
    // the GET.
    // ---------------------------------------------------------------

    [Fact]
    public async Task Admin_with_students_view_only_cannot_post_to_assisted_registration()
    {
        var email = await CreateUserAsync("assisted-view-only", RoleNames.Admin);
        await GrantAsync(email, PermissionKeys.StudentsView);
        var client = await LoggedInClientAsync(CreateClient(), email);

        // Cannot even GET the page to fetch a real antiforgery token — a
        // fabricated one is used instead, since the policy check happens
        // before the handler (or the antiforgery check) ever runs.
        var response = await client.PostAsync("/Admin/AssistedRegistration?handler=RegisterGuardian",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["NewGuardian.Email"] = $"permtest-{Guid.NewGuid():N}@test.mvteaches.local",
                ["NewGuardian.Password"] = Password,
                ["NewGuardian.FullName"] = "Should Not Be Created",
            }));
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
        Assert.False(await db.Guardians.AnyAsync(g => g.FullName == "Should Not Be Created"));
    }

    // ---------------------------------------------------------------
    // 16. Student Notes: without StudentNotes.View, an existing written note
    // is invisible — not merely un-editable.
    // ---------------------------------------------------------------

    [Fact]
    public async Task Admin_without_studentnotes_view_does_not_see_an_existing_written_note()
    {
        var email = await CreateUserAsync("notes-none", RoleNames.Admin);
        await GrantAsync(email, PermissionKeys.StudentsView);
        var client = await LoggedInClientAsync(CreateClient(), email);

        var studentId = await SeedStudentAsync("notes-hidden-target");
        var secretNoteText = $"Secret note {Guid.NewGuid():N}";
        await SeedStudentNoteAsync(studentId, secretNoteText);

        var body = await client.GetStringAsync($"/Admin/StudentDetails/{studentId}");
        Assert.DoesNotContain(secretNoteText, body);
    }

    // ---------------------------------------------------------------
    // 17. Student Notes: with StudentNotes.View but not Manage, an admin
    // sees the note but a direct POST to add one is refused and writes
    // nothing.
    // ---------------------------------------------------------------

    [Fact]
    public async Task Admin_with_studentnotes_view_only_sees_the_note_but_cannot_add_one()
    {
        var email = await CreateUserAsync("notes-view-only", RoleNames.Admin);
        await GrantAsync(email, PermissionKeys.StudentsView, PermissionKeys.StudentNotesView);
        var client = await LoggedInClientAsync(CreateClient(), email);

        var studentId = await SeedStudentAsync("notes-view-target");
        var existingNoteText = $"Visible note {Guid.NewGuid():N}";
        await SeedStudentNoteAsync(studentId, existingNoteText);

        var detailsPath = $"/Admin/StudentDetails/{studentId}";
        var body = await client.GetStringAsync(detailsPath);
        Assert.Contains(existingNoteText, body);

        var token = await GetAntiforgeryTokenAsync(client, detailsPath);
        var attemptedText = $"Should not be saved {Guid.NewGuid():N}";
        var response = await client.PostAsync($"/Admin/StudentDetails/{studentId}?handler=AddNote", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["id"] = studentId.ToString(),
            ["NewNote.Category"] = "Learning",
            ["NewNote.Text"] = attemptedText,
        }));
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
        Assert.False(await db.StudentNotes.AnyAsync(n => n.StudentId == studentId && n.Text == attemptedText));
        Assert.Equal(1, await db.StudentNotes.CountAsync(n => n.StudentId == studentId)); // only the seeded one
    }

    // ---------------------------------------------------------------
    // 18. Student Notes: with StudentNotes.Manage, an admin can actually add
    // a note through the real handler.
    // ---------------------------------------------------------------

    [Fact]
    public async Task Admin_with_studentnotes_manage_can_add_a_note()
    {
        var email = await CreateUserAsync("notes-manage", RoleNames.Admin);
        await GrantAsync(email, PermissionKeys.StudentsView, PermissionKeys.StudentNotesView, PermissionKeys.StudentNotesManage);
        var client = await LoggedInClientAsync(CreateClient(), email);

        var studentId = await SeedStudentAsync("notes-manage-target");
        var detailsPath = $"/Admin/StudentDetails/{studentId}";
        var token = await GetAntiforgeryTokenAsync(client, detailsPath);
        var noteText = $"Added by the manage test {Guid.NewGuid():N}";
        var response = await client.PostAsync($"/Admin/StudentDetails/{studentId}?handler=AddNote", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["id"] = studentId.ToString(),
            ["NewNote.Category"] = "Learning",
            ["NewNote.Text"] = noteText,
        }));
        // AddNote redirects back to itself on success (RedirectToPage), the
        // same as a Forbid() redirect to AccessDenied would — so the
        // authoritative proof is the DB row, not the status code alone.
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.DoesNotContain("AccessDenied", response.Headers.Location?.ToString() ?? string.Empty);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
        Assert.True(await db.StudentNotes.AnyAsync(n => n.StudentId == studentId && n.Text == noteText));
    }

    // ---------------------------------------------------------------
    // 19. Revoking Students.View takes effect on the very next request,
    // with no logout/login.
    // ---------------------------------------------------------------

    [Fact]
    public async Task Revoking_students_view_blocks_the_very_next_request_with_no_relogin()
    {
        var email = await CreateUserAsync("revoke-students-target", RoleNames.Admin);
        await GrantAsync(email, PermissionKeys.StudentsView);
        var client = await LoggedInClientAsync(CreateClient(), email);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Admin/Students")).StatusCode);

        await RevokeAsync(email, PermissionKeys.StudentsView);

        // Same client, same cookie, no logout/login in between.
        Assert.NotEqual(HttpStatusCode.OK, (await client.GetAsync("/Admin/Students")).StatusCode);
    }

    // =================================================================
    // Stage 2B (2026-09-03, Review Required — Authorization): Teachers,
    // Schedules, and RescheduleSessions. Same PermissionAuthorizationHandler,
    // same live-per-request DB check, same SystemAdmin bypass.
    // =================================================================

    // ---------------------------------------------------------------
    // 20. SystemAdmin: sees and acts on Teachers/Schedules/
    // RescheduleSessions with ZERO claims.
    // ---------------------------------------------------------------

    [Fact]
    public async Task SystemAdmin_views_and_manages_teachers_and_schedules_with_zero_permission_claims()
    {
        var email = await CreateUserAsync("sa-teachers-schedules", RoleNames.SystemAdmin);
        var client = await LoggedInClientAsync(CreateClient(), email);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Admin/Teachers")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Admin/Schedules")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Admin/RescheduleSessions")).StatusCode);

        var token = await GetAntiforgeryTokenAsync(client, "/Admin/Teachers");
        var newTeacherEmail = $"permtest-teacher-{Guid.NewGuid():N}@test.mvteaches.local";
        var response = await client.PostAsync("/Admin/Teachers?handler=Register", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["NewTeacher.FullName"] = "SystemAdmin Created Teacher",
            ["NewTeacher.Email"] = newTeacherEmail,
            ["NewTeacher.Password"] = Password,
            ["NewTeacher.TimeZoneId"] = "Asia/Amman",
        }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
        Assert.True(await db.Teachers.AnyAsync(t => t.FullName == "SystemAdmin Created Teacher"));
    }

    // ---------------------------------------------------------------
    // 21. Plain Admin, zero claims: forbidden from all three Stage 2B pages.
    // ---------------------------------------------------------------

    [Fact]
    public async Task Admin_with_no_permission_claims_is_forbidden_from_teachers_and_schedules_pages()
    {
        var email = await CreateUserAsync("no-claims-teachers-schedules", RoleNames.Admin);
        var client = await LoggedInClientAsync(CreateClient(), email);

        Assert.NotEqual(HttpStatusCode.OK, (await client.GetAsync("/Admin/Teachers")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await client.GetAsync("/Admin/Schedules")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await client.GetAsync("/Admin/RescheduleSessions")).StatusCode);
    }

    // ---------------------------------------------------------------
    // 22. Admin with only Teachers.View: sees teachers, cannot deactivate,
    // and a direct POST writes nothing.
    // ---------------------------------------------------------------

    [Fact]
    public async Task Admin_with_teachers_view_only_sees_teachers_but_cannot_deactivate_and_writes_nothing()
    {
        var email = await CreateUserAsync("teachers-view-only", RoleNames.Admin);
        await GrantAsync(email, PermissionKeys.TeachersView);
        var client = await LoggedInClientAsync(CreateClient(), email);

        var teacherId = await SeedTeacherAsync("view-only-deactivate-target");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Admin/Teachers")).StatusCode);

        var token = await GetAntiforgeryTokenAsync(client, "/Admin/Teachers");
        var response = await client.PostAsync("/Admin/Teachers?handler=Deactivate", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["teacherId"] = teacherId.ToString(),
        }));
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
        var stillActive = await db.Teachers.FirstAsync(t => t.Id == teacherId);
        Assert.True(stillActive.IsActive); // Deactivate never touched it
    }

    // ---------------------------------------------------------------
    // 23. Admin with Teachers.View + Teachers.Manage: can register a
    // teacher and deactivate one through the real handlers.
    // ---------------------------------------------------------------

    [Fact]
    public async Task Admin_with_teachers_manage_can_register_and_deactivate_a_teacher()
    {
        var email = await CreateUserAsync("teachers-manage", RoleNames.Admin);
        await GrantAsync(email, PermissionKeys.TeachersView, PermissionKeys.TeachersManage);
        var client = await LoggedInClientAsync(CreateClient(), email);

        var token = await GetAntiforgeryTokenAsync(client, "/Admin/Teachers");
        var newTeacherEmail = $"permtest-teacher-{Guid.NewGuid():N}@test.mvteaches.local";
        var registerResponse = await client.PostAsync("/Admin/Teachers?handler=Register", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["NewTeacher.FullName"] = "Manage Test Teacher",
            ["NewTeacher.Email"] = newTeacherEmail,
            ["NewTeacher.Password"] = Password,
            ["NewTeacher.TimeZoneId"] = "Asia/Amman",
        }));
        Assert.Equal(HttpStatusCode.OK, registerResponse.StatusCode);

        long teacherId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
            var created = await db.Teachers.FirstAsync(t => t.FullName == "Manage Test Teacher");
            teacherId = created.Id;
        }

        var deactivateToken = await GetAntiforgeryTokenAsync(client, "/Admin/Teachers");
        var deactivateResponse = await client.PostAsync("/Admin/Teachers?handler=Deactivate", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = deactivateToken,
            ["teacherId"] = teacherId.ToString(),
        }));
        Assert.Equal(HttpStatusCode.OK, deactivateResponse.StatusCode);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
        var deactivated = await verifyDb.Teachers.FirstAsync(t => t.Id == teacherId);
        Assert.False(deactivated.IsActive);
    }

    // ---------------------------------------------------------------
    // 24. Admin with only Schedules.View: sees schedules and reschedule
    // sessions, cannot pause a schedule, and a direct POST writes nothing.
    // ---------------------------------------------------------------

    [Fact]
    public async Task Admin_with_schedules_view_only_sees_schedules_but_cannot_pause_and_writes_nothing()
    {
        var email = await CreateUserAsync("schedules-view-only", RoleNames.Admin);
        await GrantAsync(email, PermissionKeys.SchedulesView);
        var client = await LoggedInClientAsync(CreateClient(), email);

        var scheduleId = await SeedRecurringScheduleAsync("view-only-pause-target");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Admin/Schedules")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Admin/RescheduleSessions")).StatusCode);

        var token = await GetAntiforgeryTokenAsync(client, "/Admin/Schedules");
        var response = await client.PostAsync("/Admin/Schedules?handler=Pause", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["scheduleId"] = scheduleId.ToString(),
        }));
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
        var stillActive = await db.RecurringSchedules.FirstAsync(s => s.Id == scheduleId);
        Assert.Equal(RecurringScheduleStatus.Active, stillActive.Status); // Pause never touched it
    }

    // ---------------------------------------------------------------
    // 25. Admin with Schedules.View + Schedules.Manage: can pause and
    // resume a real schedule through the real handlers.
    // ---------------------------------------------------------------

    [Fact]
    public async Task Admin_with_schedules_manage_can_pause_and_resume_a_schedule()
    {
        var email = await CreateUserAsync("schedules-manage", RoleNames.Admin);
        await GrantAsync(email, PermissionKeys.SchedulesView, PermissionKeys.SchedulesManage);
        var client = await LoggedInClientAsync(CreateClient(), email);

        var scheduleId = await SeedRecurringScheduleAsync("manage-pause-target");

        var pauseToken = await GetAntiforgeryTokenAsync(client, "/Admin/Schedules");
        var pauseResponse = await client.PostAsync("/Admin/Schedules?handler=Pause", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = pauseToken,
            ["scheduleId"] = scheduleId.ToString(),
        }));
        Assert.Equal(HttpStatusCode.OK, pauseResponse.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
            var paused = await db.RecurringSchedules.FirstAsync(s => s.Id == scheduleId);
            Assert.Equal(RecurringScheduleStatus.Paused, paused.Status);
        }

        var resumeToken = await GetAntiforgeryTokenAsync(client, "/Admin/Schedules");
        var resumeResponse = await client.PostAsync("/Admin/Schedules?handler=Resume", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = resumeToken,
            ["scheduleId"] = scheduleId.ToString(),
        }));
        Assert.Equal(HttpStatusCode.OK, resumeResponse.StatusCode);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
        var resumed = await verifyDb.RecurringSchedules.FirstAsync(s => s.Id == scheduleId);
        Assert.Equal(RecurringScheduleStatus.Active, resumed.Status);
    }

    // ---------------------------------------------------------------
    // 26. Admin with only Schedules.View cannot POST to
    // RescheduleSessions' handlers either — proving the shared
    // SchedulesManage guard covers both pages, not just Schedules.cshtml.
    // ---------------------------------------------------------------

    [Fact]
    public async Task Admin_with_schedules_view_only_cannot_post_to_reschedule_sessions()
    {
        var email = await CreateUserAsync("reschedule-view-only", RoleNames.Admin);
        await GrantAsync(email, PermissionKeys.SchedulesView);
        var client = await LoggedInClientAsync(CreateClient(), email);

        var token = await GetAntiforgeryTokenAsync(client, "/Admin/RescheduleSessions");
        var response = await client.PostAsync("/Admin/RescheduleSessions?handler=Reschedule", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Reschedule.StudentId"] = "1",
            ["Reschedule.OriginalSessionId"] = "1",
            ["Reschedule.ReplacementSessionId"] = "2",
        }));
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    // ---------------------------------------------------------------
    // 27. Revoking Schedules.View takes effect on the very next request,
    // with no logout/login.
    // ---------------------------------------------------------------

    [Fact]
    public async Task Revoking_schedules_view_blocks_the_very_next_request_with_no_relogin()
    {
        var email = await CreateUserAsync("revoke-schedules-target", RoleNames.Admin);
        await GrantAsync(email, PermissionKeys.SchedulesView);
        var client = await LoggedInClientAsync(CreateClient(), email);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Admin/Schedules")).StatusCode);

        await RevokeAsync(email, PermissionKeys.SchedulesView);

        // Same client, same cookie, no logout/login in between.
        Assert.NotEqual(HttpStatusCode.OK, (await client.GetAsync("/Admin/Schedules")).StatusCode);
    }
}
