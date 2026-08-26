using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MVTeaches.Domain.Payroll;
using MVTeaches.Domain.People;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;

namespace MVTeaches.Web.Pages.Admin;

/// <summary>
/// Technical Study §14 — the Visual CRM Dashboard, first real screen. This is
/// a deliberately minimal starting slice (live counts only, no charts/filters
/// yet) — the rest of §14's dashboard surface is still open work; see
/// docs/deployment/STATUS.md.
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
    }
}
