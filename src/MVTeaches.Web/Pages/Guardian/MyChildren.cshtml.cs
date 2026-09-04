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

    /// <summary>Owner decision 2026-09-04: a guardian adds their own children
    /// here rather than phoning the centre to have it typed in for them.</summary>
    private readonly MVTeaches.Application.People.ISelfRegistrationService _selfRegistration;

    public MyChildrenModel(MvTeachesDbContext db, IJoinAttendanceService join, UserManager<ApplicationUser> userManager, IClock clock,
        IStringLocalizer<SharedResource> localizer, MVTeaches.Application.People.ISelfRegistrationService selfRegistration)
    {
        _db = db;
        _join = join;
        _userManager = userManager;
        _clock = clock;
        _localizer = localizer;
        _selfRegistration = selfRegistration;
    }

    /// <summary>Owner decision 2026-09-04: the guardian's own add-a-child form.
    /// Deliberately small — a name, a birth date, a country. No level, no
    /// package and no login: a child gets those from the centre, and offering
    /// them here would be offering something this page cannot honour.</summary>
    public class NewChildInput
    {
        [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Enter the full name.")]
        public string FullName { get; set; } = string.Empty;

        /// <summary>Nullable so an untouched picker fails [Required] rather
        /// than passing as 0001-01-01 — the binding trap already documented on
        /// the admin registration forms.</summary>
        [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Enter the date of birth.")]
        public DateOnly? DateOfBirth { get; set; }

        [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Choose a country.")]
        public int? CountryId { get; set; }

        /// <summary>Optional: the number the centre actually calls about this
        /// child is the guardian's own. Recorded only if the family has one.</summary>
        [System.ComponentModel.DataAnnotations.Phone(ErrorMessage = "This is not a valid phone number.")]
        public string? PhoneNumber { get; set; }
    }

    [BindProperty]
    public NewChildInput NewChild { get; set; } = new();

    public IReadOnlyList<MVTeaches.Domain.Catalog.Country> Countries { get; set; } =
        Array.Empty<MVTeaches.Domain.Catalog.Country>();

    public bool IsArabic => System.Globalization.CultureInfo.CurrentUICulture
        .TwoLetterISOLanguageName.Equals("ar", StringComparison.OrdinalIgnoreCase);

    public string DisplayCountry(MVTeaches.Domain.Catalog.Country country) =>
        IsArabic ? country.NameAr : country.NameEn;

    /// <summary>Owner decision 2026-09-04. Every rule lives in
    /// ISelfRegistrationService.AddOwnChildAsync — which guardian this is comes
    /// from the signed-in account, never from the form, so no guardian can add
    /// a child to somebody else's family.</summary>
    public async Task<IActionResult> OnPostAddChildAsync()
    {
        ModelState.Clear();
        if (!TryValidateModel(NewChild, nameof(NewChild)))
        {
            await LoadAsync();
            return Page();
        }

        var actingUserId = long.Parse(_userManager.GetUserId(User)!);
        var dob = NewChild.DateOfBirth!.Value;
        var result = await _selfRegistration.AddOwnChildAsync(actingUserId, NewChild.FullName,
            new LocalDate(dob.Year, dob.Month, dob.Day), NewChild.PhoneNumber, NewChild.CountryId!.Value,
            HttpContext.RequestAborted);

        StatusMessage = result.Outcome == MVTeaches.Application.People.AddOwnChildOutcome.Added
            ? _localizer["{0} was added. The centre will set their level — until then you cannot buy a package or book a lesson for them.", NewChild.FullName].Value
            : null;
        ErrorMessage = result.Outcome switch
        {
            MVTeaches.Application.People.AddOwnChildOutcome.NotAGuardian =>
                _localizer["This account is not linked to a guardian record yet — please contact the centre."].Value,
            MVTeaches.Application.People.AddOwnChildOutcome.CountryNotAvailable =>
                _localizer["The centre does not currently operate in that country."].Value,
            _ => null,
        };

        if (result.Outcome == MVTeaches.Application.People.AddOwnChildOutcome.Added)
        {
            NewChild = new NewChildInput(); // a cleared form, ready for the next child
        }

        await LoadAsync();
        return Page();
    }

    /// <summary>Owner decision 2026-09-04, two changes to this row.
    /// <para>Time zone: <paramref name="StudentTimeZone"/> is the child's own
    /// country's DefaultTimeZone, and is what the guardian is shown. Nothing
    /// about STORAGE changed — the session is still stamped in UTC and still
    /// carries its own ScheduleTimeZone, which is kept here and shown beside
    /// the local time whenever the two differ, so a parent phoning the centre
    /// about "the 5 o'clock lesson" and the centre reading its own schedule
    /// are still talking about the same moment.</para>
    /// <para>Join context: a guardian with several children was being asked to
    /// press one of several identical-looking Join buttons. TeacherName is
    /// carried for that reason — the child's name, the teacher, the time, and
    /// the level together make each row identifiable at a glance. None of it
    /// touches what Join DOES; every press still names exactly one
    /// studentId and one sessionId.</para></summary>
    public record ChildSessionRow(long StudentId, string StudentName, long SessionId, Instant StartsAtUtc,
        string ScheduleTimeZone, string StudentTimeZone, string CourseName, string LevelCode,
        string TeacherName, ClassSessionStatus SessionStatus, bool AlreadyPresent, bool CanJoin);

    public IReadOnlyList<ChildSessionRow> UpcomingSessions { get; set; } = Array.Empty<ChildSessionRow>();

    /// <summary>Who this guardian's children are, independently of whether any
    /// of them happens to have a session this week — the previous page showed
    /// nothing at all in that (very common) case.</summary>
    public record ChildRow(long StudentId, string FullName, string? LevelCode,
        MVTeaches.Domain.People.StudentStatus Status, int UpcomingSessionCount);

    public IReadOnlyList<ChildRow> Children { get; set; } = Array.Empty<ChildRow>();

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
        Countries = await _db.Countries.Where(c => c.IsActive).OrderBy(c => c.NameEn).ToListAsync();
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

        // Owner decision 2026-09-04 — see ChildSessionRow. Each child's own
        // country zone, resolved once for the whole page rather than per row.
        var studentTimeZones = await _db.Students
            .Where(s => childIds.Contains(s.Id))
            .Join(_db.Countries, s => s.CountryId, c => c.Id, (s, c) => new { s.Id, c.DefaultTimeZone })
            .ToDictionaryAsync(x => x.Id, x => x.DefaultTimeZone);

        var teacherIds = sessions.Select(s => s.TeacherId).Distinct().ToList();
        var teacherNames = await _db.Teachers
            .Where(t => teacherIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.FullName);

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
                rows.Add(new ChildSessionRow(studentId, childNames.GetValueOrDefault(studentId, string.Empty),
                    session.Id, session.StartsAtUtc, session.ScheduleTimeZone,
                    // Falls back to the session's own zone when a country row
                    // somehow carries no zone — never to a silent UTC.
                    studentTimeZones.GetValueOrDefault(studentId) is { Length: > 0 } zone ? zone : session.ScheduleTimeZone,
                    courseNames.GetValueOrDefault(session.CourseId, "?"), levelCodes.GetValueOrDefault(session.LevelId, "?"),
                    teacherNames.GetValueOrDefault(session.TeacherId, "?"),
                    session.Status, alreadyPresent, canJoin));
            }
        }

        UpcomingSessions = rows;

        var currentLevelByChild = await _db.StudentLevels
            .Where(l => childIds.Contains(l.StudentId) && l.IsCurrent)
            .ToDictionaryAsync(l => l.StudentId, l => l.LevelId);
        var children = await _db.Students
            .Where(s => childIds.Contains(s.Id))
            .OrderBy(s => s.FullName)
            .ToListAsync();

        Children = children.Select(child => new ChildRow(
            child.Id,
            child.FullName,
            currentLevelByChild.TryGetValue(child.Id, out var levelId) ? levelCodes.GetValueOrDefault(levelId) : null,
            child.Status,
            rows.Count(r => r.StudentId == child.Id))).ToList();
    }
}
