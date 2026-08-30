using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using MVTeaches.Application.People;
using MVTeaches.Application.Scheduling;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using MVTeaches.Web.Resources;
using NodaTime;

namespace MVTeaches.Web.Pages.Teacher;

/// <summary>
/// Owner decision 2026-08-30 rule 7: "the teacher creates and manages
/// available session slots within their permitted levels — an authorized
/// level, date and start time, scheduled duration, Group or Private."
/// Distinct from the admin-only RecurringScheduleService (a weekly roster):
/// this publishes one concrete, one-off ClassSession directly through
/// ITeacherSlotPublishingService, which re-derives capacity from the chosen
/// SessionType and re-checks level authorization/ownership/video-readiness
/// server-side — this page only shapes the form and reports the outcome.
/// </summary>
[Authorize(Roles = RoleNames.Teacher)]
public class PublishSlotsModel : PageModel
{
    private readonly MvTeachesDbContext _db;
    private readonly ITeacherSlotPublishingService _publishing;
    private readonly ITeacherLevelAuthorizationService _levelAuthorization;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IClock _clock;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public PublishSlotsModel(MvTeachesDbContext db, ITeacherSlotPublishingService publishing,
        ITeacherLevelAuthorizationService levelAuthorization, UserManager<ApplicationUser> userManager, IClock clock,
        IStringLocalizer<SharedResource> localizer)
    {
        _db = db;
        _publishing = publishing;
        _levelAuthorization = levelAuthorization;
        _userManager = userManager;
        _clock = clock;
        _localizer = localizer;
    }

    public record SlotRow(long SessionId, string LocalDateAndTime, int DurationMinutes,
        SessionType SessionType, string CourseName, string LevelCode, string AgeGroupCode, ClassSessionStatus Status,
        int SeatsTaken, int Capacity);

    public IReadOnlyList<SlotRow> UpcomingSlots { get; set; } = Array.Empty<SlotRow>();
    public IReadOnlyList<Country> Countries { get; set; } = Array.Empty<Country>();
    public IReadOnlyList<Course> Courses { get; set; } = Array.Empty<Course>();
    public IReadOnlyList<AgeGroup> AgeGroups { get; set; } = Array.Empty<AgeGroup>();

    /// <summary>Only the levels this teacher is actually authorized for — the
    /// server re-checks this independently in
    /// ITeacherSlotPublishingService.PublishSlotAsync, but restricting the
    /// dropdown to them means an honest teacher never sees a choice that can
    /// only fail.</summary>
    public IReadOnlyList<Level> AuthorizedLevels { get; set; } = Array.Empty<Level>();

    public bool NoTeacherProfileLinked { get; set; }

    [BindProperty]
    public PublishSlotInput NewSlot { get; set; } = new();

    public string? StatusMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public class PublishSlotInput
    {
        [Required] public int CountryId { get; set; }
        [Required] public long CourseId { get; set; }
        [Required] public int LevelId { get; set; }
        [Required] public int AgeGroupId { get; set; }

        // Nullable, not DateOnly/TimeOnly: a real, reported bug — on the
        // blank "publish a new slot" form (nothing typed yet), a
        // non-nullable DateOnly/TimeOnly defaults to 0001-01-01/00:00, and
        // asp-for renders that CLR default straight into the date/time
        // input's value attribute (a plainly wrong pre-filled date, not an
        // empty field). [Required] below still enforces a real value on
        // submit exactly as before.
        [Required] public DateOnly? Date { get; set; }
        [Required] public TimeOnly? StartLocal { get; set; }

        [Required, Range(1, 480)] public int DurationMinutes { get; set; } = 60;
        [Required] public SessionType SessionType { get; set; } = SessionType.Group;
    }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostAsync()
    {
        ModelState.Clear();
        if (!TryValidateModel(NewSlot, nameof(NewSlot)))
        {
            await LoadAsync();
            return Page();
        }

        var userId = long.Parse(_userManager.GetUserId(User)!);
        var teacher = await _db.Teachers.FirstOrDefaultAsync(t => t.UserId == userId);
        if (teacher is null)
        {
            NoTeacherProfileLinked = true;
            return Page();
        }

        // A teacher publishes in their OWN registered time zone — rule 7 lists
        // "date and start time", not a separate zone picker, and every other
        // teacher-facing screen already treats Teacher.TimeZoneId as that
        // teacher's own frame of reference (e.g. MySessions' declared hours).
        var zone = DateTimeZoneProviders.Tzdb[teacher.TimeZoneId];
        var date = NewSlot.Date!.Value; // [Required] + TryValidateModel above guarantee this
        var startLocal = NewSlot.StartLocal!.Value;
        var localDate = new LocalDate(date.Year, date.Month, date.Day);
        var localTime = new LocalTime(startLocal.Hour, startLocal.Minute);
        var startInstant = zone.AtLeniently(localDate.At(localTime)).ToInstant();

        var result = await _publishing.PublishSlotAsync(teacher.Id, userId, NewSlot.CountryId, NewSlot.CourseId,
            NewSlot.LevelId, NewSlot.AgeGroupId, startInstant, NewSlot.DurationMinutes, teacher.TimeZoneId,
            localTime.ToString("HH:mm", CultureInfo.InvariantCulture), NewSlot.SessionType, HttpContext.RequestAborted);

        ErrorMessage = result.Outcome switch
        {
            PublishSlotOutcome.Published => null,
            // This page always passes the authenticated teacher's own Id, so
            // this branch is unreachable through the UI itself — it only
            // guards against PublishSlotAsync's own defense-in-depth check.
            PublishSlotOutcome.Unauthorized => _localizer["Could not publish this slot."].Value,
            PublishSlotOutcome.TeacherNotReadyForOnlineSessions =>
                _localizer["You have no connected video account yet — connect Zoom or a free Google account on the Connections page first."].Value,
            PublishSlotOutcome.NotAuthorizedForLevel => _localizer["You are not authorized to teach this level — ask an admin to grant it on your profile."].Value,
            PublishSlotOutcome.Overlapping => _localizer["This overlaps another slot you already published."].Value,
            _ => _localizer["Could not publish this slot."].Value,
        };
        if (result.Outcome == PublishSlotOutcome.Published)
        {
            StatusMessage = _localizer["Slot published (session #{0}).", result.SessionId!].Value;
        }

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
            UpcomingSlots = Array.Empty<SlotRow>();
            return;
        }

        Countries = await _db.Countries.Where(c => c.IsActive).OrderBy(c => c.NameEn).ToListAsync();
        Courses = await _db.Courses.Where(c => c.IsActive).OrderBy(c => c.NameEn).ToListAsync();
        AgeGroups = await _db.AgeGroups.OrderBy(a => a.MinAge).ToListAsync();

        var permittedLevelIds = await _levelAuthorization.GetPermittedLevelIdsAsync(teacher.Id, HttpContext.RequestAborted);
        AuthorizedLevels = await _db.Levels.Where(l => permittedLevelIds.Contains(l.Id)).OrderBy(l => l.SortOrder).ToListAsync();

        var courseNameById = Courses.ToDictionary(c => c.Id, c => c.NameEn);
        var levelCodes = await _db.Levels.ToDictionaryAsync(l => l.Id, l => l.Code);
        var ageGroupCodes = AgeGroups.ToDictionary(a => a.Id, a => a.Code);

        var now = _clock.GetCurrentInstant();
        var sessions = await _db.ClassSessions
            .Where(s => s.TeacherId == teacher.Id && s.StartsAtUtc >= now.Minus(Duration.FromDays(1))
                        && s.RecurringScheduleId == null) // this page's own one-off slots only, not the admin weekly roster
            .OrderBy(s => s.StartsAtUtc)
            .ToListAsync();

        UpcomingSlots = sessions.Select(s =>
        {
            var zone = DateTimeZoneProviders.Tzdb[s.ScheduleTimeZone];
            var local = s.StartsAtUtc.InZone(zone).LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            return new SlotRow(s.Id, local, s.DurationMinutes, s.SessionType,
                courseNameById.GetValueOrDefault(s.CourseId, "?"), levelCodes.GetValueOrDefault(s.LevelId, "?"),
                ageGroupCodes.GetValueOrDefault(s.AgeGroupId, "?"), s.Status, s.SeatsTaken, s.Capacity);
        }).ToList();
    }
}
