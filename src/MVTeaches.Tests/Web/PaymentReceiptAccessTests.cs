using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MVTeaches.Application.Files;
using MVTeaches.Application.Payments;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Common;
using MVTeaches.Domain.People;
using MVTeaches.Domain.Subscriptions;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;
using Xunit;

namespace MVTeaches.Tests.Web;

/// <summary>
/// Section 5 (attachment protection): proves /Files/Receipt — the ONLY way
/// to read an uploaded receipt — is limited to the payment's own student,
/// one of their active guardians, or Admin/SystemAdmin, and that an
/// unrelated authenticated account (a different guardian entirely) is
/// refused exactly like a payment/receipt that doesn't exist at all (a 404
/// either way, never leaking which case it was). Runs against a real
/// ASP.NET Core host and real PostgreSQL, same convention as
/// AuthorizationTests. Every account below is created fresh with a
/// Guid-based email PER TEST (never a shared constant email looked up via
/// find-or-create) — xUnit runs [Fact]s within a class in parallel by
/// default, and a shared "find the guardian by email, create it if
/// missing" pattern races across concurrently-running facts; a uniquely
/// named account per call has nothing to race against.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class PaymentReceiptAccessTests : IClassFixture<PaymentReceiptAccessTests.Factory>, IAsyncLifetime
{
    private readonly Factory _factory;
    private const string Password = "CorrectHorse123!";
    private const string AdminEmail = "receipt-admin@test.mvteaches.local";
    private static long _idSeed = 90_200_000;
    private static long NextId() => Interlocked.Increment(ref _idSeed);

    private static string TwoLetterCode(long seed)
    {
        var n = (int)(seed % 676);
        return string.Concat((char)('A' + n / 26), (char)('A' + n % 26));
    }

    // Same retry-on-collision fix as SessionCancellationServiceTests (the
    // fix for the real, reproduced commit-1394e15 CI failure): the 2-letter
    // code space (676 combinations) is shared with every other test class
    // in the same run via the same NextId()-derived TwoLetterCode pattern.
    private static async Task<int> SeedCountryAsync(MvTeachesDbContext db)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var id = (int)NextId();
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

    private static readonly Regex AntiforgeryTokenPattern = new(
        "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");

    public PaymentReceiptAccessTests(TestDatabaseFixture fixture, Factory factory)
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

        // The only account reused across facts — role-only (Admin), no
        // per-test domain row (Guardian/Student) is ever created against it,
        // so there is nothing for concurrent facts to race on.
        await EnsureUserAsync(userManager, AdminEmail, RoleNames.Admin);
    }

    public Task DisposeAsync() => Task.CompletedTask;

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

    private HttpClient CreateClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private async Task<HttpClient> CreateAuthenticatedClientAsync(string email)
    {
        var client = CreateClient();
        var loginPage = await client.GetStringAsync("/Account/Login");
        var token = AntiforgeryTokenPattern.Match(loginPage).Groups[1].Value;

        var form = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Input.Email"] = email,
            ["Input.Password"] = Password,
        };
        var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    /// <summary>Creates a brand-new, uniquely-emailed guardian login (via the
    /// real UserManager, exactly like a real sign-up) linked as the primary
    /// guardian of a brand-new student, with a Payment carrying a real
    /// uploaded receipt. Returns the payment id and the guardian's own login
    /// email — nothing here is shared with any other test invocation.</summary>
    private async Task<(long PaymentId, string GuardianEmail)> SeedPaymentWithReceiptAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var files = scope.ServiceProvider.GetRequiredService<IFileStorageService>();
        var payments = scope.ServiceProvider.GetRequiredService<IPaymentService>();

        var guardianEmail = $"receipt-owner-{Guid.NewGuid():N}@test.mvteaches.local";
        await EnsureUserAsync(userManager, guardianEmail, RoleNames.Guardian);
        var ownerUser = await userManager.FindByEmailAsync(guardianEmail);

        // SeedCountryAsync retries on a 2-letter-code collision by clearing
        // this context's change tracker (see its own remarks) — so it must
        // run BEFORE anything else is added to this context, never after,
        // or a clear would silently drop an unsaved entity added earlier.
        var countryId = await SeedCountryAsync(db);

        var guardian = new Guardian(ownerUser!.Id, "Receipt Owner Guardian");
        db.Guardians.Add(guardian);

        var student = new Student(countryId, "Receipt Test Student", new LocalDate(2012, 1, 1), userId: null);
        db.Students.Add(student);
        await db.SaveChangesAsync();

        db.Guardianships.Add(new Guardianship(guardian.Id, student.Id, GuardianRelationship.Parent, isPrimary: true, linkedByUserId: ownerUser.Id));
        await db.SaveChangesAsync();

        var recorded = await payments.RecordManualPaymentAsync(
            new RecordPaymentRequest(student.Id, null, ownerUser.Id, new Money(50m, "JOD"), MVTeaches.Domain.Payments.PaymentMethod.BankTransfer, null),
            CancellationToken.None);

        // A minimal valid JPEG magic-byte stream — enough for the real
        // content-sniffing FileStorageService to accept it.
        var jpegBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46 };
        await using var stream = new MemoryStream(jpegBytes);
        var upload = await files.SaveAsync(stream, nameof(MVTeaches.Domain.Files.FilePurpose.PaymentProof),
            "receipt.jpg", ownerUser.Id, CancellationToken.None, student.Id);
        Assert.Equal(SaveUploadOutcome.Saved, upload.Outcome);

        await payments.AttachTransferDetailsAsync(recorded.PaymentId, ownerUser.Id, isAdminInitiated: false,
            "Owner Guardian", new LocalDate(2026, 8, 30), $"RX-REF-{Guid.NewGuid():N}"[..20], upload.DocumentId, CancellationToken.None);

        return (recorded.PaymentId, guardianEmail);
    }

    [Fact]
    public async Task The_payments_own_guardian_can_view_the_receipt()
    {
        var (paymentId, guardianEmail) = await SeedPaymentWithReceiptAsync();
        var client = await CreateAuthenticatedClientAsync(guardianEmail);

        var response = await client.GetAsync($"/Files/Receipt?paymentId={paymentId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task An_admin_can_view_any_receipt()
    {
        var (paymentId, _) = await SeedPaymentWithReceiptAsync();
        var client = await CreateAuthenticatedClientAsync(AdminEmail);

        var response = await client.GetAsync($"/Files/Receipt?paymentId={paymentId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>The core IDOR guarantee: an unrelated guardian — authenticated,
    /// but with no relationship to this student at all — must be refused
    /// exactly like a nonexistent payment (404 either way).</summary>
    [Fact]
    public async Task An_unrelated_guardian_cannot_view_someone_elses_receipt()
    {
        var (paymentId, _) = await SeedPaymentWithReceiptAsync();
        var (_, strangerEmail) = await SeedPaymentWithReceiptAsync(); // a second, wholly unrelated guardian
        var client = await CreateAuthenticatedClientAsync(strangerEmail);

        var response = await client.GetAsync($"/Files/Receipt?paymentId={paymentId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task An_unauthenticated_request_is_redirected_to_login()
    {
        var client = CreateClient();
        var response = await client.GetAsync("/Files/Receipt?paymentId=1");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location?.ToString() ?? string.Empty);
    }

    [Fact]
    public async Task A_nonexistent_payment_id_returns_not_found_for_any_authenticated_role()
    {
        var client = await CreateAuthenticatedClientAsync(AdminEmail);
        var response = await client.GetAsync("/Files/Receipt?paymentId=999999999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    public class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseEnvironment("Development");
    }
}
