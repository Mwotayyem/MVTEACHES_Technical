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
using MVTeaches.Application.Ledger;
using MVTeaches.Web.Display;
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
[Authorize(Policy = PermissionKeys.DashboardView)]
public class DashboardModel : PageModel
{
    private readonly MvTeachesDbContext _db;
    private readonly IClock _clock;
    private readonly IEntitlementBalanceQuery _balances;
    private readonly SessionRosterReader _rosters;

    public DashboardModel(MvTeachesDbContext db, IClock clock, IEntitlementBalanceQuery balances,
        SessionRosterReader rosters)
    {
        _db = db;
        _clock = clock;
        _balances = balances;
        _rosters = rosters;
    }

    public int ActiveStudents { get; set; }
    public int ActiveTeachers { get; set; }
    public int SessionsToday { get; set; }
    public int OpenPayrollPeriods { get; set; }
    public int UnresolvedScheduleConflicts { get; set; }

    /// <summary>Packages actually running, and weekly classes actually
    /// producing sessions - the size of what the centre is delivering right
    /// now, which a head-count of students does not answer.</summary>
    public int RunningPackages { get; set; }
    public int RunningWeeklyClasses { get; set; }

    /// <summary>Money for ONE currency. Never summed across currencies: D-53
    /// forbids converting between them automatically, so two currencies show
    /// as two lines rather than one invented total.</summary>
    public record MoneyLine(string Currency, decimal PaidThisMonth, decimal Outstanding);

    public IReadOnlyList<MoneyLine> Money { get; set; } = Array.Empty<MoneyLine>();

    /// <summary>How many students sit in each state, using exactly the same
    /// classification the register shows on each row (StudentLifecycle), so a
    /// number here can never disagree with the badges over there.</summary>
    public IReadOnlyDictionary<StudentLifecycleState, int> StudentStates { get; set; } =
        new Dictionary<StudentLifecycleState, int>();

    public int StudentsNeedingAttention => StudentStates
        .Where(pair => StudentLifecycle.NeedsAttention(pair.Key))
        .Sum(pair => pair.Value);

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
    public record UpcomingSessionRow(long Id, NodaTime.Instant StartsAtUtc, string? TimeZoneId, string TeacherName,
        string LevelCode, int SeatsTaken, int Capacity);

    public IReadOnlyList<UpcomingSessionRow> UpcomingSessions { get; set; } = Array.Empty<UpcomingSessionRow>();

    /// <summary>Who is in each of those lessons. Read-only.</summary>
    public IReadOnlyDictionary<long, SessionRoster> Rosters { get; set; } =
        new Dictionary<long, SessionRoster>();

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

        RunningPackages = await _db.Subscriptions.CountAsync(sub => sub.Status == SubscriptionStatus.Active);
        RunningWeeklyClasses = await _db.RecurringSchedules
            .CountAsync(r => r.Status == RecurringScheduleStatus.Active);

        // --- money -------------------------------------------------------
        // "This month" is the current UTC calendar month, the same
        // simplification "today" already makes above. Paid means CONFIRMED and
        // uses the received amount - the money that actually arrived, not the
        // amount that was asked for.
        var monthStart = new LocalDate(now.InUtc().Date.Year, now.InUtc().Date.Month, 1)
            .AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();
        var confirmedPayments = await _db.Payments
            .Where(pay => pay.Status == PaymentStatus.Confirmed)
            .ToListAsync();
        var billableSubscriptions = await _db.Subscriptions
            .Where(sub => sub.Status == SubscriptionStatus.Draft || sub.Status == SubscriptionStatus.Active)
            .ToListAsync();

        var paidThisMonth = confirmedPayments
            .Where(pay => pay.ConfirmedAtUtc is not null && pay.ConfirmedAtUtc >= monthStart)
            .GroupBy(pay => pay.ReceivedCurrency ?? pay.Amount.Currency)
            .ToDictionary(g => g.Key, g => g.Sum(pay => pay.ReceivedAmount ?? pay.Amount.Amount));

        // Outstanding, school-wide: MoneyStanding sums each OPEN subscription's
        // own shortfall (clamped at zero on its own) rather than netting one
        // big billed total against one big paid total — a payment tied to a
        // closed, since-Expired package must not silently offset a different,
        // currently-unpaid one, for the same reason it must not on one
        // student's own card. See MoneyStanding's own remarks.
        var standing = MoneyStanding.ComputeByCurrency(billableSubscriptions, confirmedPayments);

        Money = paidThisMonth.Keys.Union(standing.Keys)
            .OrderBy(currency => currency)
            .Select(currency => new MoneyLine(currency,
                paidThisMonth.GetValueOrDefault(currency),
                standing.GetValueOrDefault(currency).Outstanding))
            .ToList();

        // --- where every student stands ----------------------------------
        var allStudents = await _db.Students.ToListAsync();
        var allSubscriptions = await _db.Subscriptions.ToListAsync();
        var balanceBySubscription = await _balances.GetSubscriptionBalancesAsync(
            allSubscriptions.Select(sub => sub.Id).ToList(), HttpContext.RequestAborted);
        var pendingPaymentStudentIds = (await _db.Payments
                .Where(pay => pay.Status == PaymentStatus.Pending)
                .Select(pay => pay.StudentId).Distinct().ToListAsync())
            .ToHashSet();
        var attendedStudentIds = (await _db.AttendanceRecords
                .Where(a => a.IsPresent).Select(a => a.StudentId).Distinct().ToListAsync())
            .ToHashSet();
        var activeEnrollments = await _db.SessionEnrollments
            .Where(e => e.State == EnrollmentState.Active).ToListAsync();
        var futureSessions = await _db.ClassSessions
            .Where(cs => cs.StartsAtUtc > now && cs.Status != ClassSessionStatus.Cancelled)
            .ToDictionaryAsync(cs => cs.Id);

        var subscriptionsByStudent = allSubscriptions.GroupBy(sub => sub.StudentId)
            .ToDictionary(g => g.Key, g => g.ToList());
        var upcomingByStudent = activeEnrollments
            .Where(e => futureSessions.ContainsKey(e.SessionId))
            .GroupBy(e => e.StudentId)
            .ToDictionary(g => g.Key, g => g.Select(e => futureSessions[e.SessionId])
                .OrderBy(cs => cs.StartsAtUtc).ToList());

        StudentStates = allStudents
            .Select(student =>
            {
                var subs = subscriptionsByStudent.GetValueOrDefault(student.Id, new List<Subscription>());
                var running = subs.FirstOrDefault(sub => sub.Status == SubscriptionStatus.Active);
                var upcoming = upcomingByStudent.GetValueOrDefault(student.Id, new List<ClassSession>());
                var nextLesson = upcoming.FirstOrDefault();
                return StudentLifecycle.Classify(new StudentLifecycleFacts(
                    student.Status,
                    pendingPaymentStudentIds.Contains(student.Id),
                    subs.Any(sub => sub.Status == SubscriptionStatus.Draft),
                    running is not null,
                    subs.Count > 0,
                    subs.Where(sub => sub.Status == SubscriptionStatus.Active)
                        .Sum(sub => balanceBySubscription.GetValueOrDefault(sub.Id)),
                    attendedStudentIds.Contains(student.Id),
                    upcoming.Count,
                    nextLesson?.DurationMinutes,
                    running?.ExpiresOn,
                    nextLesson is null ? null : nextLesson.StartsAtUtc.InUtc().Date));
            })
            .GroupBy(state => state)
            .ToDictionary(g => g.Key, g => g.Count());

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
            .Select(s => new UpcomingSessionRow(s.Id, s.StartsAtUtc, s.ScheduleTimeZone,
                teacherNames.GetValueOrDefault(s.TeacherId, string.Empty),
                levelCodes.GetValueOrDefault(s.LevelId, "?"), s.SeatsTaken, s.Capacity))
            .ToList();

        Rosters = await _rosters.ReadAsync(
            UpcomingSessions.Select(session => session.Id).ToList(), HttpContext.RequestAborted);

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
