using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Attendance;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;

namespace MVTeaches.Web.Pages.Student;

/// <summary>
/// The last of the four personas (Admin/Teacher/Guardian/Student) to get any
/// screen at all — the Teens/Adults case with their own login (§7), as
/// opposed to a child with no independent account who is covered by
/// /Guardian/MyChildren instead. Mirrors that page's structure closely: this
/// calls IJoinAttendanceService and nothing else, since every rule
/// (entitlement, enrollment, "the student's own login" authorization case —
/// checked first, before the guardian case, in
/// JoinAttendanceService.IsAuthorizedToJoinAsync) is already enforced there.
/// The acting student id is always resolved server-side from the
/// authenticated account's own linked Student row, never taken from the
/// request — there is no "which student" choice to make here at all.
/// </summary>
[Authorize(Roles = RoleNames.Student)]
public class MySessionsModel : PageModel
{
    private readonly MvTeachesDbContext _db;
    private readonly IJoinAttendanceService _join;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IClock _clock;

    public MySessionsModel(MvTeachesDbContext db, IJoinAttendanceService join, UserManager<ApplicationUser> userManager, IClock clock)
    {
        _db = db;
        _join = join;
        _userManager = userManager;
        _clock = clock;
    }

    public record SessionRow(long SessionId, Instant StartsAtUtc, string ScheduleTimeZone, string CourseName,
        string LevelCode, ClassSessionStatus SessionStatus, bool AlreadyPresent, bool CanJoin);

    public IReadOnlyList<SessionRow> UpcomingSessions { get; set; } = Array.Empty<SessionRow>();

    /// <summary>True only when this Student-role account has no linked Student
    /// row yet — an admin data-entry gap (see /Admin/Students), not something
    /// this page can fix itself.</summary>
    public bool NoStudentProfileLinked { get; set; }

    public string? StatusMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostJoinAsync(long sessionId)
    {
        var actingUserId = long.Parse(_userManager.GetUserId(User)!);
        var student = await _db.Students.FirstOrDefaultAsync(s => s.UserId == actingUserId);
        if (student is null)
        {
            NoStudentProfileLinked = true;
            return Page();
        }

        var result = await _join.JoinAsync(new JoinAttendanceRequest(sessionId, student.Id, actingUserId), HttpContext.RequestAborted);

        StatusMessage = result.Outcome switch
        {
            JoinOutcome.Recorded => "Attendance recorded — the session's full duration has been drawn from your subscription.",
            JoinOutcome.AlreadyRecorded => "You're already marked present for this session.",
            _ => null,
        };
        ErrorMessage = result.Outcome switch
        {
            JoinOutcome.Unauthorized => "You are not enrolled in that session.",
            JoinOutcome.SessionNotFound => "Session not found.",
            JoinOutcome.SessionNotYetJoinable => "This session hasn't started yet.",
            JoinOutcome.InsufficientBalance => "No subscription has enough remaining balance to cover this session.",
            _ => null,
        };

        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        var userId = long.Parse(_userManager.GetUserId(User)!);
        var student = await _db.Students.FirstOrDefaultAsync(s => s.UserId == userId);
        if (student is null)
        {
            NoStudentProfileLinked = true;
            UpcomingSessions = Array.Empty<SessionRow>();
            return;
        }

        var now = _clock.GetCurrentInstant();
        var windowStart = now.Minus(Duration.FromDays(1));
        var windowEnd = now.Plus(Duration.FromDays(7));

        var enrollments = await _db.SessionEnrollments
            .Where(e => e.StudentId == student.Id && e.State == EnrollmentState.Active)
            .ToListAsync();
        var sessionIds = enrollments.Select(e => e.SessionId).Distinct().ToList();

        var sessions = await _db.ClassSessions
            .Where(s => sessionIds.Contains(s.Id) && s.StartsAtUtc >= windowStart && s.StartsAtUtc <= windowEnd)
            .OrderBy(s => s.StartsAtUtc)
            .ToListAsync();

        var attendedSessionIds = (await _db.AttendanceRecords
            .Where(a => sessionIds.Contains(a.SessionId) && a.StudentId == student.Id)
            .Select(a => a.SessionId)
            .ToListAsync()).ToHashSet();

        var courseNames = await _db.Courses.ToDictionaryAsync(c => c.Id, c => c.NameEn);
        var levelCodes = await _db.Levels.ToDictionaryAsync(l => l.Id, l => l.Code);

        UpcomingSessions = sessions.Select(session =>
        {
            var alreadyPresent = attendedSessionIds.Contains(session.Id);
            var canJoin = !alreadyPresent && now >= session.StartsAtUtc && session.Status == ClassSessionStatus.Scheduled;
            return new SessionRow(session.Id, session.StartsAtUtc, session.ScheduleTimeZone,
                courseNames.GetValueOrDefault(session.CourseId, "?"), levelCodes.GetValueOrDefault(session.LevelId, "?"),
                session.Status, alreadyPresent, canJoin);
        }).ToList();
    }
}
