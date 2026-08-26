using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MVTeaches.Infrastructure.Identity;

namespace MVTeaches.Web.Pages.Account;

/// <summary>
/// Email + password sign-in via ASP.NET Core Identity's own SignInManager —
/// the standard, well-documented pattern, used here deliberately for
/// Admin/SystemAdmin/Teacher accounts. Guardian/Student sign-in is documented
/// elsewhere as phone + OTP (§7), which depends on the WhatsApp integration
/// this codebase does not yet have real credentials for (see
/// NotConfiguredWhatsAppSender) — that flow is NOT built here; this page only
/// covers the credential-based accounts that don't depend on it.
/// </summary>
public class LoginModel : PageModel
{
    private readonly SignInManager<ApplicationUser> _signInManager;

    public LoginModel(SignInManager<ApplicationUser> signInManager)
    {
        _signInManager = signInManager;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string? ErrorMessage { get; set; }

    public string ReturnUrl { get; set; } = "/";

    public class InputModel
    {
        [Required, EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required, DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        public bool RememberMe { get; set; }
    }

    public void OnGet(string? returnUrl = null)
    {
        ReturnUrl = returnUrl ?? Url.Content("~/");
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        returnUrl ??= Url.Content("~/");
        ReturnUrl = returnUrl;

        if (!ModelState.IsValid)
        {
            return Page();
        }

        // §22 security review: one generic failure message for both "no such
        // user" and "wrong password" — SignInManager's own result already
        // avoids leaking which one it was.
        var result = await _signInManager.PasswordSignInAsync(Input.Email, Input.Password, Input.RememberMe, lockoutOnFailure: true);

        if (result.Succeeded)
        {
            return LocalRedirect(returnUrl);
        }

        if (result.RequiresTwoFactor)
        {
            // §22's "MFA إلزامي لـ SystemAdmin وReadOnlyAdmin" (TOTP) is a real,
            // documented requirement — but the enrollment/challenge UI for it
            // has not been built yet. Flagged honestly rather than silently
            // routed around; see docs/deployment/STATUS.md.
            ErrorMessage = "This account requires two-factor verification, which has not been built yet.";
            return Page();
        }

        if (result.IsLockedOut)
        {
            ErrorMessage = "This account is temporarily locked after too many failed attempts.";
            return Page();
        }

        ErrorMessage = "Invalid email or password.";
        return Page();
    }
}
