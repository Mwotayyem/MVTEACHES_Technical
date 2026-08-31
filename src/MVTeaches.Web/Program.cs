using System.Globalization;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Attendance;
using MVTeaches.Application.Integrations;
using MVTeaches.Application.Certificates;
using MVTeaches.Application.Payments;
using MVTeaches.Application.Ledger;
using MVTeaches.Application.Payroll;
using MVTeaches.Application.People;
using MVTeaches.Application.Placement;
using MVTeaches.Application.Reports;
using MVTeaches.Application.Scheduling;
using MVTeaches.Application.Settings;
using MVTeaches.Application.Subscriptions;
using MVTeaches.Infrastructure.Attendance;
using MVTeaches.Infrastructure.Certificates;
using MVTeaches.Infrastructure.Hangfire;
using MVTeaches.Infrastructure.Health;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Integrations;
using MVTeaches.Infrastructure.Integrations.Email;
using MVTeaches.Infrastructure.Integrations.GoogleMeet;
using MVTeaches.Infrastructure.Integrations.Security;
using MVTeaches.Infrastructure.Integrations.WhatsApp;
using MVTeaches.Infrastructure.Integrations.Zoom;
using MVTeaches.Infrastructure.Ledger;
using MVTeaches.Infrastructure.Notifications;
using MVTeaches.Infrastructure.Payments;
using MVTeaches.Infrastructure.Payroll;
using MVTeaches.Infrastructure.People;
using MVTeaches.Infrastructure.Persistence;
using MVTeaches.Infrastructure.Placement;
using MVTeaches.Infrastructure.Reports;
using MVTeaches.Infrastructure.Scheduling;
using MVTeaches.Infrastructure.Settings;
using MVTeaches.Infrastructure.Subscriptions;
using NodaTime;
using MVTeaches.Web;

var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsDevelopment())
{
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole();
    builder.Logging.AddDebug();
}

// Local Staging's own secrets (ConnectionStrings:MvTeaches, StagingSeed:*,
// Bootstrap:*) — ASP.NET Core's User Secrets provider is only added when
// IsDevelopment() is true, so Staging needs a different mechanism. A
// machine-wide "User"-scope environment variable was tried first and
// rejected: it silently redirected every dotnet process on this Windows
// account — including a plain Development run — to the staging database,
// with no warning. This file is the replacement: it is gitignored, lives
// only in this worktree, and — because it is added after CreateBuilder's
// own default providers — outranks any stray environment variable that
// might exist, so that mistake cannot recur. The IsStaging() guard is
// deliberate and load-bearing, not just tidiness: it makes it impossible
// for this file to be consulted by Development or a future Production
// deployment even if it somehow ended up present there, and it leaves
// Production's own environment-variable-based configuration (see
// /docs/deployment/README.md) completely untouched. Never commit it; see
// docs/LOCAL-STAGING.md for how to populate it.
//
// The path is resolved from MVTEACHES_STAGING_SECRETS_PATH (an absolute
// path) when set, falling back to the plain relative filename otherwise.
// This is load-bearing, not cosmetic: Local Staging's supported launch
// method (scripts/run-local-staging.ps1) runs the *published* build, whose
// working directory is the publish output folder — and this file is
// deliberately excluded from publish output (see the .csproj) so it can
// never be copied there. A relative path would silently resolve to the
// publish folder and never be found, making every setting in this file a
// silent no-op under that launch method. The script sets the environment
// variable to this project folder's own copy; a plain `dotnet run`/F5
// launch (working directory already the project folder) needs no override
// and keeps working via the relative fallback.
if (builder.Environment.IsStaging())
{
    var stagingSecretsPath = Environment.GetEnvironmentVariable("MVTEACHES_STAGING_SECRETS_PATH")
        ?? "appsettings.Staging.secrets.json";
    builder.Configuration.AddJsonFile(stagingSecretsPath, optional: true, reloadOnChange: false);
}

// ---------------------------------------------------------------------
// Persistence (PostgreSQL 16 + NodaTime — Technical Study §33, §14.4 rule 4)
// ---------------------------------------------------------------------
var connectionString = builder.Configuration.GetConnectionString("MvTeaches")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:MvTeaches is not set. See /docs/deployment/README.md — " +
        "this must come from environment/user-secrets, never a committed value.");

builder.Services.AddDbContext<MvTeachesDbContext>(options =>
    options.UseNpgsql(connectionString, npgsql => npgsql.UseNodaTime()));

// ---------------------------------------------------------------------
// Identity (§7.1 — long keys, every documented FK is bigint)
// ---------------------------------------------------------------------
builder.Services
    .AddIdentity<ApplicationUser, ApplicationRole>(options =>
    {
        // §22 security review defaults — tightened here rather than left at
        // ASP.NET Core's generic defaults, since this handles minors' data.
        options.Password.RequiredLength = 10;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.SignIn.RequireConfirmedPhoneNumber = false; // OTP flow confirms via a custom step, not Identity's own token provider
    })
    .AddEntityFrameworkStores<MvTeachesDbContext>()
    .AddDefaultTokenProviders()
    .AddErrorDescriber<MVTeaches.Web.Identity.LocalizedIdentityErrorDescriber>();

// Local Staging isolation: a browser cookie's identity is (name, domain,
// path) — the PORT is not part of it. Running Development and Local
// Staging side by side on the same "localhost" domain with the default,
// environment-agnostic cookie name would mean whichever app signs in
// LAST overwrites the other's auth cookie in the browser, silently
// logging the other environment out or (worse) mixing up which
// environment a request is actually authenticated against. Development
// deliberately keeps ASP.NET Core Identity's own default cookie
// name/behavior untouched (no reason to disrupt anyone already using it);
// every OTHER environment (Staging today, Production later) gets its own
// name suffixed with the environment, so it can never collide with
// Development's in the same browser. The antiforgery cookie gets the same
// treatment for the same reason.
if (!builder.Environment.IsDevelopment())
{
    builder.Services.ConfigureApplicationCookie(options =>
    {
        options.Cookie.Name = $".MVTeaches.Identity.{builder.Environment.EnvironmentName}";
    });
    builder.Services.AddAntiforgery(options =>
    {
        options.Cookie.Name = $".MVTeaches.Antiforgery.{builder.Environment.EnvironmentName}";
    });
}

// ---------------------------------------------------------------------
// Time — NodaTime's real clock everywhere except tests (FakeClock there)
// ---------------------------------------------------------------------
builder.Services.AddSingleton<IClock>(SystemClock.Instance);

// ---------------------------------------------------------------------
// Application services
// ---------------------------------------------------------------------
builder.Services.AddScoped<IJoinAttendanceService, JoinAttendanceService>();
builder.Services.AddScoped<IPaymentService, PaymentService>();
builder.Services.AddScoped<IPaymentMethodConfigService, PaymentMethodConfigService>();
builder.Services.AddScoped<MVTeaches.Application.Files.IFileStorageService, MVTeaches.Infrastructure.Files.FileStorageService>();
builder.Services.Configure<MVTeaches.Infrastructure.Files.FileStorageOptions>(
    builder.Configuration.GetSection(MVTeaches.Infrastructure.Files.FileStorageOptions.SectionName));
builder.Services.AddScoped<ISettingsProvider, SettingsProvider>();
builder.Services.AddScoped<IScheduleGenerationService, ScheduleGenerationService>();
builder.Services.AddScoped<IPayrollRateResolver, PayrollRateResolver>();
builder.Services.AddScoped<ICertificateService, CertificateService>();
builder.Services.AddScoped<IPayrollService, PayrollService>();
builder.Services.AddScoped<IFinancialReportService, FinancialReportService>();
builder.Services.AddScoped<IOperatingExpenseService, OperatingExpenseService>();
builder.Services.AddScoped<IStudentAdmissionService, StudentAdmissionService>();
builder.Services.AddScoped<ITeacherAdmissionService, TeacherAdmissionService>();
builder.Services.AddScoped<ITeacherLevelAuthorizationService, TeacherLevelAuthorizationService>();
builder.Services.AddScoped<IPlacementTestAdminService, PlacementTestAdminService>();
builder.Services.AddScoped<IPlacementAttemptService, PlacementAttemptService>();
builder.Services.AddScoped<ITeacherSlotPublishingService, TeacherSlotPublishingService>();
builder.Services.AddScoped<IRecurringScheduleService, RecurringScheduleService>();
builder.Services.AddScoped<IEnrollmentService, EnrollmentService>();
builder.Services.AddScoped<ITeacherRateService, TeacherRateService>();
builder.Services.AddScoped<ISubscriptionService, SubscriptionService>();
builder.Services.AddScoped<IEntitlementBalanceQuery, EntitlementBalanceQuery>();
builder.Services.AddScoped<IEntitlementTransferService, EntitlementTransferService>();
builder.Services.AddScoped<ISessionCancellationService, SessionCancellationService>();
builder.Services.AddScoped<IStudentBookingService, StudentBookingService>();
builder.Services.AddScoped<ICompensationRequestService, CompensationRequestService>();
builder.Services.AddScoped<ISessionFinalizationService, SessionFinalizationService>();

builder.Services.AddLocalization();

// ---------------------------------------------------------------------
// Integration boundaries — §5-8 of the master engineering prompt.
// Each is registered as "not configured" until real credentials exist;
// swapping in a real, verified implementation is a one-line change here,
// not a redesign, once D-88/Meta credentials are available. Zoom/Google
// Meet are the exception below: the real OAuth+REST clients ARE written
// (owner clarification 2026-08-29) — only live verification against real
// teacher accounts is still outstanding, not the code itself.
// ---------------------------------------------------------------------
builder.Services.Configure<ZoomOptions>(builder.Configuration.GetSection(ZoomOptions.SectionName));
builder.Services.Configure<GoogleOptions>(builder.Configuration.GetSection(GoogleOptions.SectionName));
builder.Services.Configure<WhatsAppOptions>(builder.Configuration.GetSection(WhatsAppOptions.SectionName));
builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection(SmtpOptions.SectionName));
builder.Services.Configure<BootstrapAdminOptions>(builder.Configuration.GetSection(BootstrapAdminOptions.SectionName));
builder.Services.Configure<LocalDevelopmentSeedOptions>(builder.Configuration.GetSection(LocalDevelopmentSeedOptions.SectionName));
builder.Services.Configure<StagingSeedOptions>(builder.Configuration.GetSection(StagingSeedOptions.SectionName));

builder.Services.AddScoped<INotificationSender, NotConfiguredWhatsAppSender>();
builder.Services.AddScoped<INotificationSender, SmtpEmailSender>(); // real — see SmtpEmailSender's remarks

// ---------------------------------------------------------------------
// Video meetings (owner clarification 2026-08-29) — provider-neutral
// Zoom/Google Meet, each teacher authorizing their OWN account. Access/
// refresh tokens are encrypted via ASP.NET Core Data Protection; the key
// ring is persisted OUTSIDE the application database (a dedicated folder,
// overridable via the DataProtectionKeysPath setting) so tokens stay
// decryptable across restarts/deployments — see /docs/deployment for the
// production path (a persistent volume, never the container filesystem).
// ---------------------------------------------------------------------
var dataProtectionKeysPath = builder.Configuration["DataProtectionKeysPath"];
var dataProtectionBuilder = builder.Services.AddDataProtection()
    .SetApplicationName("MVTeaches");
dataProtectionBuilder.PersistKeysToFileSystem(
    new DirectoryInfo(string.IsNullOrWhiteSpace(dataProtectionKeysPath)
        ? Path.Combine(AppContext.BaseDirectory, "dataprotection-keys")
        : dataProtectionKeysPath));

builder.Services.AddHttpClient<IVideoMeetingProviderClient, ZoomVideoMeetingProviderClient>();
builder.Services.AddHttpClient<IVideoMeetingProviderClient, GoogleMeetProviderClient>();
builder.Services.AddSingleton<ITokenProtector, DataProtectionTokenProtector>();
builder.Services.AddScoped<TokenRefreshCoordinator>();
builder.Services.AddScoped<ITeacherMeetingConnectionService, TeacherMeetingConnectionService>();
builder.Services.AddScoped<IMeetingProvisioningService, MeetingProvisioningService>();

// ---------------------------------------------------------------------
// Hangfire — §25: execution mechanism only, PostgreSQL storage (§30.3: no
// Redis/RabbitMQ needed at this scale).
// ---------------------------------------------------------------------
builder.Services.AddHangfire(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(c => c.UseNpgsqlConnection(connectionString)));
builder.Services.AddHangfireServer();
builder.Services.AddScoped<NotificationDispatchJob>();
builder.Services.AddScoped<SessionReminderJob>();

builder.Services
    .AddRazorPages()
    .AddViewLocalization(LanguageViewLocationExpanderFormat.Suffix)
    .AddDataAnnotationsLocalization(options =>
    {
        options.DataAnnotationLocalizerProvider = (type, factory) =>
            factory.Create(typeof(MVTeaches.Web.Resources.SharedResource));
    });

// Deployment guide §10's flagged gap — a minimal liveness/readiness probe,
// not a dependency dashboard. See DatabaseHealthCheck's own remarks on why
// optional integrations (Zoom/WhatsApp/MEPS) are deliberately not checked here.
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("postgresql");

var app = builder.Build();

var supportedUiCultures = new[]
{
    new CultureInfo("ar-JO"),
    new CultureInfo("en"),
};

// Owner decision 2026-08-30 (bilingual amount/date entry): UI language and
// number/date FORMATTING are deliberately decoupled. Only "en-US" is ever a
// supported (non-UI) Culture — ASP.NET Core's own RequestLocalizationMiddleware
// resolves Culture and UICulture independently against their own Supported
// lists (a documented, intentional feature), so a payer whose UI is Arabic
// still has CultureInfo.CurrentCulture pinned to en-US: an HTML5 number/date
// input always submits "3.500"/"2026-08-30" with a period and ISO order
// regardless of the browser's own locale, and en-US parses/formats that
// exact same way. Without this pin, .NET's ar-JO NumberFormatInfo (Arabic
// decimal/group separators) made ASP.NET Core's default model binder reject
// a perfectly valid "3.500" submitted from an Arabic-language page — a real,
// reproduced bug, not a theoretical one. SupportedUICultures is untouched:
// @T[...] resx lookups still resolve to ar-JO exactly as before.
var supportedCultures = new[] { new CultureInfo("en-US") };

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseRequestLocalization(new RequestLocalizationOptions
{
    DefaultRequestCulture = new RequestCulture("en-US", "ar-JO"),
    SupportedCultures = supportedCultures,
    SupportedUICultures = supportedUiCultures,
});

app.UseHttpsRedirection();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();
app.MapHealthChecks("/health");

// Zoom webhook — unauthenticated by design (Zoom cannot present an MVTeaches
// login) but every accepted event is signature+timestamp-verified inside
// the handler itself before anything is trusted (see ZoomWebhookHandler).
// Returns 404 outright when Zoom isn't configured, never a fake 200.
// (Written as an explicit lambda rather than a method group: the ASP.NET
// Core route-handler analyzer crashes on the method-group form here.)
app.MapPost("/webhooks/zoom", async (HttpContext ctx, MvTeachesDbContext db,
        Microsoft.Extensions.Options.IOptions<ZoomOptions> zoom, IClock clock, ILoggerFactory loggerFactory) =>
    await ZoomWebhookHandler.HandleAsync(ctx, db, zoom, clock, loggerFactory));

// Idempotent reference-data seeding (roles, age groups, settings defaults)
// only — NOT schema migrations. Migrations are a deliberate, separate
// deployment step (`dotnet ef database update`, see /docs/deployment) run
// by a human before a new version goes live, never auto-applied by the app
// process itself (§39/§40: production infra steps stay explicit, not implicit
// side effects of starting the app).
using (var scope = app.Services.CreateScope())
{
    // Local-only `F5` bootstrap (auto-migrate + idempotent dummy accounts/
    // content) — a no-op in every environment except Development, and even
    // there a no-op unless LocalDevelopmentSeed:Enabled is explicitly set.
    // See docs/LOCAL-DEVELOPMENT.md and LocalDevelopmentSeeder's own remarks
    // for the full safety story (why this can never touch a
    // staging/production database). MUST run BEFORE DataSeeder.SeedAsync —
    // on a genuinely fresh database, DataSeeder's own role-seeding query
    // fails outright ("relation AspNetRoles does not exist") unless the
    // schema already exists, which only this migration step creates.
    await MVTeaches.Infrastructure.Persistence.LocalDevelopmentSeeder.MigrateIfEnabledAsync(scope.ServiceProvider, app.Environment);

    // The SAME local-convenience pattern, independently gated for the
    // "Local Staging" environment on THIS machine only — never a real
    // remote staging/production deployment, which still applies migrations
    // as the deliberate, separate, human-run `dotnet ef database update`
    // step documented in /docs/deployment/README.md. See StagingSeeder's
    // own remarks and docs/LOCAL-STAGING.md for why this is a wholly
    // separate class/guard from LocalDevelopmentSeeder, not an extension
    // of it — relaxing one can never accidentally relax the other.
    await MVTeaches.Infrastructure.Persistence.StagingSeeder.MigrateIfEnabledAsync(scope.ServiceProvider, app.Environment);

    await MVTeaches.Infrastructure.Persistence.DataSeeder.SeedAsync(scope.ServiceProvider);

    await MVTeaches.Infrastructure.Persistence.LocalDevelopmentSeeder.SeedAsync(scope.ServiceProvider, app.Environment);
    await MVTeaches.Infrastructure.Persistence.StagingSeeder.SeedAsync(scope.ServiceProvider, app.Environment);
}

// Admin-only Hangfire dashboard — never exposed unauthenticated (§22 IDOR/auth review).
app.MapHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = new[] { new AdminOnlyDashboardAuthorizationFilter() },
});

RecurringJob.AddOrUpdate<NotificationDispatchJob>(
    "notification-dispatch", job => job.RunAsync(CancellationToken.None), "*/1 * * * *");

// Owner decision 2026-08-30 rule 9: "a 5-minute-before reminder (idempotent
// job)" — every minute so a session's 5-minute window is never missed.
RecurringJob.AddOrUpdate<SessionReminderJob>(
    "session-reminder-5min", job => job.SendFiveMinuteRemindersAsync(CancellationToken.None), "*/1 * * * *");

// §15.3: "مهمة Hangfire ليلية + تشغيل يدوي من الأدمن" — the manual trigger is
// the Hangfire dashboard's own "Trigger now" button on this same recurring
// job (admin-only per AdminOnlyDashboardAuthorizationFilter above), not a
// second code path to keep in sync with the scheduled one.
RecurringJob.AddOrUpdate<IScheduleGenerationService>(
    "schedule-generation", job => job.GenerateAsync(CancellationToken.None), "0 2 * * *");

// Owner correction (self-service booking, 2026-08-28): a no-show must
// resolve promptly after the session ends, not the next morning — every 5
// minutes, not nightly.
RecurringJob.AddOrUpdate<ISessionFinalizationService>(
    "session-finalization", job => job.FinalizeEndedSessionsAsync(CancellationToken.None), "*/5 * * * *");

app.Run();

// Exposes the top-level-statements Program for WebApplicationFactory<Program>
// (the authorization/IDOR integration tests) — a standard, required pattern
// for testing minimal-hosting-model ASP.NET Core apps; adds no behavior.
public partial class Program { }
