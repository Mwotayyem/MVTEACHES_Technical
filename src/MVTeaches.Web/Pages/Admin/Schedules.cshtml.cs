using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using MVTeaches.Application.Integrations;
using MVTeaches.Application.Scheduling;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Integrations;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using MVTeaches.Web.Resources;
using NodaTime;

namespace MVTeaches.Web.Pages.Admin;

/// <summary>
/// §15.2 (D-23) — the piece that was missing entirely: nothing in the app
/// could previously create a RecurringSchedule, which meant no ClassSession
/// could ever exist outside of a test's direct database insert. This is the
/// literal root of "sessions/scheduling/attendance" — every other feature
/// built this session (Join/attendance, payroll declare/verify, certificate
/// progress) depends on a real ClassSession existing.
///
/// The nightly generator (ScheduleGenerationService) turns a schedule created
/// here into real ClassSession rows; per the standing decision recorded in
/// docs/deployment/STATUS.md, the manual-run path for that generator is the
/// Hangfire dashboard's own admin-only "Trigger now" button on the
/// "schedule-generation" job — deliberately NOT a second code path here.
/// </summary>
[Authorize(Roles = RoleNames.Admin + "," + RoleNames.SystemAdmin)]
public class SchedulesModel : PageModel
{
    private readonly MvTeachesDbContext _db;
    private readonly IRecurringScheduleService _schedules;
    private readonly IEnrollmentService _enrollments;
    private readonly ISessionCancellationService _cancellations;
    private readonly IMeetingProvisioningService _meetings;
    private readonly IClock _clock;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public SchedulesModel(MvTeachesDbContext db, IRecurringScheduleService schedules, IEnrollmentService enrollments,
        ISessionCancellationService cancellations, IMeetingProvisioningService meetings, IClock clock,
        UserManager<ApplicationUser> userManager, IStringLocalizer<SharedResource> localizer)
    {
        _db = db;
        _schedules = schedules;
        _enrollments = enrollments;
        _cancellations = cancellations;
        _meetings = meetings;
        _clock = clock;
        _userManager = userManager;
        _localizer = localizer;
    }

    public record ScheduleRow(long Id, string TeacherName, string CourseName, int LevelId, string LevelCode, string AgeGroupCode,
        string Days, string StartLocal, int DurationMinutes, string TimeZoneId, RecurringScheduleStatus Status);

    /// <summary>An upcoming, still-cancellable session — the admin needs to see
    /// a real session Id to act on it; nothing before this page ever surfaced one.</summary>
    public record SessionRow(long Id, string TeacherName, string CourseName, string LevelCode,
        Instant StartsAtUtc, string ScheduleTimeZone, int SeatsTaken, int Capacity, ClassSessionStatus Status);

    /// <summary><see cref="CurrentLevelId"/> is what lets the "enroll a
    /// student" picker below narrow the schedule list to the student's own
    /// level automatically (same data-owner-key/data-mv-options-of mechanism
    /// Admin/Subscriptions already uses for its plan picker) — arriving here
    /// from a student's own file used to drop that context entirely and show
    /// every level's schedules at once.</summary>
    public record StudentPickRow(long Id, string FullName, int? CurrentLevelId, string? CurrentLevelCode);

    public IReadOnlyList<ScheduleRow> Schedules { get; set; } = Array.Empty<ScheduleRow>();
    public IReadOnlyList<SessionRow> UpcomingSessions { get; set; } = Array.Empty<SessionRow>();
    public IReadOnlyList<Country> Countries { get; set; } = Array.Empty<Country>();
    public IReadOnlyList<Course> Courses { get; set; } = Array.Empty<Course>();
    public IReadOnlyList<Level> Levels { get; set; } = Array.Empty<Level>();
    public IReadOnlyList<AgeGroup> AgeGroups { get; set; } = Array.Empty<AgeGroup>();
    public IReadOnlyList<MVTeaches.Domain.People.Teacher> Teachers { get; set; } = Array.Empty<MVTeaches.Domain.People.Teacher>();
    public IReadOnlyList<string> TimeZoneIds { get; set; } = Array.Empty<string>();
    public IReadOnlyList<StudentPickRow> Students { get; set; } = Array.Empty<StudentPickRow>();

    /// <summary>Set when the admin arrived here from one student's own file
    /// ("Open schedules"), so the enroll form can pre-select them and narrow
    /// the schedule list to their level instead of starting from scratch.</summary>
    [BindProperty(SupportsGet = true, Name = "studentId")]
    public long? FocusStudentId { get; set; }

    public string? FocusStudentName { get; set; }
    public string? FocusStudentLevelCode { get; set; }

    [BindProperty]
    public CreateScheduleInput NewSchedule { get; set; } = new();

    [BindProperty]
    public EnrollInput Enroll { get; set; } = new();

    [BindProperty]
    public CancelInput Cancel { get; set; } = new();

    [BindProperty]
    public ReassignInput Reassign { get; set; } = new();

    public string? StatusMessage { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>Owner clarification (2026-08-29): teachers with no connected
    /// Zoom/Google Meet account cannot be assigned online sessions — shown
    /// here so the admin doesn't pick one and get a bare rejection.</summary>
    public IReadOnlySet<long> TeachersNotReadyForOnlineSessions { get; set; } = new HashSet<long>();

    // Ids/dates are nullable so [Required] actually fires on an untouched
    // picker: on a non-nullable value type the empty post binds to 0 (or
    // 0001-01-01), ModelState.Clear() drops the binding error, and the service
    // is called with an id nobody chose.
    public class ReassignInput
    {
        [Required(ErrorMessage = "Choose a session.")] public long? SessionId { get; set; }
        [Required(ErrorMessage = "Choose a teacher.")] public long? NewTeacherId { get; set; }
    }

    public class EnrollInput
    {
        [Required(ErrorMessage = "Choose a schedule.")] public long? RecurringScheduleId { get; set; }
        [Required(ErrorMessage = "Choose a student.")] public long? StudentId { get; set; }
    }

    public class CancelInput
    {
        [Required(ErrorMessage = "Choose a session.")] public long? SessionId { get; set; }
        [Required(ErrorMessage = "Write the reason for this decision.")] public string Reason { get; set; } = string.Empty;
        public long? ReplacementSessionId { get; set; }
    }

    public class CreateScheduleInput
    {
        [Required(ErrorMessage = "Choose a country.")] public int? CountryId { get; set; }
        [Required(ErrorMessage = "Choose a course.")] public long? CourseId { get; set; }
        [Required(ErrorMessage = "Choose a level.")] public int? LevelId { get; set; }
        [Required(ErrorMessage = "Choose an age group.")] public int? AgeGroupId { get; set; }
        [Required(ErrorMessage = "Choose a teacher.")] public long? TeacherId { get; set; }

        /// <summary>ISO day numbers (1=Monday..7=Sunday) — bound from a set of checkboxes.</summary>
        [Required, MinLength(1)]
        public List<int> DaysOfWeek { get; set; } = new();

        [Required(ErrorMessage = "Enter the start time.")] public TimeOnly? StartLocal { get; set; }
        [Required, Range(1, 480)] public int DurationMinutes { get; set; } = 60;
        [Required(ErrorMessage = "Choose a time zone.")] public string TimeZoneId { get; set; } = string.Empty;
        [Required(ErrorMessage = "Enter the start date.")] public DateOnly? StartsOn { get; set; }
    }

    /// <summary>The fixed seat count a weekly group session gets (D-98) —
    /// read from the domain so the screen can never state a different one.</summary>
    public int GroupCapacity => MVTeaches.Domain.Scheduling.ClassSession.CapacityFor(SessionType.Group);

    /// <summary>Which of the tabbed panels is open. Four unrelated actions —
    /// create a weekly class, enroll a student, cancel a session, reassign a
    /// teacher — used to sit stacked on this one page, and an admin who came to
    /// do one of them had to read past the other three. They are tabs now.
    ///
    /// This is set by whichever handler just ran, so a re-render after a post
    /// reopens the tab the admin was actually working in instead of dropping
    /// them back on the first one with their own message out of sight.
    /// Display only — no handler reads it, and it changes no rule.</summary>
    public string ActiveTab { get; private set; } = "create";

    public async Task OnGetAsync()
    {
        // Arriving from a student's file means the intent is to enroll THAT
        // student, not to create a centre-wide weekly class.
        if (FocusStudentId is not null) { ActiveTab = "enroll"; }
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        ActiveTab = "create";
        ModelState.Clear();
        if (!TryValidateModel(NewSchedule, nameof(NewSchedule)))
        {
            await LoadAsync();
            return Page();
        }

        var days = NewSchedule.DaysOfWeek.Select(d => (IsoDayOfWeek)d).ToList();
        var startLocalTime = NewSchedule.StartLocal!.Value;
        var startsOnDate = NewSchedule.StartsOn!.Value;
        var startLocal = new LocalTime(startLocalTime.Hour, startLocalTime.Minute);
        var startsOn = new LocalDate(startsOnDate.Year, startsOnDate.Month, startsOnDate.Day);

        try
        {
            var actingUserId = long.Parse(_userManager.GetUserId(User)!);
            // Owner decision D-98: a group session seats exactly four, and
            // "no seat count entered from the interface or the request body is
            // accepted". The form used to offer a 1-10 box whose value was then
            // ignored anyway — ScheduleGenerationService derives every session's
            // capacity from its type, never from the schedule's stored number —
            // so the admin could type 7 and silently get 4. The field is gone and
            // the stored value is now the same one the sessions really get.
            var groupCapacity = MVTeaches.Domain.Scheduling.ClassSession.CapacityFor(SessionType.Group);
            await _schedules.CreateAsync(NewSchedule.CountryId!.Value, NewSchedule.CourseId!.Value, NewSchedule.LevelId!.Value,
                NewSchedule.AgeGroupId!.Value, NewSchedule.TeacherId!.Value, days, startLocal, NewSchedule.DurationMinutes,
                NewSchedule.TimeZoneId, startsOn, groupCapacity, actingUserId, HttpContext.RequestAborted);
            StatusMessage = _localizer["Recurring schedule created — it will start producing sessions on the next nightly generation run (or an admin's manual \"Trigger now\" on /hangfire)."].Value;
        }
        catch (ArgumentException ex)
        {
            ErrorMessage = ex.Message;
        }

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostEnrollAsync()
    {
        ActiveTab = "enroll";
        ModelState.Clear();
        if (!TryValidateModel(Enroll, nameof(Enroll)))
        {
            await LoadAsync();
            return Page();
        }

        var actingUserId = long.Parse(_userManager.GetUserId(User)!);
        var count = await _enrollments.EnrollInUpcomingSessionsAsync(Enroll.RecurringScheduleId!.Value, Enroll.StudentId!.Value,
            actingUserId, HttpContext.RequestAborted);
        StatusMessage = _localizer["Enrolled the student into {0} upcoming session(s) generated from this schedule.", count].Value;

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostCancelAsync()
    {
        ActiveTab = "cancel";
        ModelState.Clear();
        if (!TryValidateModel(Cancel, nameof(Cancel)))
        {
            await LoadAsync();
            return Page();
        }

        var actingUserId = long.Parse(_userManager.GetUserId(User)!);
        var result = await _cancellations.CancelAsync(Cancel.SessionId!.Value, Cancel.Reason, actingUserId,
            Cancel.ReplacementSessionId, HttpContext.RequestAborted);

        switch (result.Outcome)
        {
            case CancelSessionOutcome.Cancelled:
                // Used to name the raw path "/Admin/RescheduleSessions" —
                // the destination is now named in words instead.
                StatusMessage = Cancel.ReplacementSessionId is null
                    ? _localizer["Session cancelled. {0} enrollment(s) cancelled; {1} already-joined student(s) left untouched — approve a specific replacement lesson for them from the Reschedule / Compensation screen if appropriate.",
                        result.EnrollmentsMovedOrCancelled, result.EnrollmentsLeftUntouchedBecauseAlreadyConsumed].Value
                    : _localizer["Session cancelled and replaced. {0} student(s) moved to the replacement; {1} could not fit and need manual attention; {2} already-joined student(s) left untouched.",
                        result.EnrollmentsMovedOrCancelled, result.EnrollmentsThatCouldNotBeMovedToReplacement, result.EnrollmentsLeftUntouchedBecauseAlreadyConsumed].Value;
                break;
            case CancelSessionOutcome.SessionNotFound:
                ErrorMessage = _localizer["Session not found."].Value;
                break;
            case CancelSessionOutcome.NotCancellable:
                ErrorMessage = _localizer["This session is already cancelled, completed, or marked not delivered."].Value;
                break;
            case CancelSessionOutcome.ReplacementSessionNotFound:
                ErrorMessage = _localizer["The replacement session id was not found."].Value;
                break;
            case CancelSessionOutcome.ReplacementSessionIsTheSameSession:
                ErrorMessage = _localizer["The replacement session must be a different session."].Value;
                break;
        }

        await LoadAsync();
        return Page();
    }

    /// <summary>
    /// Owner clarification (2026-08-29): "If the assigned teacher changes
    /// before an unstarted session, do not reuse the former teacher's
    /// meeting." IMeetingProvisioningService owns the whole sequence —
    /// cancel the old meeting under its own owning connection, reprovision
    /// lazily under the new teacher, audit, and notify enrolled students —
    /// so this handler only validates input and reports the outcome.
    /// </summary>
    public async Task<IActionResult> OnPostReassignAsync()
    {
        ActiveTab = "reassign";
        ModelState.Clear();
        if (!TryValidateModel(Reassign, nameof(Reassign)))
        {
            await LoadAsync();
            return Page();
        }

        var actingUserId = long.Parse(_userManager.GetUserId(User)!);
        var result = await _meetings.ReassignTeacherAsync(Reassign.SessionId!.Value, Reassign.NewTeacherId!.Value, actingUserId,
            HttpContext.RequestAborted);

        if (result.Outcome == TeacherReassignmentOutcome.Reassigned)
        {
            StatusMessage = _localizer["Teacher reassigned. The previous meeting was cancelled (or flagged for admin attention if its account was already revoked); a fresh meeting is created under the new teacher when the session is next started or joined. Enrolled students have been notified."].Value;
        }
        else
        {
            ErrorMessage = result.Outcome switch
            {
                TeacherReassignmentOutcome.SessionNotFound => _localizer["Session not found."].Value,
                TeacherReassignmentOutcome.SessionNotReassignable => result.Detail ?? _localizer["That session can no longer be reassigned."].Value,
                TeacherReassignmentOutcome.NewTeacherOverlaps => result.Detail ?? _localizer["The new teacher already has a session at that time."].Value,
                TeacherReassignmentOutcome.NewTeacherNotReadyForOnlineSessions => result.Detail
                    ?? _localizer["That teacher has no connected Zoom or Google Meet account."].Value,
                _ => _localizer["Could not reassign the teacher."].Value,
            };
        }

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostPauseAsync(long scheduleId)
    {
        ActiveTab = "create";
        await _schedules.PauseAsync(scheduleId, HttpContext.RequestAborted);
        StatusMessage = _localizer["Schedule paused — future occurrences stop generating; nothing already generated is affected."].Value;
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostResumeAsync(long scheduleId)
    {
        ActiveTab = "create";
        await _schedules.ResumeAsync(scheduleId, HttpContext.RequestAborted);
        StatusMessage = _localizer["Schedule resumed."].Value;
        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        Countries = await _db.Countries.Where(c => c.IsActive).OrderBy(c => c.NameEn).ToListAsync();
        Courses = await _db.Courses.Where(c => c.IsActive).OrderBy(c => c.NameEn).ToListAsync();
        Levels = await _db.Levels.Where(l => l.IsActive).OrderBy(l => l.SortOrder).ToListAsync();
        AgeGroups = await _db.AgeGroups.OrderBy(a => a.MinAge).ToListAsync();
        Teachers = await _db.Teachers.Where(t => t.IsActive).OrderBy(t => t.FullName).ToListAsync();
        TimeZoneIds = DateTimeZoneProviders.Tzdb.Ids.OrderBy(id => id, StringComparer.Ordinal).ToList();

        var readyTeacherIds = (await _db.TeacherMeetingConnections
            .Where(c => c.Status == ProviderConnectionStatus.Connected)
            .Select(c => c.TeacherId)
            .Distinct()
            .ToListAsync()).ToHashSet();
        TeachersNotReadyForOnlineSessions = Teachers.Select(t => t.Id).Where(id => !readyTeacherIds.Contains(id)).ToHashSet();

        var students = await _db.Students.OrderByDescending(s => s.Id).Take(200).ToListAsync();

        var teacherNames = Teachers.ToDictionary(t => t.Id, t => t.FullName);
        var courseNames = Courses.ToDictionary(c => c.Id, c => c.NameEn);
        var levelCodes = Levels.ToDictionary(l => l.Id, l => l.Code);
        var ageGroupCodes = AgeGroups.ToDictionary(a => a.Id, a => a.Code);

        // Same source Admin/Subscriptions reads its own student-level pickers
        // from, kept as its own query here rather than a shared helper,
        // matching how each admin page already keeps its own copy.
        var currentLevelByStudent = (await _db.StudentLevels.Where(l => l.IsCurrent).ToListAsync())
            .ToDictionary(l => l.StudentId, l => l.LevelId);
        Students = students.Select(s =>
        {
            var levelId = currentLevelByStudent.TryGetValue(s.Id, out var found) ? found : (int?)null;
            return new StudentPickRow(s.Id, s.FullName, levelId, levelId is null ? null : levelCodes.GetValueOrDefault(levelId.Value));
        }).ToList();

        if (FocusStudentId is not null)
        {
            var focus = Students.FirstOrDefault(s => s.Id == FocusStudentId.Value);
            FocusStudentName = focus?.FullName;
            FocusStudentLevelCode = focus?.CurrentLevelCode;
            Enroll.StudentId ??= FocusStudentId;
        }

        var schedules = await _db.RecurringSchedules.OrderByDescending(s => s.Id).ToListAsync();
        Schedules = schedules.Select(s => new ScheduleRow(s.Id, teacherNames.GetValueOrDefault(s.TeacherId, string.Empty),
            courseNames.GetValueOrDefault(s.CourseId, "?"), s.LevelId, levelCodes.GetValueOrDefault(s.LevelId, "?"),
            ageGroupCodes.GetValueOrDefault(s.AgeGroupId, "?"),
            string.Join(",", s.DaysOfWeek.Select(d => _localizer["DayOfWeek." + d].Value)),
            s.StartLocal.ToString("HH:mm", null), s.DurationMinutes, s.TimeZoneId, s.Status)).ToList();

        var now = _clock.GetCurrentInstant();
        var horizon = now.Plus(Duration.FromDays(14));
        var sessions = await _db.ClassSessions
            .Where(s => s.Status == ClassSessionStatus.Scheduled && s.StartsAtUtc >= now && s.StartsAtUtc <= horizon)
            .OrderBy(s => s.StartsAtUtc)
            .ToListAsync();
        UpcomingSessions = sessions.Select(s => new SessionRow(s.Id, teacherNames.GetValueOrDefault(s.TeacherId, string.Empty),
            courseNames.GetValueOrDefault(s.CourseId, "?"), levelCodes.GetValueOrDefault(s.LevelId, "?"),
            s.StartsAtUtc, s.ScheduleTimeZone, s.SeatsTaken, s.Capacity, s.Status)).ToList();
    }
}
