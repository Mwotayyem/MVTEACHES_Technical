using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using MVTeaches.Domain.Attendance;
using MVTeaches.Domain.Audit;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Certificates;
using MVTeaches.Domain.Delivery;
using MVTeaches.Domain.Files;
using MVTeaches.Domain.Homework;
using MVTeaches.Domain.Integrations;
using MVTeaches.Domain.Ledger;
using MVTeaches.Domain.Migration;
using MVTeaches.Domain.Notifications;
using MVTeaches.Domain.Payments;
using MVTeaches.Domain.Payroll;
using MVTeaches.Domain.People;
using MVTeaches.Domain.Placement;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Domain.Settings;
using MVTeaches.Domain.Subscriptions;
using MVTeaches.Infrastructure.Identity;

namespace MVTeaches.Infrastructure.Persistence;

/// <summary>
/// The one and only EF Core context. Table-per-module configuration lives in
/// Persistence/Configurations/*, applied via ApplyConfigurationsFromAssembly —
/// this class itself stays a thin registry, per the repository's own instruction
/// that the study (and here, the configuration classes with their doc-comment
/// citations) remains the single source of business-rule truth, not scattered
/// magic numbers in this file.
/// </summary>
public class MvTeachesDbContext : IdentityDbContext<ApplicationUser, ApplicationRole, long>
{
    public MvTeachesDbContext(DbContextOptions<MvTeachesDbContext> options) : base(options) { }

    // Catalog
    public DbSet<Country> Countries => Set<Country>();
    public DbSet<Course> Courses => Set<Course>();
    public DbSet<Level> Levels => Set<Level>();
    public DbSet<AgeGroup> AgeGroups => Set<AgeGroup>();
    public DbSet<PricingPlan> PricingPlans => Set<PricingPlan>();

    // People
    public DbSet<Guardian> Guardians => Set<Guardian>();
    public DbSet<Student> Students => Set<Student>();
    public DbSet<Teacher> Teachers => Set<Teacher>();

    /// <summary>Owner decision 2026-08-30 rule 5: the levels each teacher is
    /// permitted to publish sessions for.</summary>
    public DbSet<TeacherLevelAssignment> TeacherLevelAssignments => Set<TeacherLevelAssignment>();
    public DbSet<Guardianship> Guardianships => Set<Guardianship>();

    // Placement
    public DbSet<PlacementInterview> PlacementInterviews => Set<PlacementInterview>();
    public DbSet<StudentLevel> StudentLevels => Set<StudentLevel>();

    // Placement test engine (owner decision 2026-08-30, reversing D-48)
    public DbSet<PlacementTestVersion> PlacementTestVersions => Set<PlacementTestVersion>();
    public DbSet<PlacementQuestion> PlacementQuestions => Set<PlacementQuestion>();
    public DbSet<PlacementAnswerChoice> PlacementAnswerChoices => Set<PlacementAnswerChoice>();
    public DbSet<PlacementScoreRange> PlacementScoreRanges => Set<PlacementScoreRange>();
    public DbSet<PlacementAttempt> PlacementAttempts => Set<PlacementAttempt>();
    public DbSet<PlacementAttemptAnswer> PlacementAttemptAnswers => Set<PlacementAttemptAnswer>();
    public DbSet<PlacementRetakeRequest> PlacementRetakeRequests => Set<PlacementRetakeRequest>();

    // Scheduling
    public DbSet<RecurringSchedule> RecurringSchedules => Set<RecurringSchedule>();
    public DbSet<ClassSession> ClassSessions => Set<ClassSession>();
    public DbSet<SessionEnrollment> SessionEnrollments => Set<SessionEnrollment>();
    public DbSet<TeacherAvailabilityRule> TeacherAvailabilityRules => Set<TeacherAvailabilityRule>();
    public DbSet<TeacherTimeOff> TeacherTimeOffs => Set<TeacherTimeOff>();
    public DbSet<ScheduleGenerationException> ScheduleGenerationExceptions => Set<ScheduleGenerationException>();
    public DbSet<CompensationRequest> CompensationRequests => Set<CompensationRequest>();

    // Attendance (D-83 anchor)
    public DbSet<AttendanceRecord> AttendanceRecords => Set<AttendanceRecord>();

    // Delivery & Payroll
    public DbSet<SessionDelivery> SessionDeliveries => Set<SessionDelivery>();
    public DbSet<Domain.Payroll.TeacherRate> TeacherRates => Set<Domain.Payroll.TeacherRate>();
    public DbSet<PayrollPeriod> PayrollPeriods => Set<PayrollPeriod>();
    public DbSet<PayrollLine> PayrollLines => Set<PayrollLine>();

    // Subscriptions & Ledger (the highest-risk pair)
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<SubscriptionFreeze> SubscriptionFreezes => Set<SubscriptionFreeze>();
    public DbSet<EntitlementLedgerEntry> EntitlementLedgerEntries => Set<EntitlementLedgerEntry>();

    // Payments
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<RefundRequest> RefundRequests => Set<RefundRequest>();
    public DbSet<PaymentMethodConfig> PaymentMethodConfigs => Set<PaymentMethodConfig>();

    // Finance
    public DbSet<MVTeaches.Domain.Finance.OperatingExpense> OperatingExpenses => Set<MVTeaches.Domain.Finance.OperatingExpense>();

    // Certificates
    public DbSet<LevelProgress> LevelProgresses => Set<LevelProgress>();
    public DbSet<Certificate> Certificates => Set<Certificate>();

    // Homework & Files
    public DbSet<Domain.Homework.Homework> Homeworks => Set<Domain.Homework.Homework>();
    public DbSet<HomeworkSubmission> HomeworkSubmissions => Set<HomeworkSubmission>();
    public DbSet<FileRecord> Files => Set<FileRecord>();

    // Settings & Audit
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<AuditLogEntry> AuditLogEntries => Set<AuditLogEntry>();

    // Notifications
    public DbSet<NotificationOutboxItem> NotificationOutboxItems => Set<NotificationOutboxItem>();

    // Migration
    public DbSet<MigrationBatch> MigrationBatches => Set<MigrationBatch>();
    public DbSet<MigrationRecord> MigrationRecords => Set<MigrationRecord>();

    // Video meetings (owner clarification 2026-08-29): provider-neutral Zoom/Google Meet
    public DbSet<TeacherMeetingConnection> TeacherMeetingConnections => Set<TeacherMeetingConnection>();
    public DbSet<ProvisionedMeeting> ProvisionedMeetings => Set<ProvisionedMeeting>();
    public DbSet<OAuthAuthorizationState> OAuthAuthorizationStates => Set<OAuthAuthorizationState>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfigurationsFromAssembly(typeof(MvTeachesDbContext).Assembly);

        // Identity tables use ASP.NET Core's default names (AspNetUsers, ...);
        // that's a cosmetic choice, not a business rule, so left as-is.
    }
}
