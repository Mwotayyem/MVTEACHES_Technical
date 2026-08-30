using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Web.Resources;

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
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public LoginModel(SignInManager<ApplicationUser> signInManager, UserManager<ApplicationUser> userManager,
        IStringLocalizer<SharedResource> localizer)
    {
        _signInManager = signInManager;
        _userManager = userManager;
        _localizer = localizer;
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
            // §22's "MFA إلزامي لـ SystemAdmin وReadOnlyAdmin" (TOTP mandatory
            // for the most-privileged roles — this codebase's closed 5-role
            // set has no distinct "ReadOnlyAdmin", so Admin/SystemAdmin are the
            // two roles that apply here, matching docs/deployment/STATUS.md).
            // This is an honest nudge — a redirect right after login, not a
            // hardened per-request block on every later page — see
            // ManageMfa.cshtml.cs's remarks for why, and STATUS.md for the
            // further-hardening option this deliberately leaves open.
            var signedInUser = await _userManager.FindByEmailAsync(Input.Email);
            if (signedInUser is not null && !await _userManager.GetTwoFactorEnabledAsync(signedInUser))
            {
                var roles = await _userManager.GetRolesAsync(signedInUser);
                if (roles.Contains(RoleNames.Admin) || roles.Contains(RoleNames.SystemAdmin))
                {
                    return RedirectToPage("./ManageMfa");
                }
            }

            return LocalRedirect(returnUrl);
        }

        if (result.RequiresTwoFactor)
        {
            return RedirectToPage("./LoginWith2fa", new { rememberMe = Input.RememberMe, returnUrl });
        }

        if (result.IsLockedOut)
        {
            ErrorMessage = _localizer["This account is temporarily locked after too many failed attempts."].Value;
            return Page();
        }

        ErrorMessage = _localizer["Invalid email or password."].Value;
        return Page();
    }
}
