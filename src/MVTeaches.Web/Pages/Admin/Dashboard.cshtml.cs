using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MVTeaches.Domain.Delivery;
using MVTeaches.Domain.Payments;
using MVTeaches.Domain.Payroll;
using MVTeaches.Domain.People;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Domain.Subscriptions;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;

namespace MVTeaches.Web.Pages.Admin;

/// <summary>
/// Technical Study §14 — the Visual CRM Dashboard, first real screen. This is
/// a deliberately minimal starting slice (live counts only, no charts/filters
/// yet) — the rest of §14's dashboard surface is still open work; see
/// docs/deployment/STATUS.md.
///
/// UI pass: the counts are now split into "the state of the school" and "work
/// waiting for you", and every waiting-work count links to the screen that
/// clears it. All of it is still counted live from tables that already exist —
/// no stored counter, no new field, no threshold invented here (the
/// StartingSoon/EndingSoon thresholds §2.3 of the dashboard design calls for
/// are admin settings that do not exist yet, so nothing pretends to use them).
/// </summary>
[Authorize(Roles = RoleNames.Admin + "," + RoleNames.SystemAdmin)]
public class DashboardModel : PageModel
{
    private readonly MvTeachesDbContext _db;
    private readonly IClock _clock;

    public DashboardModel(MvTeachesDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public int ActiveStudents { get; set; }
    public int ActiveTeachers { get; set; }
    public int SessionsToday { get; set; }
    public int OpenPayrollPeriods { get; set; }
    public int UnresolvedScheduleConflicts { get; set; }

    // "Waiting for an admin" — each one is a queue with a screen that clears it.
    public int PaymentsAwaitingConfirmation { get; set; }
    public int ReplacementRequestsAwaitingReview { get; set; }
    public int DeliveriesAwaitingVerification { get; set; }
    public int StudentsAwaitingVerification { get; set; }
    public int StudentsWithoutLevel { get; set; }
    public int PackagesAwaitingPayment { get; set; }

    public int TotalWaiting =>
        PaymentsAwaitingConfirmation + ReplacementRequestsAwaitingReview + DeliveriesAwaitingVerification
        + StudentsAwaitingVerification + StudentsWithoutLevel + PackagesAwaitingPayment;

    public async Task OnGetAsync()
    {
        var now = _clock.GetCurrentInstant();
        // "Today" here is the UTC calendar day — a genuine simplification,
        // not a per-country-timezone dashboard yet (§14's fuller spec is
        // still open work).
        var todayStartUtc = now.InUtc().Date.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();
        var todayEndUtc = todayStartUtc.Plus(Duration.FromDays(1));

        ActiveStudents = await _db.Students.CountAsync(s => s.Status == StudentStatus.Active);
        ActiveTeachers = await _db.Teachers.CountAsync(t => t.IsActive);
        SessionsToday = await _db.ClassSessions.CountAsync(s => s.StartsAtUtc >= todayStartUtc && s.StartsAtUtc < todayEndUtc);
        OpenPayrollPeriods = await _db.PayrollPeriods.CountAsync(p => p.Status == PayrollPeriodStatus.Open);
        UnresolvedScheduleConflicts = await _db.ScheduleGenerationExceptions.CountAsync(e => !e.Resolved);

        PaymentsAwaitingConfirmation = await _db.Payments.CountAsync(p => p.Status == PaymentStatus.Pending);
        ReplacementRequestsAwaitingReview = await _db.CompensationRequests
            .CountAsync(r => r.Status == CompensationRequestStatus.Pending);
        DeliveriesAwaitingVerification = await _db.SessionDeliveries.CountAsync(d => d.State == DeliveryState.Declared);
        StudentsAwaitingVerification = await _db.Students.CountAsync(s => s.Status == StudentStatus.PendingVerification);
        StudentsWithoutLevel = await _db.Students
            .CountAsync(s => s.Status != StudentStatus.Migrated
                             && !_db.StudentLevels.Any(l => l.StudentId == s.Id && l.IsCurrent));
        PackagesAwaitingPayment = await _db.Subscriptions.CountAsync(s => s.Status == SubscriptionStatus.Draft);
    }
}
