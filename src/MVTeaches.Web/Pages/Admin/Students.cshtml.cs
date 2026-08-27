using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.People;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.People;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;

namespace MVTeaches.Web.Pages.Admin;

/// <summary>
/// §14 — the first slice of the Student/Guardian register. Real self-registration
/// is phone+OTP via WhatsApp (§7), which is genuinely blocked (see
/// docs/deployment/STATUS.md) — this page is the honest interim: an admin enters
/// what staff already collect over the phone, driving the exact same Student/
/// Guardian/Guardianship/StudentLevel domain state machines. Deliberately no
/// edit/delete yet — only the forward moves the state machines themselves allow.
/// </summary>
[Authorize(Roles = RoleNames.Admin + "," + RoleNames.SystemAdmin)]
public class StudentsModel : PageModel
{
    private readonly MvTeachesDbContext _db;
    private readonly IStudentAdmissionService _admissions;
    private readonly UserManager<ApplicationUser> _userManager;

    public StudentsModel(MvTeachesDbContext db, IStudentAdmissionService admissions, UserManager<ApplicationUser> userManager)
    {
        _db = db;
        _admissions = admissions;
        _userManager = userManager;
    }

    public record StudentRow(long Id, string FullName, string CountryCode, StudentStatus Status,
        string? CurrentLevelCode, IReadOnlyList<string> GuardianNames);

    public IReadOnlyList<StudentRow> Students { get; set; } = Array.Empty<StudentRow>();
    public IReadOnlyList<Guardian> Guardians { get; set; } = Array.Empty<Guardian>();
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
        public int CountryId { get; set; }

        [Required]
        public string FullName { get; set; } = string.Empty;

        [Required]
        public DateOnly DateOfBirth { get; set; }

        [EmailAddress]
        public string? LoginEmail { get; set; }

        [MinLength(8)]
        public string? LoginPassword { get; set; }
    }

    public class LinkGuardianInput
    {
        [Required]
        public long GuardianId { get; set; }

        [Required]
        public long StudentId { get; set; }

        [Required]
        public GuardianRelationship Relationship { get; set; }

        public bool IsPrimary { get; set; }
    }

    public class AssignLevelInput
    {
        [Required]
        public long StudentId { get; set; }

        [Required]
        public int LevelId { get; set; }

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
            ErrorMessage = "Could not create the guardian's login: " + string.Join("; ", result.Errors ?? Array.Empty<string>());
        }
        else
        {
            StatusMessage = $"Guardian '{NewGuardian.FullName}' registered.";
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

        var dob = new LocalDate(NewStudent.DateOfBirth.Year, NewStudent.DateOfBirth.Month, NewStudent.DateOfBirth.Day);
        var result = await _admissions.RegisterStudentAsync(NewStudent.CountryId, NewStudent.FullName, dob,
            NewStudent.LoginEmail, NewStudent.LoginPassword, HttpContext.RequestAborted);

        if (result.Outcome == RegisterStudentOutcome.LoginFailed)
        {
            ErrorMessage = "Could not create the student's login: " + string.Join("; ", result.Errors ?? Array.Empty<string>());
        }
        else
        {
            StatusMessage = $"Student '{NewStudent.FullName}' registered (PendingVerification).";
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
        var result = await _admissions.LinkGuardianAsync(Link.GuardianId, Link.StudentId, Link.Relationship, Link.IsPrimary, actingUserId, HttpContext.RequestAborted);

        ErrorMessage = result.Outcome switch
        {
            LinkGuardianOutcome.PrimaryConflict => "This student already has a primary guardian — un-primary the existing one first.",
            LinkGuardianOutcome.AlreadyLinked => "This guardian is already linked to this student.",
            _ => null,
        };
        if (result.Outcome == LinkGuardianOutcome.Linked)
        {
            StatusMessage = "Guardian linked.";
        }

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostVerifyAsync(long studentId)
    {
        await _admissions.VerifyStudentAsync(studentId, HttpContext.RequestAborted);
        StatusMessage = "Student marked verified.";
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
        await _admissions.AssignLevelAsync(LevelAssignment.StudentId, LevelAssignment.LevelId, actingUserId, LevelAssignment.Reason, HttpContext.RequestAborted);
        StatusMessage = "Level assigned.";
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
