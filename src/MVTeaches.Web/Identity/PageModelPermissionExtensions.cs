using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MVTeaches.Web.Identity;

/// <summary>
/// Razor Pages authorization only ever applies at the whole-page level — a
/// single [Authorize(Policy=...)] on a PageModel class protects every
/// handler on that page equally, but there is no built-in way to require a
/// DIFFERENT policy on one specific OnPostXxxAsync than the page's own GET.
/// That is exactly the shape Payments/Payroll/Subscriptions need: an Admin
/// with only the View permission must still reach OnGetAsync, but must be
/// refused by OnPostConfirmAsync specifically.
///
/// This one-line helper is the honest, explicit answer to that gap: call it
/// as the very first statement of any handler that writes data, before any
/// other code runs. A non-null result means "stop, this is the response" -
/// always `return` it immediately. Deliberately not a magic attribute: an
/// explicit call at the top of each guarded handler is something a reviewer
/// (or a `grep OnPostAsync` audit later) can actually see and verify,
/// which a hidden convention-based mechanism would not be.
/// </summary>
public static class PageModelPermissionExtensions
{
    public static async Task<IActionResult?> RequirePermissionAsync(this PageModel page,
        IAuthorizationService authorizationService, string permissionKey)
    {
        var result = await authorizationService.AuthorizeAsync(page.User, permissionKey);
        return result.Succeeded ? null : page.Forbid();
    }
}
