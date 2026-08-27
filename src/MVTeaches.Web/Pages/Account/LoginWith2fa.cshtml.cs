using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MVTeaches.Infrastructure.Identity;

namespace MVTeaches.Web.Pages.Account;

/// <summary>
/// The second step of sign-in for an account with TwoFactorEnabled — reached
/// only via Login.cshtml.cs's RequiresTwoFactor branch, never directly: it
/// relies on SignInManager's own short-lived "passed password, pending 2FA"
/// intermediate cookie (GetTwoFactorAuthenticationUserAsync), the same
/// standard mechanism the ASP.NET Core Identity scaffolding uses — not a
/// custom session/claims mechanism built here.
/// </summary>
public class LoginWith2faModel : PageModel
{
    private readonly SignInManager<ApplicationUser> _signInManager;

    public LoginWith2faModel(SignInManager<ApplicationUser> signInManager)
    {
        _signInManager = signInManager;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool RememberMe { get; set; }
    public string ReturnUrl { get; set; } = "/";
    public string? ErrorMessage { get; set; }

    public class InputModel
    {
        [Required, StringLength(7, MinimumLength = 6)]
        public string Code { get; set; } = string.Empty;

        public bool RememberMachine { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(bool rememberMe, string? returnUrl = null)
    {
        var user = await _signInManager.GetTwoFactorAuthenticationUserAsync();
        if (user is null)
        {
            // No pending 2FA state — someone navigated here directly rather
            // than through a real password-sign-in attempt.
            return RedirectToPage("./Login");
        }

        RememberMe = rememberMe;
        ReturnUrl = returnUrl ?? Url.Content("~/");
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(bool rememberMe, string? returnUrl = null)
    {
        RememberMe = rememberMe;
        ReturnUrl = returnUrl ?? Url.Content("~/");

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var user = await _signInManager.GetTwoFactorAuthenticationUserAsync();
        if (user is null)
        {
            return RedirectToPage("./Login");
        }

        var code = Input.Code.Replace(" ", string.Empty).Replace("-", string.Empty);
        var result = await _signInManager.TwoFactorAuthenticatorSignInAsync(code, rememberMe, Input.RememberMachine);

        if (result.Succeeded)
        {
            return LocalRedirect(ReturnUrl);
        }

        if (result.IsLockedOut)
        {
            ErrorMessage = "This account is temporarily locked after too many failed attempts.";
            return Page();
        }

        ErrorMessage = "Invalid verification code.";
        return Page();
    }
}
