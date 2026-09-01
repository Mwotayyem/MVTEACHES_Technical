using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Files;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;

namespace MVTeaches.Web.Pages.Files;

/// <summary>
/// Serves one offer poster's image. Written alongside /Files/Receipt rather
/// than reusing it, because the two answer completely different questions: a
/// receipt is a family's private document and is reachable only by that
/// family or an admin, while a poster is centre advertising meant to be seen.
///
/// So the rule here is only "a hidden poster is not public": any signed-in
/// account may fetch the image of a poster the centre has marked visible,
/// and a hidden one is reachable by Admin/SystemAdmin alone. A poster that
/// does not exist and one the caller may not see return the same 404 — the
/// same convention the receipt endpoint uses.
///
/// The file id is never accepted from the caller: it is read from the poster
/// row, so this endpoint can never be pointed at somebody's receipt.
/// </summary>
[Authorize]
public class PosterImageModel : PageModel
{
    private readonly MvTeachesDbContext _db;
    private readonly IFileStorageService _files;

    public PosterImageModel(MvTeachesDbContext db, IFileStorageService files)
    {
        _db = db;
        _files = files;
    }

    public async Task<IActionResult> OnGetAsync(long posterId)
    {
        var poster = await _db.PromotionalPosters.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == posterId, HttpContext.RequestAborted);
        if (poster?.ImageFileId is null)
        {
            return NotFound();
        }

        var isAdmin = User.IsInRole(RoleNames.Admin) || User.IsInRole(RoleNames.SystemAdmin);
        if (!poster.IsActive && !isAdmin)
        {
            return NotFound();
        }

        var document = await _files.OpenAsync(poster.ImageFileId.Value, HttpContext.RequestAborted);
        if (document is null)
        {
            return NotFound();
        }

        return File(document.Content, document.ContentType);
    }
}
