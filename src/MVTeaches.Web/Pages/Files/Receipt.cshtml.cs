using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Files;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;

namespace MVTeaches.Web.Pages.Files;

/// <summary>
/// Owner decision 2026-08-30 (attachment protection, Section 5): the ONLY
/// way to read an uploaded receipt — never a public `wwwroot` link, never a
/// raw object key exposed to a browser. Access is limited to the payment's
/// own student, one of their active guardians, or Admin/SystemAdmin — the
/// same self-or-active-guardian shape every other payment/placement
/// operation already uses, kept here as its own private copy rather than a
/// shared helper (matching PaymentService's own documented convention).
/// A caller with no access, or a payment/receipt that doesn't exist, gets
/// the exact same 404 either way — existence is never leaked. Nothing about
/// this request is ever logged beyond what the framework's own default
/// request logging already does (no receipt content, no file path).
/// </summary>
[Authorize]
public class ReceiptModel : PageModel
{
    private readonly MvTeachesDbContext _db;
    private readonly IFileStorageService _files;
    private readonly UserManager<ApplicationUser> _userManager;

    public ReceiptModel(MvTeachesDbContext db, IFileStorageService files, UserManager<ApplicationUser> userManager)
    {
        _db = db;
        _files = files;
        _userManager = userManager;
    }

    public async Task<IActionResult> OnGetAsync(long paymentId)
    {
        var payment = await _db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.Id == paymentId, HttpContext.RequestAborted);
        if (payment?.ProofFileId is null)
        {
            return NotFound();
        }

        var isAdmin = User.IsInRole(RoleNames.Admin) || User.IsInRole(RoleNames.SystemAdmin);
        if (!isAdmin)
        {
            var actingUserId = long.Parse(_userManager.GetUserId(User)!);
            var isTheStudentThemself = await _db.Students.AnyAsync(
                s => s.Id == payment.StudentId && s.UserId == actingUserId, HttpContext.RequestAborted);
            var isAnActiveGuardian = !isTheStudentThemself && await _db.Guardianships
                .Join(_db.Guardians, gs => gs.GuardianId, g => g.Id, (gs, g) => new { gs.StudentId, g.UserId })
                .AnyAsync(x => x.StudentId == payment.StudentId && x.UserId == actingUserId, HttpContext.RequestAborted);

            if (!isTheStudentThemself && !isAnActiveGuardian)
            {
                return NotFound(); // never reveal that a payment/receipt exists to someone unauthorized
            }
        }

        var document = await _files.OpenAsync(payment.ProofFileId.Value, HttpContext.RequestAborted);
        if (document is null)
        {
            return NotFound();
        }

        return File(document.Content, document.ContentType, document.OriginalFileName);
    }
}
