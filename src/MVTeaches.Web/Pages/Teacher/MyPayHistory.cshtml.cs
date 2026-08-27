using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MVTeaches.Domain.Payroll;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;

namespace MVTeaches.Web.Pages.Teacher;

/// <summary>
/// §18.1/§18.2 — the other half of the teacher-facing payroll gap:
/// MySessions lets a teacher declare delivery, but nothing showed them what
/// they'd actually earned from verified deliveries once admin aggregated,
/// reviewed, approved, and paid a period. This is a read-only report over
/// the already-tested PayrollLine/PayrollPeriod data — no new business
/// logic, no new Application-layer method, the same "query _db directly
/// for a report" pattern /Admin/Payroll's own LoadAsync already uses.
/// </summary>
[Authorize(Roles = RoleNames.Teacher)]
public class MyPayHistoryModel : PageModel
{
    private readonly MvTeachesDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;

    public MyPayHistoryModel(MvTeachesDbContext db, UserManager<ApplicationUser> userManager)
    {
        _db = db;
        _userManager = userManager;
    }

    public record PayLineRow(long LineId, LocalDate PeriodStart, LocalDate PeriodEnd, PayrollPeriodStatus PeriodStatus,
        Instant SessionStartsAtUtc, string CourseName, string LevelCode, int Minutes, decimal RateAmount, decimal Amount,
        string Currency);

    public record CurrencyTotal(string Currency, decimal Paid, decimal Pending);

    public IReadOnlyList<PayLineRow> Lines { get; set; } = Array.Empty<PayLineRow>();
    public IReadOnlyList<CurrencyTotal> Totals { get; set; } = Array.Empty<CurrencyTotal>();

    /// <summary>True only when this Teacher-role account has no linked Teacher
    /// row yet — the same admin data-entry gap MySessions already guards against.</summary>
    public bool NoTeacherProfileLinked { get; set; }

    public async Task OnGetAsync()
    {
        var userId = long.Parse(_userManager.GetUserId(User)!);
        var teacher = await _db.Teachers.FirstOrDefaultAsync(t => t.UserId == userId);
        if (teacher is null)
        {
            NoTeacherProfileLinked = true;
            return;
        }

        // The one authorization-relevant line in this whole page: every other
        // filter below only narrows this teacher's own rows further, never
        // widens it to another teacher's.
        var lines = await _db.PayrollLines
            .Where(l => l.TeacherId == teacher.Id)
            .OrderByDescending(l => l.Id)
            .ToListAsync();

        var periodIds = lines.Select(l => l.PeriodId).Distinct().ToList();
        var periods = await _db.PayrollPeriods
            .Where(p => periodIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id);

        var sessionIds = lines.Select(l => l.SessionId).Distinct().ToList();
        var sessions = await _db.ClassSessions
            .Where(s => sessionIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id);

        var courseIds = sessions.Values.Select(s => s.CourseId).Distinct().ToList();
        var courseNames = await _db.Courses.Where(c => courseIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.NameEn);
        var levelIds = sessions.Values.Select(s => s.LevelId).Distinct().ToList();
        var levelCodes = await _db.Levels.Where(l => levelIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.Code);

        Lines = lines.Select(l =>
        {
            var period = periods[l.PeriodId];
            var session = sessions.GetValueOrDefault(l.SessionId);
            return new PayLineRow(l.Id, period.PeriodStart, period.PeriodEnd, period.Status,
                session?.StartsAtUtc ?? default, session is null ? "?" : courseNames.GetValueOrDefault(session.CourseId, "?"),
                session is null ? "?" : levelCodes.GetValueOrDefault(session.LevelId, "?"),
                l.Minutes, l.RateAmount, l.Amount, l.RateCurrency);
        }).ToList();

        // "Paid" for this teacher's own report means the money has actually
        // moved (Paid or Closed); everything earlier in the cycle (Open,
        // Review, Approved) is real work already verified but not yet paid,
        // shown separately so a teacher never mistakes one for the other.
        Totals = Lines
            .GroupBy(l => l.Currency)
            .Select(g => new CurrencyTotal(
                g.Key,
                g.Where(l => l.PeriodStatus is PayrollPeriodStatus.Paid or PayrollPeriodStatus.Closed).Sum(l => l.Amount),
                g.Where(l => l.PeriodStatus is not (PayrollPeriodStatus.Paid or PayrollPeriodStatus.Closed)).Sum(l => l.Amount)))
            .ToList();
    }
}
