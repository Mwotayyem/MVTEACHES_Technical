using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using MVTeaches.Application.Payroll;
using MVTeaches.Application.People;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Common;
using MVTeaches.Domain.Payroll;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using MVTeaches.Web.Resources;
using NodaTime;

namespace MVTeaches.Web.Pages.Admin;

/// <summary>
/// §9.1/D-28 — before this page there was NO way to create a Teacher account
/// anywhere in the application, which meant the whole payroll surface
/// (declare/verify/pay) had nothing real to operate on. Mirrors the
/// guardian-registration pattern on /Admin/Students exactly.
/// </summary>
[Authorize(Roles = RoleNames.Admin + "," + RoleNames.SystemAdmin)]
public class TeachersModel : PageModel
{
    private readonly MvTeachesDbContext _db;
    private readonly ITeacherAdmissionService _teachers;
    private readonly ITeacherRateService _rates;
    private readonly ITeacherLevelAuthorizationService _levelAuthorization;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public TeachersModel(MvTeachesDbContext db, ITeacherAdmissionService teachers, ITeacherRateService rates,
        ITeacherLevelAuthorizationService levelAuthorization, UserManager<ApplicationUser> userManager,
        IStringLocalizer<SharedResource> localizer)
    {
        _db = db;
        _teachers = teachers;
        _rates = rates;
        _levelAuthorization = levelAuthorization;
        _userManager = userManager;
        _localizer = localizer;
    }

    // Fully qualified to avoid ambiguity with the sibling MVTeaches.Web.Pages.Teacher namespace.
    public IReadOnlyList<MVTeaches.Domain.People.Teacher> Teachers { get; set; } = Array.Empty<MVTeaches.Domain.People.Teacher>();

    /// <summary>Every IANA zone NodaTime's own tzdb copy knows about — §14.4
    /// rule 5 requires a real IANA id, never a Windows time zone name, so this
    /// is populated from the same data source the app itself validates against
    /// rather than a hand-typed, easily-mistyped free-text field.</summary>
    public IReadOnlyList<string> TimeZoneIds { get; set; } = Array.Empty<string>();
    public IReadOnlyList<Course> Courses { get; set; } = Array.Empty<Course>();
    public IReadOnlyList<Level> Levels { get; set; } = Array.Empty<Level>();
    public IReadOnlyList<AgeGroup> AgeGroups { get; set; } = Array.Empty<AgeGroup>();

    public record RateRow(long Id, string TeacherName, string? CourseName, string? LevelCode, string? AgeGroupCode,
        decimal Amount, string Currency, RateUnit Unit, LocalDate EffectiveFrom, LocalDate? EffectiveTo);

    public IReadOnlyList<RateRow> Rates { get; set; } = Array.Empty<RateRow>();

    /// <summary>Owner clarification (2026-08-29): teacher ids with NO usable
    /// Zoom/Google Meet connection — "Not ready for online sessions", and
    /// blocked from being assigned any (RecurringScheduleService and
    /// IMeetingProvisioningService.ReassignTeacherAsync each enforce that
    /// server-side; this set only makes it visible to the admin here).</summary>
    public IReadOnlySet<long> TeachersNotReadyForOnlineSessions { get; set; } = new HashSet<long>();

    /// <summary>Owner decision 2026-08-30 rule 6: every level currently
    /// granted to each teacher, for the "view teachers and assigned levels"
    /// half of this screen.</summary>
    public IReadOnlyDictionary<long, IReadOnlyList<Level>> LevelsByTeacher { get; set; } =
        new Dictionary<long, IReadOnlyList<Level>>();

    [BindProperty]
    public RegisterTeacherInput NewTeacher { get; set; } = new();

    [BindProperty]
    public CreateRateInput NewRate { get; set; } = new();

    [BindProperty]
    public LevelGrantInput LevelGrant { get; set; } = new();

    public string? StatusMessage { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>Currency codes from the configured countries, home market
    /// first — the pay-rate form picks one instead of typing it.</summary>
    public IReadOnlyList<string> Currencies { get; set; } = Array.Empty<string>();

    // Ids and dates are nullable so [Required] actually fires on an untouched
    // picker; on a non-nullable value type the empty post binds to 0 /
    // 0001-01-01 and passes validation silently.
    public class LevelGrantInput
    {
        [Required(ErrorMessage = "Choose a teacher.")] public long? TeacherId { get; set; }
        [Required(ErrorMessage = "Choose a level.")] public int? LevelId { get; set; }
    }

    public class RegisterTeacherInput
    {
        [Required(ErrorMessage = "Enter an email address."), EmailAddress(ErrorMessage = "This is not a valid email address.")]
        public string Email { get; set; } = string.Empty;

        [Required(ErrorMessage = "Enter a temporary password."), MinLength(8, ErrorMessage = "The password must be at least 8 characters.")]
        public string Password { get; set; } = string.Empty;

        [Required(ErrorMessage = "Enter the full name.")]
        public string FullName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Choose a time zone.")]
        public string TimeZoneId { get; set; } = string.Empty;
    }

    public class CreateRateInput
    {
        [Required(ErrorMessage = "Choose a teacher.")] public long? TeacherId { get; set; }

        /// <summary>§9.2's most-specific-wins rule: blank means "applies to
        /// every course/level/age-group" at that dimension.</summary>
        public long? CourseId { get; set; }
        public int? LevelId { get; set; }
        public int? AgeGroupId { get; set; }

        [Required, Range(0, double.MaxValue, ErrorMessage = "Enter the rate amount.")] public decimal Amount { get; set; }
        [Required(ErrorMessage = "Choose a currency."), StringLength(3, MinimumLength = 3)] public string Currency { get; set; } = string.Empty;
        [Required] public RateUnit Unit { get; set; }
        [Required(ErrorMessage = "Enter the date this rate starts.")] public DateOnly? EffectiveFrom { get; set; }
    }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostRegisterAsync()
    {
        ModelState.Clear();
        if (!TryValidateModel(NewTeacher, nameof(NewTeacher)))
        {
            await LoadAsync();
            return Page();
        }

        var result = await _teachers.RegisterTeacherAsync(NewTeacher.Email, NewTeacher.Password, NewTeacher.FullName,
            NewTeacher.TimeZoneId, HttpContext.RequestAborted);

        if (result.Outcome == RegisterTeacherOutcome.LoginFailed)
        {
            ErrorMessage = _localizer["Could not create the teacher's login: {0}", string.Join("; ", result.Errors ?? Array.Empty<string>())].Value;
        }
        else
        {
            StatusMessage = _localizer["Teacher '{0}' registered.", NewTeacher.FullName].Value;
        }

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostCreateRateAsync()
    {
        ModelState.Clear();
        if (!TryValidateModel(NewRate, nameof(NewRate)))
        {
            await LoadAsync();
            return Page();
        }

        var from = NewRate.EffectiveFrom!.Value;
        var effectiveFrom = new LocalDate(from.Year, from.Month, from.Day);
        var actingUserId = long.Parse(_userManager.GetUserId(User)!);

        try
        {
            await _rates.CreateRateAsync(NewRate.TeacherId!.Value, NewRate.CourseId, NewRate.LevelId, NewRate.AgeGroupId,
                new Money(NewRate.Amount, NewRate.Currency), NewRate.Unit, effectiveFrom, actingUserId, HttpContext.RequestAborted);
            StatusMessage = _localizer["Rate created."].Value;
        }
        catch (ArgumentOutOfRangeException ex)
        {
            ErrorMessage = ex.Message;
        }

        await LoadAsync();
        return Page();
    }

    /// <summary>Owner decision 2026-08-30 rule 6: "add allowed levels,
    /// prevent duplicates" — TeacherId/LevelId are bound to a dedicated input
    /// DTO, never a domain entity (no over-posting surface), and duplicate
    /// prevention is enforced server-side by
    /// ITeacherLevelAuthorizationService itself (ux_teacher_level), not
    /// merely by hiding an already-granted level in this form.</summary>
    public async Task<IActionResult> OnPostGrantLevelAsync()
    {
        ModelState.Clear();
        if (!TryValidateModel(LevelGrant, nameof(LevelGrant)))
        {
            await LoadAsync();
            return Page();
        }

        var actingUserId = long.Parse(_userManager.GetUserId(User)!);
        var outcome = await _levelAuthorization.GrantAsync(LevelGrant.TeacherId!.Value, LevelGrant.LevelId!.Value,
            actingUserId, HttpContext.RequestAborted);
        ErrorMessage = outcome switch
        {
            TeacherLevelGrantOutcome.Granted => null,
            TeacherLevelGrantOutcome.AlreadyGranted => _localizer["This teacher is already authorized for this level."].Value,
            TeacherLevelGrantOutcome.TeacherNotFound => _localizer["Teacher not found."].Value,
            TeacherLevelGrantOutcome.LevelNotFound => _localizer["Level not found."].Value,
            _ => _localizer["Could not grant this level."].Value,
        };
        if (outcome == TeacherLevelGrantOutcome.Granted)
        {
            StatusMessage = _localizer["Level granted."].Value;
        }

        await LoadAsync();
        return Page();
    }

    /// <summary>Revocation deliberately does not touch sessions the teacher
    /// already published for this level — see
    /// ITeacherLevelAuthorizationService's own remarks on why.</summary>
    public async Task<IActionResult> OnPostRevokeLevelAsync(long teacherId, int levelId)
    {
        var actingUserId = long.Parse(_userManager.GetUserId(User)!);
        var outcome = await _levelAuthorization.RevokeAsync(teacherId, levelId, actingUserId, HttpContext.RequestAborted);
        StatusMessage = outcome == TeacherLevelRevokeOutcome.Revoked ? _localizer["Level revoked."].Value : null;
        ErrorMessage = outcome switch
        {
            TeacherLevelRevokeOutcome.Revoked => null,
            TeacherLevelRevokeOutcome.NotGranted => _localizer["This teacher was not authorized for this level."].Value,
            TeacherLevelRevokeOutcome.TeacherNotFound => _localizer["Teacher not found."].Value,
            _ => _localizer["Could not revoke this level."].Value,
        };

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostDeactivateAsync(long teacherId)
    {
        await _teachers.DeactivateAsync(teacherId, HttpContext.RequestAborted);
        StatusMessage = _localizer["Teacher deactivated."].Value;
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostReactivateAsync(long teacherId)
    {
        await _teachers.ReactivateAsync(teacherId, HttpContext.RequestAborted);
        StatusMessage = _localizer["Teacher reactivated."].Value;
        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        Teachers = await _db.Teachers.OrderBy(t => t.FullName).ToListAsync();

        // Home market first (countries are seeded in that order), so the pay
        // rate defaults to the currency this centre pays in without a code
        // being written into the page.
        Currencies = (await _db.Countries.Where(c => c.IsActive)
                .OrderBy(c => c.Id)
                .Select(c => c.CurrencyCode)
                .ToListAsync())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var readyTeacherIds = (await _db.TeacherMeetingConnections
            .Where(c => c.Status == MVTeaches.Domain.Integrations.ProviderConnectionStatus.Connected)
            .Select(c => c.TeacherId)
            .Distinct()
            .ToListAsync()).ToHashSet();
        TeachersNotReadyForOnlineSessions = Teachers.Select(t => t.Id).Where(id => !readyTeacherIds.Contains(id)).ToHashSet();
        TimeZoneIds = DateTimeZoneProviders.Tzdb.Ids.OrderBy(id => id, StringComparer.Ordinal).ToList();
        Courses = await _db.Courses.Where(c => c.IsActive).OrderBy(c => c.NameEn).ToListAsync();
        Levels = await _db.Levels.Where(l => l.IsActive).OrderBy(l => l.SortOrder).ToListAsync();
        AgeGroups = await _db.AgeGroups.OrderBy(a => a.MinAge).ToListAsync();

        var teacherNames = Teachers.ToDictionary(t => t.Id, t => t.FullName);
        var courseNames = Courses.ToDictionary(c => c.Id, c => c.NameEn);
        var levelCodes = Levels.ToDictionary(l => l.Id, l => l.Code);
        var ageGroupCodes = AgeGroups.ToDictionary(a => a.Id, a => a.Code);

        var rates = await _db.TeacherRates.OrderByDescending(r => r.Id).ToListAsync();
        Rates = rates.Select(r => new RateRow(r.Id, teacherNames.GetValueOrDefault(r.TeacherId, string.Empty),
            r.CourseId.HasValue ? courseNames.GetValueOrDefault(r.CourseId.Value) : null,
            r.LevelId.HasValue ? levelCodes.GetValueOrDefault(r.LevelId.Value) : null,
            r.AgeGroupId.HasValue ? ageGroupCodes.GetValueOrDefault(r.AgeGroupId.Value) : null,
            r.Rate.Amount, r.Rate.Currency, r.Unit, r.EffectiveFrom, r.EffectiveTo)).ToList();

        // Deliberately looks up against EVERY level, not just Levels (active
        // ones only, used for the "grant" dropdown) — a level assigned in the
        // past must still display even if it was later deactivated.
        var levelById = await _db.Levels.ToDictionaryAsync(l => l.Id);
        var assignments = await _db.TeacherLevelAssignments.ToListAsync();
        LevelsByTeacher = assignments
            .GroupBy(a => a.TeacherId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Level>)g
                .Select(a => levelById.GetValueOrDefault(a.LevelId))
                .Where(l => l is not null)
                .Select(l => l!)
                .OrderBy(l => l.SortOrder)
                .ToList());
    }
}
