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

    /// <summary>The next lessons the centre will actually run — the thing an
    /// admin looks for first in the morning, and something a page of counts
    /// alone could never answer.</summary>
    public record UpcomingSessionRow(NodaTime.Instant StartsAtUtc, string? TimeZoneId, string TeacherName,
        string LevelCode, int SeatsTaken, int Capacity);

    public IReadOnlyList<UpcomingSessionRow> UpcomingSessions { get; set; } = Array.Empty<UpcomingSessionRow>();

    /// <summary>One bucket per day for the coming week: how many sessions are
    /// scheduled that day. Drawn as plain CSS bars — no charting library is
    /// added for six numbers, and the numbers are printed as text as well.</summary>
    public record DayLoad(LocalDate Day, int SessionCount);

    public IReadOnlyList<DayLoad> WeekAhead { get; set; } = Array.Empty<DayLoad>();

    public int BusiestDayCount => WeekAhead.Count == 0 ? 0 : WeekAhead.Max(d => d.SessionCount);

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

        // The week ahead, from now: the sessions themselves for the list, and a
        // per-day count for the bars. Same table, same live read as everything
        // else on this page.
        var weekEndUtc = todayStartUtc.Plus(Duration.FromDays(7));
        var weekSessions = await _db.ClassSessions
            .Where(s => s.StartsAtUtc >= todayStartUtc && s.StartsAtUtc < weekEndUtc
                        && s.Status != ClassSessionStatus.Cancelled)
            .OrderBy(s => s.StartsAtUtc)
            .ToListAsync();

        var teacherNames = await _db.Teachers.ToDictionaryAsync(t => t.Id, t => t.FullName);
        var levelCodes = await _db.Levels.ToDictionaryAsync(l => l.Id, l => l.Code);

        UpcomingSessions = weekSessions
            .Where(s => s.StartsAtUtc >= now)
            .Take(6)
            .Select(s => new UpcomingSessionRow(s.StartsAtUtc, s.ScheduleTimeZone,
                teacherNames.GetValueOrDefault(s.TeacherId, string.Empty),
                levelCodes.GetValueOrDefault(s.LevelId, "?"), s.SeatsTaken, s.Capacity))
            .ToList();

        var firstDay = todayStartUtc.InUtc().Date;
        var countsByDay = weekSessions
            .GroupBy(s => s.StartsAtUtc.InUtc().Date)
            .ToDictionary(g => g.Key, g => g.Count());
        WeekAhead = Enumerable.Range(0, 7)
            .Select(offset => firstDay.PlusDays(offset))
            .Select(day => new DayLoad(day, countsByDay.GetValueOrDefault(day, 0)))
            .ToList();
    }
}
