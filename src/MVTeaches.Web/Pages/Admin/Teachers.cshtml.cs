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
using MVTeaches.Web.Identity;
using MVTeaches.Web.Display;
using MVTeaches.Web.Resources;
using NodaTime;

namespace MVTeaches.Web.Pages.Admin;

/// <summary>
/// §9.1/D-28 — before this page there was NO way to create a Teacher account
/// anywhere in the application, which meant the whole payroll surface
/// (declare/verify/pay) had nothing real to operate on. Mirrors the
/// guardian-registration pattern on /Admin/Students exactly.
///
/// Security review 2026-09-03 (Review Required — Authorization), Stage 2B:
/// [Authorize(Policy=TeachersView)] gates the GET; every mutating handler
/// below (Register/CreateRate/GrantLevel/RevokeLevel/Deactivate/Reactivate)
/// requires TeachersManage explicitly, the same RequirePermissionAsync
/// pattern used since Stage 1.
/// </summary>
[Authorize(Roles = RoleNames.Admin + "," + RoleNames.SystemAdmin)]
[Authorize(Policy = PermissionKeys.TeachersView)]
public class TeachersModel : PageModel
{
    private readonly MvTeachesDbContext _db;
    private readonly ITeacherAdmissionService _teachers;
    private readonly ITeacherRateService _rates;
    private readonly ITeacherLevelAuthorizationService _levelAuthorization;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly IAuthorizationService _authorizationService;

    /// <summary>Owner decision 2026-09-04: a new pay rate starts today, and the
    /// screen no longer asks. Taken from IClock rather than DateTime.Now so it
    /// is the same time source the rest of the application (and its tests) use.</summary>
    private readonly IClock _clock;

    public TeachersModel(MvTeachesDbContext db, ITeacherAdmissionService teachers, ITeacherRateService rates,
        ITeacherLevelAuthorizationService levelAuthorization, UserManager<ApplicationUser> userManager,
        IStringLocalizer<SharedResource> localizer, IAuthorizationService authorizationService, IClock clock)
    {
        _db = db;
        _teachers = teachers;
        _rates = rates;
        _levelAuthorization = levelAuthorization;
        _userManager = userManager;
        _localizer = localizer;
        _authorizationService = authorizationService;
        _clock = clock;
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
    /// <summary>Owner decision 2026-09-04: a grant is a (course, level) pair,
    /// so the chip has to name both. Showing the level alone would say "B2"
    /// beside a teacher authorised only for B2 English, which is exactly the
    /// ambiguity the course column removes.</summary>
    public record TeacherGrant(long CourseId, string CourseName, int LevelId, string LevelCode);

    public IReadOnlyDictionary<long, IReadOnlyList<TeacherGrant>> GrantsByTeacher { get; set; } =
        new Dictionary<long, IReadOnlyList<TeacherGrant>>();

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
    /// <summary>Owner decision 2026-08-30 rule 6 asks for allowed levels to be
    /// easy to add. Ticking several at once is exactly the same operation
    /// repeated — ITeacherLevelAuthorizationService.GrantAsync is still called
    /// once per level and is still the only thing that decides whether each one
    /// is allowed (including refusing a duplicate). No batching rule of any
    /// kind lives in this page.</summary>
    public class LevelGrantInput
    {
        [Required(ErrorMessage = "Choose a teacher.")] public long? TeacherId { get; set; }

        /// <summary>Owner decision 2026-09-04: a grant says WHICH COURSES these
        /// levels are in. Never defaulted — "authorised for B2" with no course
        /// silently authorised B2 in every subject the centre teaches, for a
        /// teacher hired for one of them.
        /// <para>Revised the same day: a teacher who teaches three subjects at
        /// the same levels was being asked to fill this form in three times.
        /// Courses and levels are both ticked now, and every pair of the two is
        /// granted — which is exactly what the table stores anyway. Emptiness
        /// is checked in the handler rather than by [Required], so the message
        /// can name which box was left empty.</para></summary>
        public List<long> CourseIds { get; set; } = new();

        public List<int> LevelIds { get; set; } = new();
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

        // Nullable for the same reason every picked id on this page is: a
        // non-nullable decimal binds an empty box to 0, Range(0, ...) accepts
        // it, and the admin gets a pay rate of zero with no complaint at all.
        [Required(ErrorMessage = "Enter the rate amount."), Range(0, double.MaxValue, ErrorMessage = "Enter the rate amount.")]
        public decimal? Amount { get; set; }
        [Required(ErrorMessage = "Choose a currency."), StringLength(3, MinimumLength = 3)] public string Currency { get; set; } = string.Empty;
        [Required] public RateUnit Unit { get; set; }
        /// <summary>Owner decision 2026-09-04: the screen no longer asks for
        /// this. The column stays — PayrollRateResolver selects on it, and a
        /// rate with no start date could never be resolved at all — but a new
        /// rate always starts today, so there is nothing for an admin to key
        /// in and get wrong. Kept on the DTO rather than removed so a future
        /// "backdate this rate" screen has somewhere to put it; unset means
        /// today. See OnPostCreateRateAsync.</summary>
        public DateOnly? EffectiveFrom { get; set; }
    }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostRegisterAsync()
    {
        if (await this.RequirePermissionAsync(_authorizationService, PermissionKeys.TeachersManage) is { } deny)
        {
            return deny;
        }

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
        if (await this.RequirePermissionAsync(_authorizationService, PermissionKeys.TeachersManage) is { } deny)
        {
            return deny;
        }

        ModelState.Clear();
        if (!TryValidateModel(NewRate, nameof(NewRate)))
        {
            await LoadAsync();
            return Page();
        }

        // Today unless something explicitly supplied a date. The form does
        // not, so in practice this is always today: "what this teacher is paid
        // from now on", which is the only question the owner wanted asked.
        var effectiveFrom = NewRate.EffectiveFrom is { } from
            ? new LocalDate(from.Year, from.Month, from.Day)
            : _clock.GetCurrentInstant().InUtc().Date;
        var actingUserId = long.Parse(_userManager.GetUserId(User)!);

        try
        {
            // Owner decision 2026-09-04: exactly one open rate per
            // (course, level, age group). The service closes the previous one
            // itself; the two refusals below are the cases where closing is
            // not representable, and in both of them nothing was written.
            var result = await _rates.CreateRateAsync(NewRate.TeacherId!.Value, NewRate.CourseId, NewRate.LevelId, NewRate.AgeGroupId,
                new Money(NewRate.Amount!.Value, NewRate.Currency), NewRate.Unit, effectiveFrom, actingUserId, HttpContext.RequestAborted);

            StatusMessage = result.Outcome == CreateTeacherRateOutcome.Created
                ? _localizer["Pay rate saved. Any earlier rate for the same course, level and age group was closed on that date, so exactly one rate is ever in force."].Value
                : null;
            ErrorMessage = result.Outcome switch
            {
                CreateTeacherRateOutcome.DuplicateStartDate => _localizer[
                    "This teacher already has a rate for the same course, level and age group starting on {0}. Nothing was saved — one rate cannot both start and be replaced on the same day. Use a later start date.",
                    _localizer.Date(result.ExistingEffectiveFrom!.Value)].Value,
                CreateTeacherRateOutcome.StartsBeforeExistingRate => _localizer[
                    "This teacher already has a rate for the same course, level and age group starting on {0}, which is after the date entered. Nothing was saved — an earlier rate would have to end before it began.",
                    _localizer.Date(result.ExistingEffectiveFrom!.Value)].Value,
                _ => null,
            };
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
        if (await this.RequirePermissionAsync(_authorizationService, PermissionKeys.TeachersManage) is { } deny)
        {
            return deny;
        }

        ModelState.Clear();
        if (!TryValidateModel(LevelGrant, nameof(LevelGrant)))
        {
            await LoadAsync();
            return Page();
        }

        var courseIds = LevelGrant.CourseIds.Distinct().ToList();
        if (courseIds.Count == 0)
        {
            ErrorMessage = _localizer["Tick at least one course this teacher may teach."].Value;
            await LoadAsync();
            return Page();
        }

        var levelIds = LevelGrant.LevelIds.Distinct().ToList();
        if (levelIds.Count == 0)
        {
            ErrorMessage = _localizer["Tick at least one level this teacher may teach."].Value;
            await LoadAsync();
            return Page();
        }

        var actingUserId = long.Parse(_userManager.GetUserId(User)!);
        var granted = 0;
        var alreadyGranted = 0;
        string? failure = null;

        // Every ticked course crossed with every ticked level. The service
        // still decides each one on its own — a pair already granted comes back
        // AlreadyGranted and is counted, never re-inserted.
        foreach (var (courseId, levelId) in courseIds.SelectMany(c => levelIds.Select(l => (c, l))))
        {
            var outcome = await _levelAuthorization.GrantAsync(LevelGrant.TeacherId!.Value,
                courseId, levelId, actingUserId, HttpContext.RequestAborted);
            switch (outcome)
            {
                case TeacherLevelGrantOutcome.CourseNotFound:
                    failure = _localizer["That course no longer exists."].Value;
                    break;
                case TeacherLevelGrantOutcome.Granted:
                    granted++;
                    break;
                case TeacherLevelGrantOutcome.AlreadyGranted:
                    alreadyGranted++;
                    break;
                case TeacherLevelGrantOutcome.TeacherNotFound:
                    failure ??= _localizer["Teacher not found."].Value;
                    break;
                case TeacherLevelGrantOutcome.LevelNotFound:
                    failure ??= _localizer["Level not found."].Value;
                    break;
                default:
                    failure ??= _localizer["Could not grant this level."].Value;
                    break;
            }
        }

        ErrorMessage = failure;
        if (granted > 0)
        {
            StatusMessage = _localizer["{0} level(s) added. This teacher can now publish sessions for them.", granted].Value;
        }
        else if (alreadyGranted > 0 && failure is null)
        {
            ErrorMessage = _localizer["This teacher already teaches every level you ticked."].Value;
        }

        await LoadAsync();
        return Page();
    }

    /// <summary>Revocation deliberately does not touch sessions the teacher
    /// already published for this level — see
    /// ITeacherLevelAuthorizationService's own remarks on why.</summary>
    public async Task<IActionResult> OnPostRevokeLevelAsync(long teacherId, long courseId, int levelId)
    {
        if (await this.RequirePermissionAsync(_authorizationService, PermissionKeys.TeachersManage) is { } deny)
        {
            return deny;
        }

        var actingUserId = long.Parse(_userManager.GetUserId(User)!);
        var outcome = await _levelAuthorization.RevokeAsync(teacherId, courseId, levelId, actingUserId,
            HttpContext.RequestAborted);
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
        if (await this.RequirePermissionAsync(_authorizationService, PermissionKeys.TeachersManage) is { } deny)
        {
            return deny;
        }

        await _teachers.DeactivateAsync(teacherId, HttpContext.RequestAborted);
        StatusMessage = _localizer["Teacher deactivated."].Value;
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostReactivateAsync(long teacherId)
    {
        if (await this.RequirePermissionAsync(_authorizationService, PermissionKeys.TeachersManage) is { } deny)
        {
            return deny;
        }

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
        // Every course, not just active ones, for the same reason as levels
        // below: a grant made against a course later retired must still be
        // visible, and revocable.
        var courseById = await _db.Courses.ToDictionaryAsync(c => c.Id, c => c.NameEn);
        GrantsByTeacher = assignments
            .GroupBy(a => a.TeacherId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<TeacherGrant>)g
                .Select(a => new
                {
                    a.CourseId,
                    CourseName = courseById.GetValueOrDefault(a.CourseId, "?"),
                    Level = levelById.GetValueOrDefault(a.LevelId),
                })
                .Where(x => x.Level is not null)
                .Select(x => new TeacherGrant(x.CourseId, x.CourseName, x.Level!.Id, x.Level.Code))
                .OrderBy(x => x.CourseName)
                .ThenBy(x => x.LevelCode)
                .ToList());
    }
}
