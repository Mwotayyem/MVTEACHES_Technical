using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MVTeaches.Web.Pages.Account;

/// <summary>
/// Security review 2026-09-02 (Review Required — Authorization): the target
/// of ASP.NET Core Identity's cookie AccessDeniedPath, configured in
/// Program.cs right alongside the new Stage 1 permission policies. Before
/// this page existed, calling Forbid() (as every permission-guarded page and
/// handler now does) redirected here anyway — to a path that rendered a
/// plain 404, since nothing pointed AccessDeniedPath at a real page and none
/// existed. [Authorize] with no role restriction: anyone who reaches this
/// page is, by definition, already signed in — they were just refused a
/// SPECIFIC screen or action, not authentication itself.
/// </summary>
[Authorize]
public class AccessDeniedModel : PageModel
{
    public void OnGet()
    {
    }
}
