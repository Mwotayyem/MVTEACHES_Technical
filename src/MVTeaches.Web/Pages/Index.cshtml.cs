using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MVTeaches.Infrastructure.Identity;

namespace MVTeaches.Web.Pages;

public class IndexModel : PageModel
{
    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return Page();
        }

        // Deterministic priority for multi-role users: operational admin first,
        // then teaching, guardian, and finally direct student self-service.
        if (User.IsInRole(RoleNames.SystemAdmin) || User.IsInRole(RoleNames.Admin))
        {
            return RedirectToPage("/Admin/Dashboard");
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
