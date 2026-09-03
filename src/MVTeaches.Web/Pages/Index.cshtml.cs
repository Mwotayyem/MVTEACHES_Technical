using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MVTeaches.Infrastructure.Identity;

namespace MVTeaches.Web.Pages;

public class IndexModel : PageModel
{
    private readonly IAuthorizationService _authorizationService;

    public IndexModel(IAuthorizationService authorizationService)
    {
        _authorizationService = authorizationService;
    }

    /// <summary>
    /// Owner-reported UX/routing fix (2026-09-03, follows Stage 2D): root `/`
    /// used to send every Admin/SystemAdmin account unconditionally to
    /// /Admin/Dashboard, regardless of whether it actually held
    /// Admin.Dashboard.View — an Admin granted only e.g. Payments.View +
    /// Payments.Confirm would land on Dashboard, get denied, and have no
    /// natural path to the one screen they can actually use. This is the
    /// fix: try each admin screen's own View key, in this fixed priority
    /// order, and land on the first one this account actually holds live
    /// (checked the same way every other permission check in the app is —
    /// AuthorizationService.AuthorizeAsync against the current policies, so a
    /// just-revoked/just-granted permission takes effect on this very next
    /// request with no logout/login, exactly like everywhere else).
    ///
    /// SystemAdmin needs no special case: PermissionAuthorizationHandler's
    /// own unconditional bypass (see its remarks) makes the very first check
    /// below (Dashboard.View) succeed for SystemAdmin regardless of claims,
    /// so it always lands on Dashboard through the same general mechanism —
    /// not a hardcoded shortcut, and never a reason to grant Dashboard.View
    /// to a plain Admin who was not meant to have it.
    /// </summary>
    private static readonly (string Key, string Page)[] AdminLandingPriority =
    {
        (PermissionKeys.DashboardView, "/Admin/Dashboard"),
        (PermissionKeys.PaymentsView, "/Admin/Payments"),
        (PermissionKeys.PayrollView, "/Admin/Payroll"),
        (PermissionKeys.SubscriptionsView, "/Admin/Subscriptions"),
        (PermissionKeys.StudentsView, "/Admin/Students"),
        (PermissionKeys.TeachersView, "/Admin/Teachers"),
        (PermissionKeys.SchedulesView, "/Admin/Schedules"),
        (PermissionKeys.CompensationView, "/Admin/CompensationRequests"),
        (PermissionKeys.PlacementTestsView, "/Admin/PlacementTests"),
        (PermissionKeys.CertificatesView, "/Admin/Certificates"),
        (PermissionKeys.FinancialReportView, "/Admin/FinancialReport"),
        (PermissionKeys.PostersView, "/Admin/Posters"),
    };

    public async Task<IActionResult> OnGetAsync()
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return Page();
        }

        // Deterministic priority for multi-role users: operational admin first,
        // then teaching, guardian, and finally direct student self-service.
        if (User.IsInRole(RoleNames.SystemAdmin) || User.IsInRole(RoleNames.Admin))
        {
            foreach (var (key, page) in AdminLandingPriority)
            {
                if ((await _authorizationService.AuthorizeAsync(User, key)).Succeeded)
                {
                    return RedirectToPage(page);
                }
            }

            // Neither SystemAdmin's bypass nor any granted View permission —
            // nothing under /Admin/* is reachable, so land explicitly where
            // every other denied admin request already lands.
            return RedirectToPage("/Account/AccessDenied");
        }

        if (User.IsInRole(RoleNames.Teacher))
        {
            return RedirectToPage("/Teacher/MySessions");
        }

        if (User.IsInRole(RoleNames.Guardian))
        {
            return RedirectToPage("/Guardian/MyChildren");
        }

        if (User.IsInRole(RoleNames.Student))
        {
            return RedirectToPage("/Student/MySessions");
        }

        return Page();
    }
}
