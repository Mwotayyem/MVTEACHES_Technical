using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Web.Resources;

namespace MVTeaches.Web.Pages.Account;

/// <summary>
/// Security review 2026-09-02 (Review Required — Auth): the app had no
/// self-service way for ANY account, including a freshly-handed-over admin,
/// to change its own password after signing in with a temporary one — the
/// only paths that ever set a password were account creation (Bootstrap,
/// admin-assisted registration) and the not-yet-built Forgot Password flow.
/// This page closes that specific gap and nothing else: no Forgot Password,
/// no email reset link, no forced-change flag, no change to Login.cshtml.cs
/// or MFA's own enrollment/challenge pages.
///
/// The acting user is always resolved from the authenticated principal
/// (<see cref="UserManager{TUser}.GetUserAsync"/>) — there is no user id or
/// email accepted anywhere on this page, so there is no way to reach this
/// handler and change a DIFFERENT account's password.
/// </summary>
[Authorize]
public class ChangePasswordModel : PageModel
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public ChangePasswordModel(UserManager<ApplicationUser> userManager, SignInManager<ApplicationUser> signInManager,
        IStringLocalizer<SharedResource> localizer)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _localizer = localizer;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string? StatusMessage { get; set; }

    public class InputModel
    {
        [Required(ErrorMessage = "Enter your current password.")]
        [DataType(DataType.Password)]
        public string CurrentPassword { get; set; } = string.Empty;

        [Required(ErrorMessage = "Enter a new password.")]
        [DataType(DataType.Password)]
        public string NewPassword { get; set; } = string.Empty;

        [Required(ErrorMessage = "Confirm the new password.")]
        [DataType(DataType.Password)]
        [Compare(nameof(NewPassword), ErrorMessage = "The new password and its confirmation do not match.")]
        public string ConfirmNewPassword { get; set; } = string.Empty;
    }

    public IActionResult OnGet() => User.Identity?.IsAuthenticated == true ? Page() : Challenge();

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        // The real password policy (length, character classes — see
        // Program.cs's Identity options) is enforced here by
        // ChangePasswordAsync itself, exactly like every other place this
        // app sets a password (Bootstrap, admin-assisted registration) —
        // not duplicated as a second, possibly-drifting copy on this page.
        // Its own PasswordMismatch check also means a wrong current
        // password is refused here without ever exposing which part
        // (current vs. new) failed differently from any other Identity
        // failure — same one-message-for-any-reason posture Login.cshtml.cs
        // already uses for sign-in.
        var result = await _userManager.ChangePasswordAsync(user, Input.CurrentPassword, Input.NewPassword);
        if (!result.Succeeded)
        {
            // IdentityError.Description is already localized — see
            // LocalizedIdentityErrorDescriber, registered in Program.cs —
            // so this surfaces in Arabic or English exactly like every
            // other Identity failure in this app (registration, etc.).
            // Never logged, never includes either password value.
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            return Page();
        }

        // ChangePasswordAsync rotates the account's security stamp. Without
        // refreshing the sign-in cookie now, this same request's user stays
        // signed in only until ASP.NET Core's SecurityStampValidator next
        // checks it (periodic, not immediate) and then gets signed out with
        // no clear reason shown — RefreshSignInAsync reissues the cookie
        // right away so "you stay in your account" (requirement) is actually
        // true, not just true until the next validator sweep.
        await _signInManager.RefreshSignInAsync(user);

        StatusMessage = _localizer["Your password has been changed."];
        Input = new InputModel();
        ModelState.Clear();
        return Page();
    }
}
