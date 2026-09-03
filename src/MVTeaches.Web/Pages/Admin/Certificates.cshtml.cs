using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using MVTeaches.Application.Certificates;
using MVTeaches.Domain.Certificates;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using MVTeaches.Web.Identity;
using MVTeaches.Web.Resources;

namespace MVTeaches.Web.Pages.Admin;

/// <summary>
/// §27.1/§27.2 (D-30/D-51/CONF-03/Q-27) — progress is always live (never
/// snapshotted per student), and issuance is always this explicit admin
/// action; crossing the hour threshold alone never issues a certificate.
/// </summary>
[Authorize(Roles = RoleNames.Admin + "," + RoleNames.SystemAdmin)]
[Authorize(Policy = PermissionKeys.CertificatesView)]
public class CertificatesModel : PageModel
{
    private readonly MvTeachesDbContext _db;
    private readonly ICertificateService _certificates;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IAuthorizationService _authorizationService;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public CertificatesModel(MvTeachesDbContext db, ICertificateService certificates, UserManager<ApplicationUser> userManager,
        IAuthorizationService authorizationService, IStringLocalizer<SharedResource> localizer)
    {
        _db = db;
        _certificates = certificates;
        _userManager = userManager;
        _authorizationService = authorizationService;
        _localizer = localizer;
    }

    public record ProgressRow(long StudentId, string StudentName, int LevelId, string LevelCode, long CourseId,
        string CourseName, int MinutesCompleted, int RequiredMinutes, bool IsEligible, bool AlreadyIssued);

    public record CertificateRow(long Id, string StudentName, string LevelCode, string CertificateNumber, CertificateStatus Status);

    public IReadOnlyList<ProgressRow> Progress { get; set; } = Array.Empty<ProgressRow>();
    public IReadOnlyList<CertificateRow> Certificates { get; set; } = Array.Empty<CertificateRow>();

    public string? StatusMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostIssueAsync(long studentId, int levelId, long courseId)
    {
        if (await this.RequirePermissionAsync(_authorizationService, PermissionKeys.CertificatesManage) is { } deny)
        {
            return deny;
        }

        var issuedByUserId = long.Parse(_userManager.GetUserId(User)!);
        var result = await _certificates.IssueAsync(studentId, levelId, courseId, issuedByUserId, HttpContext.RequestAborted);

        StatusMessage = result.Outcome == IssueCertificateOutcome.Issued
            ? _localizer["Certificate {0} issued.", result.CertificateNumber!].Value
            : null;
        ErrorMessage = result.Outcome switch
        {
            IssueCertificateOutcome.AlreadyIssued => _localizer["A certificate for this student/level/course already exists."].Value,
            IssueCertificateOutcome.NotEligible => _localizer["This student has not reached the required hours yet."].Value,
            _ => null,
        };

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostRevokeAsync(long certificateId)
    {
        if (await this.RequirePermissionAsync(_authorizationService, PermissionKeys.CertificatesManage) is { } deny)
        {
            return deny;
        }

        await _certificates.RevokeAsync(certificateId, HttpContext.RequestAborted);
        StatusMessage = _localizer["Certificate revoked."].Value;
        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        var students = await _db.Students.ToDictionaryAsync(s => s.Id, s => s.FullName);
        var levels = await _db.Levels.ToDictionaryAsync(l => l.Id, l => l.Code);
        var courses = await _db.Courses.ToDictionaryAsync(c => c.Id, c => c.NameEn);

        var issuedByKey = await _db.Certificates
            .Where(c => c.Status != CertificateStatus.Revoked)
            .ToDictionaryAsync(c => (c.StudentId, c.LevelId, c.CourseId), c => true);

        var progressRows = await _db.LevelProgresses
            .Where(p => p.MinutesCompleted > 0)
            .OrderByDescending(p => p.MinutesCompleted)
            .Take(200)
            .ToListAsync();

        var rows = new List<ProgressRow>();
        foreach (var p in progressRows)
        {
            var eligibility = await _certificates.GetEligibilityAsync(p.StudentId, p.LevelId, p.CourseId, HttpContext.RequestAborted);
            rows.Add(new ProgressRow(p.StudentId, students.GetValueOrDefault(p.StudentId, $"#{p.StudentId}"),
                p.LevelId, levels.GetValueOrDefault(p.LevelId, $"#{p.LevelId}"), p.CourseId,
                courses.GetValueOrDefault(p.CourseId, $"#{p.CourseId}"), eligibility.MinutesCompleted,
                eligibility.RequiredMinutes, eligibility.IsEligible,
                issuedByKey.ContainsKey((p.StudentId, p.LevelId, p.CourseId))));
        }

        Progress = rows;

        var certificates = await _db.Certificates.OrderByDescending(c => c.Id).Take(100).ToListAsync();
        Certificates = certificates.Select(c => new CertificateRow(c.Id, students.GetValueOrDefault(c.StudentId, $"#{c.StudentId}"),
            levels.GetValueOrDefault(c.LevelId, $"#{c.LevelId}"), c.CertificateNumber, c.Status)).ToList();
    }
}
