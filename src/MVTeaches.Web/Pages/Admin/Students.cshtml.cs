using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using MVTeaches.Application.People;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.People;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using MVTeaches.Web.Resources;
using NodaTime;

namespace MVTeaches.Web.Pages.Admin;

/// <summary>
/// §14 — the first slice of the Student/Guardian register. Real self-registration
/// is phone+OTP via WhatsApp (§7), which is genuinely blocked (see
/// docs/deployment/STATUS.md) — this page is the honest interim: an admin enters
/// what staff already collect over the phone, driving the exact same Student/
/// Guardian/Guardianship/StudentLevel domain state machines. Deliberately no
/// edit/delete yet — only the forward moves the state machines themselves allow.
///
/// UI pass: every id the admin used to type by hand is now a named picker, and
/// the required ids/dates are nullable so [Required] actually fires. Before
/// this, an untouched &lt;select&gt; posted an empty string, ModelState.Clear()
/// wiped the binding error, [Required] passed on a non-nullable long that had
/// defaulted to 0, and the service was called with student id 0 (or a date of
/// birth of 0001-01-01). Same fix and same reasoning as the one already
/// documented in Teacher/PublishSlots.cshtml.cs.
/// </summary>
[Authorize(Roles = RoleNames.Admin + "," + RoleNames.SystemAdmin)]
public class StudentsModel : PageModel
{
    private readonly MvTeachesDbContext _db;
    private readonly IStudentAdmissionService _admissions;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public StudentsModel(MvTeachesDbContext db, IStudentAdmissionService admissions, UserManager<ApplicationUser> userManager,
        IStringLocalizer<SharedResource> localizer)
    {
        _db = db;
        _admissions = admissions;
        _userManager = userManager;
        _localizer = localizer;
    }

    public record StudentRow(long Id, string FullName, string CountryCode, StudentStatus Status,
        string? CurrentLevelCode, IReadOnlyList<string> GuardianNames);

    public IReadOnlyList<StudentRow> Students { get; set; } = Array.Empty<StudentRow>();
    // Fully qualified to avoid ambiguity with the sibling MVTeaches.Web.Pages.Guardian namespace.
    public IReadOnlyList<MVTeaches.Domain.People.Guardian> Guardians { get; set; } = Array.Empty<MVTeaches.Domain.People.Guardian>();
    public IReadOnlyList<Country> Countries { get; set; } = Array.Empty<Country>();
    public IReadOnlyList<Level> Levels { get; set; } = Array.Empty<Level>();

    [BindProperty]
    public RegisterGuardianInput NewGuardian { get; set; } = new();

    [BindProperty]
    public RegisterStudentInput NewStudent { get; set; } = new();

    [BindProperty]
    public LinkGuardianInput Link { get; set; } = new();

    [BindProperty]
    public AssignLevelInput LevelAssignment { get; set; } = new();

    public string? StatusMessage { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>A picker label: the student's own name plus what an admin needs
    /// to tell two similar names apart — never the internal row id.</summary>
    public string PickerLabel(StudentRow student)
    {
        var level = student.CurrentLevelCode ?? _localizer["No level"].Value;
        var status = _localizer["StudentStatus." + student.Status].Value;
        return $"{student.FullName} — {level} — {status}";
    }

    public class RegisterGuardianInput
    {
        [Required, EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required, MinLength(8)]
        public string Password { get; set; } = string.Empty;

        [Required]
        public string FullName { get; set; } = string.Empty;
    }

    public class RegisterStudentInput
    {
        [Required]
        public int? CountryId { get; set; }

        [Required]
        public string FullName { get; set; } = string.Empty;

        [Required]
        public DateOnly? DateOfBirth { get; set; }

        [EmailAddress]
        public string? LoginEmail { get; set; }

        [MinLength(8)]
        public string? LoginPassword { get; set; }
    }

    public class LinkGuardianInput
    {
        [Required]
        public long? GuardianId { get; set; }

        [Required]
        public long? StudentId { get; set; }

        [Required]
        public GuardianRelationship Relationship { get; set; }

        public bool IsPrimary { get; set; }
    }

    public class AssignLevelInput
    {
        [Required]
        public long? StudentId { get; set; }

        [Required]
        public int? LevelId { get; set; }

        [Required]
        public string Reason { get; set; } = string.Empty;
    }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostRegisterGuardianAsync()
    {
        // Every [BindProperty] group on this page is bound and auto-validated on
        // every POST regardless of which named handler is invoked (a well-known
        // Razor Pages multi-form gotcha) — clearing the whole slate before
        // re-validating just the relevant group is the only reliable way to
        // avoid failing on some OTHER form's empty, irrelevant required fields.
        ModelState.Clear();
        if (!TryValidateModel(NewGuardian, nameof(NewGuardian)))
        {
            await LoadAsync();
            return Page();
        }

        var result = await _admissions.RegisterGuardianAsync(NewGuardian.Email, NewGuardian.Password, NewGuardian.FullName, HttpContext.RequestAborted);
        if (result.Outcome == RegisterGuardianOutcome.LoginFailed)
        {
            ErrorMessage = _localizer["Could not create the guardian's login: {0}", string.Join("; ", result.Errors ?? Array.Empty<string>())].Value;
        }
        else
        {
            StatusMessage = _localizer["Guardian '{0}' registered.", NewGuardian.FullName].Value;
        }

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostRegisterStudentAsync()
    {
        ModelState.Clear();
        if (!TryValidateModel(NewStudent, nameof(NewStudent)))
        {
            await LoadAsync();
            return Page();
        }

        var dateOfBirth = NewStudent.DateOfBirth!.Value;
        var dob = new LocalDate(dateOfBirth.Year, dateOfBirth.Month, dateOfBirth.Day);
        var result = await _admissions.RegisterStudentAsync(NewStudent.CountryId!.Value, NewStudent.FullName, dob,
            NewStudent.LoginEmail, NewStudent.LoginPassword, HttpContext.RequestAborted);

        if (result.Outcome == RegisterStudentOutcome.LoginFailed)
        {
            ErrorMessage = _localizer["Could not create the student's login: {0}", string.Join("; ", result.Errors ?? Array.Empty<string>())].Value;
        }
        else
        {
            StatusMessage = _localizer["Student '{0}' registered (pending verification).", NewStudent.FullName].Value;
        }

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostLinkGuardianAsync()
    {
        ModelState.Clear();
        if (!TryValidateModel(Link, nameof(Link)))
        {
            await LoadAsync();
            return Page();
        }

        var actingUserId = GetCurrentUserId();
        var result = await _admissions.LinkGuardianAsync(Link.GuardianId!.Value, Link.StudentId!.Value, Link.Relationship,
            Link.IsPrimary, actingUserId, HttpContext.RequestAborted);

        ErrorMessage = result.Outcome switch
        {
            LinkGuardianOutcome.PrimaryConflict => _localizer["This student already has a primary guardian — un-primary the existing one first."].Value,
            LinkGuardianOutcome.AlreadyLinked => _localizer["This guardian is already linked to this student."].Value,
            _ => null,
        };
        if (result.Outcome == LinkGuardianOutcome.Linked)
        {
            StatusMessage = _localizer["Guardian linked."].Value;
        }

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostVerifyAsync(long studentId)
    {
        await _admissions.VerifyStudentAsync(studentId, HttpContext.RequestAborted);
        StatusMessage = _localizer["Student marked verified."].Value;
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAssignLevelAsync()
    {
        ModelState.Clear();
        if (!TryValidateModel(LevelAssignment, nameof(LevelAssignment)))
        {
            await LoadAsync();
            return Page();
        }

        var actingUserId = GetCurrentUserId();
        await _admissions.AssignLevelAsync(LevelAssignment.StudentId!.Value, LevelAssignment.LevelId!.Value, actingUserId,
            LevelAssignment.Reason, HttpContext.RequestAborted);
        StatusMessage = _localizer["Level assigned."].Value;
        await LoadAsync();
        return Page();
    }

    private long GetCurrentUserId() => long.Parse(_userManager.GetUserId(User)!);

    private async Task LoadAsync()
    {
        Countries = await _db.Countries.Where(c => c.IsActive).OrderBy(c => c.NameEn).ToListAsync();
        Levels = await _db.Levels.Where(l => l.IsActive).OrderBy(l => l.SortOrder).ToListAsync();
        Guardians = await _db.Guardians.OrderBy(g => g.FullName).ToListAsync();

        var students = await _db.Students
            .OrderByDescending(s => s.Id)
            .Take(200)
            .ToListAsync();

        var countryByI = Countries.ToDictionary(c => c.Id, c => c.Code);
        var levelByI = Levels.ToDictionary(l => l.Id, l => l.Code);

        var currentLevels = await _db.StudentLevels.Where(l => l.IsCurrent).ToListAsync();
        var currentLevelByStudent = currentLevels.ToDictionary(l => l.StudentId, l => l.LevelId);

        var guardianships = await _db.Guardianships.ToListAsync();
        var guardianNamesByGuardianId = Guardians.ToDictionary(g => g.Id, g => g.FullName);
        var guardianNamesByStudent = guardianships
            .GroupBy(g => g.StudentId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => guardianNamesByGuardianId.GetValueOrDefault(x.GuardianId, "?")).ToList());

        Students = students.Select(s => new StudentRow(
            s.Id,
            s.FullName,
            countryByI.GetValueOrDefault(s.CountryId, "?"),
            s.Status,
            currentLevelByStudent.TryGetValue(s.Id, out var levelId) ? levelByI.GetValueOrDefault(levelId) : null,
            guardianNamesByStudent.GetValueOrDefault(s.Id, Array.Empty<string>()))).ToList();
    }
}
