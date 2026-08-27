using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Ledger;
using MVTeaches.Application.Settings;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Settings;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;

namespace MVTeaches.Web.Pages.Admin;

/// <summary>
/// D-19/D-20/D-63/D-66 — the one case a session cancellation (/Admin/Schedules)
/// cannot resolve by itself: a student already pressed Join (D-83-final
/// consumption) and then the session failed for reasons outside their control.
/// The Technical Study (§17.4, line 1018) reserves this for the admin's own
/// case-by-case judgment, so it is a separate, explicit action here — never an
/// automatic side effect of cancelling a session.
///
/// See IMakeUpCreditService's remarks on CONF-04: a granted credit here is an
/// accurate audit record and appears on this page's queue, but is not yet
/// spendable through Join — that requires the owner's own resolution of an
/// explicitly unresolved business question in the Technical Study, not a
/// silent implementation choice.
/// </summary>
[Authorize(Roles = RoleNames.Admin + "," + RoleNames.SystemAdmin)]
public class MakeUpCreditsModel : PageModel
{
    private readonly MvTeachesDbContext _db;
    private readonly IMakeUpCreditService _makeUpCredits;
    private readonly ISettingsProvider _settings;
    private readonly UserManager<ApplicationUser> _userManager;

    public MakeUpCreditsModel(MvTeachesDbContext db, IMakeUpCreditService makeUpCredits, ISettingsProvider settings,
        UserManager<ApplicationUser> userManager)
    {
        _db = db;
        _makeUpCredits = makeUpCredits;
        _settings = settings;
        _userManager = userManager;
    }

    public IReadOnlyList<PendingMakeUpCredit> Queue { get; set; } = Array.Empty<PendingMakeUpCredit>();
    public IReadOnlyList<MVTeaches.Domain.People.Student> Students { get; set; } = Array.Empty<MVTeaches.Domain.People.Student>();
    public IReadOnlyList<Course> Courses { get; set; } = Array.Empty<Course>();
    public IReadOnlyList<Level> Levels { get; set; } = Array.Empty<Level>();
    public int DefaultExpiryDays { get; set; }

    [BindProperty]
    public GrantInput Grant { get; set; } = new();

    public string? StatusMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public class GrantInput
    {
        [Required] public long StudentId { get; set; }
        [Required] public long CourseId { get; set; }
        [Required] public int LevelId { get; set; }
        [Required, Range(1, 10_000)] public int Minutes { get; set; }
        [Required] public DateOnly ExpiresOn { get; set; }
    }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostGrantAsync()
    {
        ModelState.Clear();
        if (!TryValidateModel(Grant, nameof(Grant)))
        {
            await LoadAsync();
            return Page();
        }

        var actingUserId = long.Parse(_userManager.GetUserId(User)!);
        var expiresOn = new LocalDate(Grant.ExpiresOn.Year, Grant.ExpiresOn.Month, Grant.ExpiresOn.Day);
        await _makeUpCredits.GrantAsync(Grant.StudentId, Grant.CourseId, Grant.LevelId, Grant.Minutes, expiresOn,
            actingUserId, HttpContext.RequestAborted);
        StatusMessage = $"Makeup credit granted: {Grant.Minutes} minutes, expiring {expiresOn:yyyy-MM-dd}.";

        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        Students = await _db.Students.OrderByDescending(s => s.Id).Take(200).ToListAsync();
        Courses = await _db.Courses.Where(c => c.IsActive).OrderBy(c => c.NameEn).ToListAsync();
        Levels = await _db.Levels.Where(l => l.IsActive).OrderBy(l => l.SortOrder).ToListAsync();
        DefaultExpiryDays = await _settings.GetIntAsync(SettingKey.DefaultMakeUpExpiryDays, HttpContext.RequestAborted);
        Queue = await _makeUpCredits.GetPendingQueueAsync(HttpContext.RequestAborted);
    }
}
