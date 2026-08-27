using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Payroll;
using MVTeaches.Domain.Delivery;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;

namespace MVTeaches.Web.Pages.Teacher;

/// <summary>
/// §18.1 step [1] — the teacher-facing half of the payroll cycle, which had
/// no UI at all until now (only the admin-side verify/reject on
/// /Admin/Payroll existed). A teacher sees their own sessions and declares
/// delivery on the ones that have actually finished; everything else
/// (rate lookup, separation of duties, the scheduled-duration-not-declared-
/// minutes rule) is already enforced by IPayrollService/SessionDelivery.
/// </summary>
[Authorize(Roles = RoleNames.Teacher)]
public class MySessionsModel : PageModel
{
    private readonly MvTeachesDbContext _db;
    private readonly IPayrollService _payroll;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IClock _clock;

    public MySessionsModel(MvTeachesDbContext db, IPayrollService payroll, UserManager<ApplicationUser> userManager, IClock clock)
    {
        _db = db;
        _payroll = payroll;
        _userManager = userManager;
        _clock = clock;
    }

    public record SessionRow(long SessionId, Instant StartsAtUtc, string ScheduleTimeZone, int DurationMinutes,
        string CourseName, string LevelCode, ClassSessionStatus Status, DeliveryState? DeliveryState, bool CanDeclare);

    public IReadOnlyList<SessionRow> Sessions { get; set; } = Array.Empty<SessionRow>();

    /// <summary>True only when this Teacher-role account has no linked Teacher
    /// row yet — an admin data-entry gap, not something this page can fix
    /// itself (creating a Teacher record is an admin action per §9.1/D-28).</summary>
    public bool NoTeacherProfileLinked { get; set; }

    public string? StatusMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostDeclareAsync(long sessionId, int declaredMinutes, string? note)
    {
        var userId = long.Parse(_userManager.GetUserId(User)!);
        var teacher = await _db.Teachers.FirstOrDefaultAsync(t => t.UserId == userId);
        var session = teacher is null ? null : await _db.ClassSessions.FirstOrDefaultAsync(s => s.Id == sessionId);

        // This page's whole contract is "a teacher declares THEIR OWN session"
        // (§18.1 step [1]) — IPayrollService.DeclareAsync itself has no notion
        // of caller identity beyond the declaredByUserId it stamps, so without
        // this check any authenticated Teacher-role account could declare
        // delivery on a session belonging to a completely different teacher.
        // "not found" and "not yours" get the same generic message so this
        // page never confirms or denies another teacher's session id exists.
        if (teacher is null || session is null || session.TeacherId != teacher.Id)
        {
            ErrorMessage = "Session not found.";
            await LoadAsync();
            return Page();
        }

        // The "already happened" rule shown client-side by CanDeclare (below)
        // must also hold server-side — otherwise it's just a UI suggestion.
        if (session.EndsAtUtc > _clock.GetCurrentInstant())
        {
            ErrorMessage = "This session hasn't ended yet — nothing to declare.";
            await LoadAsync();
            return Page();
        }

        var result = await _payroll.DeclareAsync(sessionId, userId, declaredMinutes, note, HttpContext.RequestAborted);

        StatusMessage = result.Outcome == DeclareDeliveryOutcome.Declared ? "Delivery declared — awaiting admin verification." : null;
        ErrorMessage = result.Outcome switch
        {
            DeclareDeliveryOutcome.SessionNotFound => "Session not found.",
            DeclareDeliveryOutcome.SessionNotDelivered => "This session was marked not-delivered — nothing to declare.",
            DeclareDeliveryOutcome.AlreadyDeclared => "This session's delivery was already declared.",
            _ => null,
        };

        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        var userId = long.Parse(_userManager.GetUserId(User)!);
        var teacher = await _db.Teachers.FirstOrDefaultAsync(t => t.UserId == userId);
        if (teacher is null)
        {
            NoTeacherProfileLinked = true;
            Sessions = Array.Empty<SessionRow>();
            return;
        }

        var now = _clock.GetCurrentInstant();
        var windowStart = now.Minus(Duration.FromDays(30));
        var windowEnd = now.Plus(Duration.FromDays(7));

        var sessions = await _db.ClassSessions
            .Where(s => s.TeacherId == teacher.Id && s.StartsAtUtc >= windowStart && s.StartsAtUtc <= windowEnd)
            .OrderByDescending(s => s.StartsAtUtc)
            .ToListAsync();

        var sessionIds = sessions.Select(s => s.Id).ToList();
        var deliveries = await _db.SessionDeliveries
            .Where(d => sessionIds.Contains(d.SessionId))
            .ToDictionaryAsync(d => d.SessionId, d => (DeliveryState?)d.State);

        var courseNames = await _db.Courses.ToDictionaryAsync(c => c.Id, c => c.NameEn);
        var levelCodes = await _db.Levels.ToDictionaryAsync(l => l.Id, l => l.Code);

        Sessions = sessions.Select(s =>
        {
            var deliveryState = deliveries.GetValueOrDefault(s.Id); // null = no delivery row exists yet
            var hasEnded = s.EndsAtUtc <= now;
            var canDeclare = hasEnded && s.Status != ClassSessionStatus.NotDelivered &&
                deliveryState is null or DeliveryState.Pending;
            return new SessionRow(s.Id, s.StartsAtUtc, s.ScheduleTimeZone, s.DurationMinutes,
                courseNames.GetValueOrDefault(s.CourseId, "?"), levelCodes.GetValueOrDefault(s.LevelId, "?"),
                s.Status, deliveryState, canDeclare);
        }).ToList();
    }
}
