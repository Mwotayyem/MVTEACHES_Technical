using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MVTeaches.Infrastructure.Persistence;

namespace MVTeaches.Web.ViewComponents;

/// <summary>
/// Renders the signed-in person's real name in the top bar instead of their
/// login email. The name is not stored on the identity user, so it is resolved
/// from whichever domain profile that account owns (teacher, guardian, or
/// student) — a read of tables that are already loaded on most screens, and no
/// change to identity, claims, or the sign-in path.
///
/// When an account has no domain profile (an admin, typically) the email's
/// local part is shown rather than the full address: still identifying, but
/// not an email address sitting in the chrome of every page.
/// </summary>
public class CurrentUserNameViewComponent : ViewComponent
{
    private readonly MvTeachesDbContext _db;

    public CurrentUserNameViewComponent(MvTeachesDbContext db)
    {
        _db = db;
    }

    public record UserNameModel(string DisplayName, string Initial, string? RoleLabel);

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var identityName = UserClaimsPrincipal.Identity?.Name ?? string.Empty;
        var displayName = LocalPart(identityName);

        var userIdText = UserClaimsPrincipal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (long.TryParse(userIdText, out var userId))
        {
            var resolved =
                await _db.Teachers.Where(x => x.UserId == userId).Select(x => x.FullName).FirstOrDefaultAsync()
                ?? await _db.Guardians.Where(x => x.UserId == userId).Select(x => x.FullName).FirstOrDefaultAsync()
                ?? await _db.Students.Where(x => x.UserId == userId).Select(x => x.FullName).FirstOrDefaultAsync();

            if (!string.IsNullOrWhiteSpace(resolved))
            {
                displayName = resolved;
            }
        }

        var initial = string.IsNullOrWhiteSpace(displayName) ? "؟" : displayName.Trim()[..1].ToUpperInvariant();
        return View(new UserNameModel(displayName, initial, null));
    }

    private static string LocalPart(string identityName)
    {
        if (string.IsNullOrWhiteSpace(identityName))
        {
            return string.Empty;
        }

        var at = identityName.IndexOf('@');
        return at > 0 ? identityName[..at] : identityName;
    }
}
