using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Payroll;
using MVTeaches.Application.People;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Common;
using MVTeaches.Domain.Payroll;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
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

    public TeachersModel(MvTeachesDbContext db, ITeacherAdmissionService teachers, ITeacherRateService rates,
        ITeacherLevelAuthorizationService levelAuthorization, UserManager<ApplicationUser> userManager)
    {
        _db = db;
        _teachers = teachers;
        _rates = rates;
        _levelAuthorization = levelAuthorization;
        _userManager = userManager;
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

    public class LevelGrantInput
    {
        [Required] public long TeacherId { get; set; }
        [Required] public int LevelId { get; set; }
    }

    public class RegisterTeacherInput
    {
        [Required, EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required, MinLength(8)]
        public string Password { get; set; } = string.Empty;

        [Required]
        public string FullName { get; set; } = string.Empty;

        [Required]
        public string TimeZoneId { get; set; } = string.Empty;
    }

    public class CreateRateInput
    {
        [Required] public long TeacherId { get; set; }

        /// <summary>§9.2's most-specific-wins rule: blank means "applies to
        /// every course/level/age-group" at that dimension.</summary>
        public long? CourseId { get; set; }
        public int? LevelId { get; set; }
        public int? AgeGroupId { get; set; }

        [Required, Range(0, double.MaxValue)] public decimal Amount { get; set; }
        [Required, StringLength(3, MinimumLength = 3)] public string Currency { get; set; } = string.Empty;
        [Required] public RateUnit Unit { get; set; }
        [Required] public DateOnly EffectiveFrom { get; set; }
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
            ErrorMessage = "Could not create the teacher's login: " + string.Join("; ", result.Errors ?? Array.Empty<string>());
        }
        else
        {
            StatusMessage = $"Teacher '{NewTeacher.FullName}' registered.";
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

        var effectiveFrom = new LocalDate(NewRate.EffectiveFrom.Year, NewRate.EffectiveFrom.Month, NewRate.EffectiveFrom.Day);
        var actingUserId = long.Parse(_userManager.GetUserId(User)!);

        try
        {
            await _rates.CreateRateAsync(NewRate.TeacherId, NewRate.CourseId, NewRate.LevelId, NewRate.AgeGroupId,
                new Money(NewRate.Amount, NewRate.Currency), NewRate.Unit, effectiveFrom, actingUserId, HttpContext.RequestAborted);
            StatusMessage = "Rate created.";
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
        var outcome = await _levelAuthorization.GrantAsync(LevelGrant.TeacherId, LevelGrant.LevelId, actingUserId, HttpContext.RequestAborted);
        ErrorMessage = outcome switch
        {
            TeacherLevelGrantOutcome.Granted => null,
            TeacherLevelGrantOutcome.AlreadyGranted => "This teacher is already authorized for this level.",
            TeacherLevelGrantOutcome.TeacherNotFound => "Teacher not found.",
            TeacherLevelGrantOutcome.LevelNotFound => "Level not found.",
            _ => "Could not grant this level.",
        };
        if (outcome == TeacherLevelGrantOutcome.Granted)
        {
            StatusMessage = "Level granted.";
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
        StatusMessage = outcome == TeacherLevelRevokeOutcome.Revoked ? "Level revoked." : null;
        ErrorMessage = outcome switch
        {
            TeacherLevelRevokeOutcome.Revoked => null,
            TeacherLevelRevokeOutcome.NotGranted => "This teacher was not authorized for this level.",
            TeacherLevelRevokeOutcome.TeacherNotFound => "Teacher not found.",
            _ => "Could not revoke this level.",
        };

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostDeactivateAsync(long teacherId)
    {
        await _teachers.DeactivateAsync(teacherId, HttpContext.RequestAborted);
        StatusMessage = "Teacher deactivated.";
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostReactivateAsync(long teacherId)
    {
        await _teachers.ReactivateAsync(teacherId, HttpContext.RequestAborted);
        StatusMessage = "Teacher reactivated.";
        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        Teachers = await _db.Teachers.OrderByDescending(t => t.Id).ToListAsync();

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
        Rates = rates.Select(r => new RateRow(r.Id, teacherNames.GetValueOrDefault(r.TeacherId, $"#{r.TeacherId}"),
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
