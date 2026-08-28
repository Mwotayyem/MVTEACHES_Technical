using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Attendance;
using MVTeaches.Application.Integrations;
using MVTeaches.Application.Certificates;
using MVTeaches.Application.Payments;
using MVTeaches.Application.Ledger;
using MVTeaches.Application.Payroll;
using MVTeaches.Application.People;
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
using MVTeaches.Infrastructure.Reports;
using MVTeaches.Infrastructure.Scheduling;
using MVTeaches.Infrastructure.Settings;
using MVTeaches.Infrastructure.Subscriptions;
using NodaTime;

var builder = WebApplication.CreateBuilder(args);

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
    .AddDefaultTokenProviders();

// ---------------------------------------------------------------------
// Time — NodaTime's real clock everywhere except tests (FakeClock there)
// ---------------------------------------------------------------------
builder.Services.AddSingleton<IClock>(SystemClock.Instance);

// ---------------------------------------------------------------------
// Application services
// ---------------------------------------------------------------------
builder.Services.AddScoped<IJoinAttendanceService, JoinAttendanceService>();
builder.Services.AddScoped<IPaymentService, PaymentService>();
builder.Services.AddScoped<ISettingsProvider, SettingsProvider>();
builder.Services.AddScoped<IScheduleGenerationService, ScheduleGenerationService>();
builder.Services.AddScoped<IPayrollRateResolver, PayrollRateResolver>();
builder.Services.AddScoped<ICertificateService, CertificateService>();
builder.Services.AddScoped<IPayrollService, PayrollService>();
builder.Services.AddScoped<IFinancialReportService, FinancialReportService>();
builder.Services.AddScoped<IStudentAdmissionService, StudentAdmissionService>();
builder.Services.AddScoped<ITeacherAdmissionService, TeacherAdmissionService>();
builder.Services.AddScoped<IRecurringScheduleService, RecurringScheduleService>();
builder.Services.AddScoped<IEnrollmentService, EnrollmentService>();
builder.Services.AddScoped<ITeacherRateService, TeacherRateService>();
builder.Services.AddScoped<ISubscriptionService, SubscriptionService>();
builder.Services.AddScoped<IEntitlementBalanceQuery, EntitlementBalanceQuery>();
builder.Services.AddScoped<ISessionCancellationService, SessionCancellationService>();
builder.Services.AddScoped<IStudentBookingService, StudentBookingService>();
builder.Services.AddScoped<ICompensationRequestService, CompensationRequestService>();
builder.Services.AddScoped<ISessionFinalizationService, SessionFinalizationService>();

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

builder.Services.AddRazorPages();

// Deployment guide §10's flagged gap — a minimal liveness/readiness probe,
// not a dependency dashboard. See DatabaseHealthCheck's own remarks on why
// optional integrations (Zoom/WhatsApp/MEPS) are deliberately not checked here.
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("postgresql");

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

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
    await MVTeaches.Infrastructure.Persistence.DataSeeder.SeedAsync(scope.ServiceProvider);
}

// Admin-only Hangfire dashboard — never exposed unauthenticated (§22 IDOR/auth review).
app.MapHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = new[] { new AdminOnlyDashboardAuthorizationFilter() },
});

RecurringJob.AddOrUpdate<NotificationDispatchJob>(
    "notification-dispatch", job => job.RunAsync(CancellationToken.None), "*/1 * * * *");

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
