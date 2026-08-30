using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Web.Resources;

namespace MVTeaches.Web.Pages.Account;

/// <summary>The lost-authenticator-device fallback — each recovery code
/// (from ManageMfa's enrollment/regenerate flow) is single-use, consumed by
/// TwoFactorRecoveryCodeSignInAsync itself.</summary>
public class LoginWithRecoveryCodeModel : PageModel
{
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public LoginWithRecoveryCodeModel(SignInManager<ApplicationUser> signInManager, IStringLocalizer<SharedResource> localizer)
    {
        _signInManager = signInManager;
        _localizer = localizer;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string ReturnUrl { get; set; } = "/";
    public string? ErrorMessage { get; set; }

    public class InputModel
    {
        [Required]
        public string RecoveryCode { get; set; } = string.Empty;
    }

    public async Task<IActionResult> OnGetAsync(string? returnUrl = null)
    {
        var user = await _signInManager.GetTwoFactorAuthenticationUserAsync();
        if (user is null)
        {
            return RedirectToPage("./Login");
        }

        ReturnUrl = returnUrl ?? Url.Content("~/");
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
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

        var code = Input.RecoveryCode.Replace(" ", string.Empty);
        var result = await _signInManager.TwoFactorRecoveryCodeSignInAsync(code);

        if (result.Succeeded)
        {
            return LocalRedirect(ReturnUrl);
        }

        if (result.IsLockedOut)
        {
            ErrorMessage = _localizer["This account is temporarily locked after too many failed attempts."];
            return Page();
        }

        ErrorMessage = _localizer["Invalid recovery code."];
        return Page();
    }
}
