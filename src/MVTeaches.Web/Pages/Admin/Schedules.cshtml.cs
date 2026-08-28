using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Integrations;
using MVTeaches.Application.Scheduling;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Integrations;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
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

    public SchedulesModel(MvTeachesDbContext db, IRecurringScheduleService schedules, IEnrollmentService enrollments,
        ISessionCancellationService cancellations, IMeetingProvisioningService meetings, IClock clock,
        UserManager<ApplicationUser> userManager)
    {
        _db = db;
        _schedules = schedules;
        _enrollments = enrollments;
        _cancellations = cancellations;
        _meetings = meetings;
        _clock = clock;
        _userManager = userManager;
    }

    public record ScheduleRow(long Id, string TeacherName, string CourseName, string LevelCode, string AgeGroupCode,
        string Days, string StartLocal, int DurationMinutes, string TimeZoneId, RecurringScheduleStatus Status);

    /// <summary>An upcoming, still-cancellable session — the admin needs to see
    /// a real session Id to act on it; nothing before this page ever surfaced one.</summary>
    public record SessionRow(long Id, string TeacherName, string CourseName, string LevelCode,
        Instant StartsAtUtc, int SeatsTaken, int Capacity, ClassSessionStatus Status);

    public IReadOnlyList<ScheduleRow> Schedules { get; set; } = Array.Empty<ScheduleRow>();
    public IReadOnlyList<SessionRow> UpcomingSessions { get; set; } = Array.Empty<SessionRow>();
    public IReadOnlyList<Country> Countries { get; set; } = Array.Empty<Country>();
    public IReadOnlyList<Course> Courses { get; set; } = Array.Empty<Course>();
    public IReadOnlyList<Level> Levels { get; set; } = Array.Empty<Level>();
    public IReadOnlyList<AgeGroup> AgeGroups { get; set; } = Array.Empty<AgeGroup>();
    public IReadOnlyList<MVTeaches.Domain.People.Teacher> Teachers { get; set; } = Array.Empty<MVTeaches.Domain.People.Teacher>();
    public IReadOnlyList<string> TimeZoneIds { get; set; } = Array.Empty<string>();
    public IReadOnlyList<MVTeaches.Domain.People.Student> Students { get; set; } = Array.Empty<MVTeaches.Domain.People.Student>();

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

    public class ReassignInput
    {
        [Required] public long SessionId { get; set; }
        [Required] public long NewTeacherId { get; set; }
    }

    public class EnrollInput
    {
        [Required] public long RecurringScheduleId { get; set; }
        [Required] public long StudentId { get; set; }
    }

    public class CancelInput
    {
        [Required] public long SessionId { get; set; }
        [Required] public string Reason { get; set; } = string.Empty;
        public long? ReplacementSessionId { get; set; }
    }

    public class CreateScheduleInput
    {
        [Required] public int CountryId { get; set; }
        [Required] public long CourseId { get; set; }
        [Required] public int LevelId { get; set; }
        [Required] public int AgeGroupId { get; set; }
        [Required] public long TeacherId { get; set; }

        /// <summary>ISO day numbers (1=Monday..7=Sunday) — bound from a set of checkboxes.</summary>
        [Required, MinLength(1)]
        public List<int> DaysOfWeek { get; set; } = new();

        [Required] public TimeOnly StartLocal { get; set; }
        [Required, Range(1, 480)] public int DurationMinutes { get; set; } = 60;
        [Required] public string TimeZoneId { get; set; } = string.Empty;
        [Required] public DateOnly StartsOn { get; set; }
        [Required, Range(1, 10)] public int Capacity { get; set; } = 4;
    }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostCreateAsync()
    {
        ModelState.Clear();
        if (!TryValidateModel(NewSchedule, nameof(NewSchedule)))
        {
            await LoadAsync();
            return Page();
        }

        var days = NewSchedule.DaysOfWeek.Select(d => (IsoDayOfWeek)d).ToList();
        var startLocal = new LocalTime(NewSchedule.StartLocal.Hour, NewSchedule.StartLocal.Minute);
        var startsOn = new LocalDate(NewSchedule.StartsOn.Year, NewSchedule.StartsOn.Month, NewSchedule.StartsOn.Day);

        try
        {
            var actingUserId = long.Parse(_userManager.GetUserId(User)!);
            await _schedules.CreateAsync(NewSchedule.CountryId, NewSchedule.CourseId, NewSchedule.LevelId,
                NewSchedule.AgeGroupId, NewSchedule.TeacherId, days, startLocal, NewSchedule.DurationMinutes,
                NewSchedule.TimeZoneId, startsOn, NewSchedule.Capacity, actingUserId, HttpContext.RequestAborted);
            StatusMessage = "Recurring schedule created — it will start producing sessions on the next nightly generation run (or an admin's manual \"Trigger now\" on /hangfire).";
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
        ModelState.Clear();
        if (!TryValidateModel(Enroll, nameof(Enroll)))
        {
            await LoadAsync();
            return Page();
        }

        var actingUserId = long.Parse(_userManager.GetUserId(User)!);
        var count = await _enrollments.EnrollInUpcomingSessionsAsync(Enroll.RecurringScheduleId, Enroll.StudentId,
            actingUserId, HttpContext.RequestAborted);
        StatusMessage = $"Enrolled the student into {count} upcoming session(s) generated from this schedule.";

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostCancelAsync()
    {
        ModelState.Clear();
        if (!TryValidateModel(Cancel, nameof(Cancel)))
        {
            await LoadAsync();
            return Page();
        }

        var actingUserId = long.Parse(_userManager.GetUserId(User)!);
        var result = await _cancellations.CancelAsync(Cancel.SessionId, Cancel.Reason, actingUserId,
            Cancel.ReplacementSessionId, HttpContext.RequestAborted);

        switch (result.Outcome)
        {
            case CancelSessionOutcome.Cancelled:
                StatusMessage = Cancel.ReplacementSessionId is null
                    ? $"Session cancelled. {result.EnrollmentsMovedOrCancelled} enrollment(s) cancelled; " +
                      $"{result.EnrollmentsLeftUntouchedBecauseAlreadyConsumed} already-joined student(s) left untouched " +
                      "(approve a specific replacement lesson for them on /Admin/RescheduleSessions if appropriate)."
                    : $"Session cancelled and replaced. {result.EnrollmentsMovedOrCancelled} student(s) moved to the replacement; " +
                      $"{result.EnrollmentsThatCouldNotBeMovedToReplacement} could not fit and need manual attention; " +
                      $"{result.EnrollmentsLeftUntouchedBecauseAlreadyConsumed} already-joined student(s) left untouched.";
                break;
            case CancelSessionOutcome.SessionNotFound:
                ErrorMessage = "Session not found.";
                break;
            case CancelSessionOutcome.NotCancellable:
                ErrorMessage = "This session is already cancelled, completed, or marked not delivered.";
                break;
            case CancelSessionOutcome.ReplacementSessionNotFound:
                ErrorMessage = "The replacement session id was not found.";
                break;
            case CancelSessionOutcome.ReplacementSessionIsTheSameSession:
                ErrorMessage = "The replacement session must be a different session.";
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
        ModelState.Clear();
        if (!TryValidateModel(Reassign, nameof(Reassign)))
        {
            await LoadAsync();
            return Page();
        }

        var actingUserId = long.Parse(_userManager.GetUserId(User)!);
        var result = await _meetings.ReassignTeacherAsync(Reassign.SessionId, Reassign.NewTeacherId, actingUserId,
            HttpContext.RequestAborted);

        if (result.Outcome == TeacherReassignmentOutcome.Reassigned)
        {
            StatusMessage = "Teacher reassigned. The previous meeting was cancelled (or flagged for admin attention if its " +
                            "account was already revoked); a fresh meeting is created under the new teacher when the session " +
                            "is next started or joined. Enrolled students have been notified.";
        }
        else
        {
            ErrorMessage = result.Outcome switch
            {
                TeacherReassignmentOutcome.SessionNotFound => "Session not found.",
                TeacherReassignmentOutcome.SessionNotReassignable => result.Detail ?? "That session can no longer be reassigned.",
                TeacherReassignmentOutcome.NewTeacherOverlaps => result.Detail ?? "The new teacher already has a session at that time.",
                TeacherReassignmentOutcome.NewTeacherNotReadyForOnlineSessions => result.Detail
                    ?? "That teacher has no connected Zoom or Google Meet account.",
                _ => "Could not reassign the teacher.",
            };
        }

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostPauseAsync(long scheduleId)
    {
        await _schedules.PauseAsync(scheduleId, HttpContext.RequestAborted);
        StatusMessage = "Schedule paused — future occurrences stop generating; nothing already generated is affected.";
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostResumeAsync(long scheduleId)
    {
        await _schedules.ResumeAsync(scheduleId, HttpContext.RequestAborted);
        StatusMessage = "Schedule resumed.";
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

        Students = await _db.Students.OrderByDescending(s => s.Id).Take(200).ToListAsync();

        var teacherNames = Teachers.ToDictionary(t => t.Id, t => t.FullName);
        var courseNames = Courses.ToDictionary(c => c.Id, c => c.NameEn);
        var levelCodes = Levels.ToDictionary(l => l.Id, l => l.Code);
        var ageGroupCodes = AgeGroups.ToDictionary(a => a.Id, a => a.Code);

        var schedules = await _db.RecurringSchedules.OrderByDescending(s => s.Id).ToListAsync();
        Schedules = schedules.Select(s => new ScheduleRow(s.Id, teacherNames.GetValueOrDefault(s.TeacherId, $"#{s.TeacherId}"),
            courseNames.GetValueOrDefault(s.CourseId, "?"), levelCodes.GetValueOrDefault(s.LevelId, "?"),
            ageGroupCodes.GetValueOrDefault(s.AgeGroupId, "?"), string.Join(",", s.DaysOfWeek),
            s.StartLocal.ToString("HH:mm", null), s.DurationMinutes, s.TimeZoneId, s.Status)).ToList();

        var now = _clock.GetCurrentInstant();
        var horizon = now.Plus(Duration.FromDays(14));
        var sessions = await _db.ClassSessions
            .Where(s => s.Status == ClassSessionStatus.Scheduled && s.StartsAtUtc >= now && s.StartsAtUtc <= horizon)
            .OrderBy(s => s.StartsAtUtc)
            .ToListAsync();
        UpcomingSessions = sessions.Select(s => new SessionRow(s.Id, teacherNames.GetValueOrDefault(s.TeacherId, $"#{s.TeacherId}"),
            courseNames.GetValueOrDefault(s.CourseId, "?"), levelCodes.GetValueOrDefault(s.LevelId, "?"),
            s.StartsAtUtc, s.SeatsTaken, s.Capacity, s.Status)).ToList();
    }
}
