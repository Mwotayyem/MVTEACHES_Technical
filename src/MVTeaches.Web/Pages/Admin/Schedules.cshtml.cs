using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Scheduling;
using MVTeaches.Domain.Catalog;
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
    private readonly UserManager<ApplicationUser> _userManager;

    public SchedulesModel(MvTeachesDbContext db, IRecurringScheduleService schedules, UserManager<ApplicationUser> userManager)
    {
        _db = db;
        _schedules = schedules;
        _userManager = userManager;
    }

    public record ScheduleRow(long Id, string TeacherName, string CourseName, string LevelCode, string AgeGroupCode,
        string Days, string StartLocal, int DurationMinutes, string TimeZoneId, RecurringScheduleStatus Status);

    public IReadOnlyList<ScheduleRow> Schedules { get; set; } = Array.Empty<ScheduleRow>();
    public IReadOnlyList<Country> Countries { get; set; } = Array.Empty<Country>();
    public IReadOnlyList<Course> Courses { get; set; } = Array.Empty<Course>();
    public IReadOnlyList<Level> Levels { get; set; } = Array.Empty<Level>();
    public IReadOnlyList<AgeGroup> AgeGroups { get; set; } = Array.Empty<AgeGroup>();
    public IReadOnlyList<MVTeaches.Domain.People.Teacher> Teachers { get; set; } = Array.Empty<MVTeaches.Domain.People.Teacher>();
    public IReadOnlyList<string> TimeZoneIds { get; set; } = Array.Empty<string>();

    [BindProperty]
    public CreateScheduleInput NewSchedule { get; set; } = new();

    public string? StatusMessage { get; set; }
    public string? ErrorMessage { get; set; }

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

        var teacherNames = Teachers.ToDictionary(t => t.Id, t => t.FullName);
        var courseNames = Courses.ToDictionary(c => c.Id, c => c.NameEn);
        var levelCodes = Levels.ToDictionary(l => l.Id, l => l.Code);
        var ageGroupCodes = AgeGroups.ToDictionary(a => a.Id, a => a.Code);

        var schedules = await _db.RecurringSchedules.OrderByDescending(s => s.Id).ToListAsync();
        Schedules = schedules.Select(s => new ScheduleRow(s.Id, teacherNames.GetValueOrDefault(s.TeacherId, $"#{s.TeacherId}"),
            courseNames.GetValueOrDefault(s.CourseId, "?"), levelCodes.GetValueOrDefault(s.LevelId, "?"),
            ageGroupCodes.GetValueOrDefault(s.AgeGroupId, "?"), string.Join(",", s.DaysOfWeek),
            s.StartLocal.ToString("HH:mm", null), s.DurationMinutes, s.TimeZoneId, s.Status)).ToList();
    }
}
