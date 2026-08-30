using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using MVTeaches.Application.Attendance;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using MVTeaches.Web.Resources;
using NodaTime;

namespace MVTeaches.Web.Pages.Guardian;

/// <summary>
/// D-83's actual front door — the single highest-risk, most heavily-tested
/// piece of backend in this repository (IJoinAttendanceService, 9 dedicated
/// tests plus a real concurrency race test) had no UI anywhere until this
/// page: a guardian pressing Join for a child with no independent login is
/// explicitly the primary case D-83 was designed around (D-02/D-03), and it
/// could previously only be exercised by calling the service directly from
/// a test. This page calls IJoinAttendanceService and nothing else — every
/// rule (entitlement check, enrollment check, guardian-authorization check,
/// idempotent double-press) is already enforced there.
/// </summary>
[Authorize(Roles = RoleNames.Guardian)]
public class MyChildrenModel : PageModel
{
    private readonly MvTeachesDbContext _db;
    private readonly IJoinAttendanceService _join;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IClock _clock;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public MyChildrenModel(MvTeachesDbContext db, IJoinAttendanceService join, UserManager<ApplicationUser> userManager, IClock clock,
        IStringLocalizer<SharedResource> localizer)
    {
        _db = db;
        _join = join;
        _userManager = userManager;
        _clock = clock;
        _localizer = localizer;
    }

    public record ChildSessionRow(long StudentId, string StudentName, long SessionId, Instant StartsAtUtc,
        string ScheduleTimeZone, string CourseName, string LevelCode, ClassSessionStatus SessionStatus,
        bool AlreadyPresent, bool CanJoin);

    public IReadOnlyList<ChildSessionRow> UpcomingSessions { get; set; } = Array.Empty<ChildSessionRow>();

    /// <summary>True only when this Guardian-role account has no linked
    /// Guardian row yet — an admin data-entry gap (see /Admin/Students),
    /// not something this page can fix itself.</summary>
    public bool NoGuardianProfileLinked { get; set; }

    public string? StatusMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostJoinAsync(long sessionId, long studentId)
    {
        var actingUserId = long.Parse(_userManager.GetUserId(User)!);
        var result = await _join.JoinAsync(new JoinAttendanceRequest(sessionId, studentId, actingUserId), HttpContext.RequestAborted);

        StatusMessage = result.Outcome switch
        {
            JoinOutcome.Recorded => _localizer["Attendance recorded — the session's full duration has been drawn from the subscription."].Value,
            JoinOutcome.AlreadyRecorded => _localizer["Already marked present for this session."].Value,
            _ => null,
        };
        ErrorMessage = result.Outcome switch
        {
            JoinOutcome.Unauthorized => _localizer["This child is not enrolled in that session, or this account is not their guardian."].Value,
            JoinOutcome.SessionNotFound => _localizer["Session not found."].Value,
            JoinOutcome.SessionNotYetJoinable => _localizer["This session hasn't started yet."].Value,
            JoinOutcome.InsufficientBalance => _localizer["No subscription has enough remaining balance to cover this session."].Value,
            _ => null,
        };

        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        var userId = long.Parse(_userManager.GetUserId(User)!);
        var guardian = await _db.Guardians.FirstOrDefaultAsync(g => g.UserId == userId);
        if (guardian is null)
        {
            NoGuardianProfileLinked = true;
            UpcomingSessions = Array.Empty<ChildSessionRow>();
            return;
        }

        var childIds = await _db.Guardianships
            .Where(g => g.GuardianId == guardian.Id)
            .Select(g => g.StudentId)
            .ToListAsync();
        var childNames = await _db.Students
            .Where(s => childIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.FullName);

        var now = _clock.GetCurrentInstant();
        var windowStart = now.Minus(Duration.FromDays(1));
        var windowEnd = now.Plus(Duration.FromDays(7));

        var enrollments = await _db.SessionEnrollments
            .Where(e => childIds.Contains(e.StudentId) && e.State == EnrollmentState.Active)
            .ToListAsync();
        var sessionIds = enrollments.Select(e => e.SessionId).Distinct().ToList();

        var sessions = await _db.ClassSessions
            .Where(s => sessionIds.Contains(s.Id) && s.StartsAtUtc >= windowStart && s.StartsAtUtc <= windowEnd)
            .OrderBy(s => s.StartsAtUtc)
            .ToListAsync();

        var attendance = await _db.AttendanceRecords
            .Where(a => sessionIds.Contains(a.SessionId) && childIds.Contains(a.StudentId))
            .ToListAsync();
        var presentPairs = attendance.Select(a => (a.SessionId, a.StudentId)).ToHashSet();

        var courseNames = await _db.Courses.ToDictionaryAsync(c => c.Id, c => c.NameEn);
        var levelCodes = await _db.Levels.ToDictionaryAsync(l => l.Id, l => l.Code);
        var enrollmentsByStudentAndSession = enrollments.ToDictionary(e => (e.SessionId, e.StudentId), e => e);

        var rows = new List<ChildSessionRow>();
        foreach (var session in sessions)
        {
            foreach (var studentId in childIds)
            {
                if (!enrollmentsByStudentAndSession.ContainsKey((session.Id, studentId)))
                {
                    continue;
                }

                var alreadyPresent = presentPairs.Contains((session.Id, studentId));
                var canJoin = !alreadyPresent && now >= session.StartsAtUtc && session.Status == ClassSessionStatus.Scheduled;
                rows.Add(new ChildSessionRow(studentId, childNames.GetValueOrDefault(studentId, $"#{studentId}"),
                    session.Id, session.StartsAtUtc, session.ScheduleTimeZone,
                    courseNames.GetValueOrDefault(session.CourseId, "?"), levelCodes.GetValueOrDefault(session.LevelId, "?"),
                    session.Status, alreadyPresent, canJoin));
            }
        }

        UpcomingSessions = rows;
    }
}
