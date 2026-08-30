using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Web.Resources;

namespace MVTeaches.Web.Pages.Account;

/// <summary>
/// §22's "MFA إلزامي لـ SystemAdmin وReadOnlyAdmin" (TOTP mandatory for the
/// most-privileged roles) — this is the enrollment/disable UI that was
/// entirely missing before (Login.cshtml.cs previously detected
/// RequiresTwoFactor and could only report "not built yet" instead of
/// actually challenging the user). This codebase's closed 5-role set
/// (RoleNames) has no distinct "ReadOnlyAdmin" — Admin/SystemAdmin are the
/// roles STATUS.md's own gap entry names, so those are the two the login
/// nudge below targets.
///
/// Available to ANY authenticated password-based account (Admin, SystemAdmin,
/// Teacher) to turn on voluntarily — Guardian/Student sign-in is phone+OTP
/// (§7, blocked on WhatsApp) and never reaches this page. "Mandatory" here
/// means Login.cshtml.cs redirects an Admin/SystemAdmin account here right
/// after a successful password check if MFA isn't enabled yet, rather than
/// sending them to their original destination — a real, honest nudge, NOT a
/// hardened per-request block on every subsequent page (that would need a
/// claims-transformation/middleware layer this pass does not add — flagged
/// in docs/deployment/STATUS.md as a further-hardening option, not silently
/// treated as already done).
/// </summary>
[Authorize]
public class ManageMfaModel : PageModel
{
    private const string Issuer = "MVTeaches";
    private const string AuthenticatorUriFormat = "otpauth://totp/{0}:{1}?secret={2}&issuer={0}&digits=6";

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public ManageMfaModel(UserManager<ApplicationUser> userManager, SignInManager<ApplicationUser> signInManager,
        IStringLocalizer<SharedResource> localizer)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _localizer = localizer;
    }

    public bool IsEnabled { get; set; }
    public bool IsMandatoryForThisAccount { get; set; }
    public string? SharedKey { get; set; }
    public string? AuthenticatorUri { get; set; }
    public int RecoveryCodesRemaining { get; set; }
    public IReadOnlyList<string>? FreshRecoveryCodes { get; set; }

    [BindProperty]
    public VerifyInput Verify { get; set; } = new();

    public string? StatusMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public class VerifyInput
    {
        [Required, StringLength(7, MinimumLength = 6)]
        public string Code { get; set; } = string.Empty;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        await LoadAsync(user);
        return Page();
    }

    public async Task<IActionResult> OnPostVerifyAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        ModelState.Clear();
        if (!TryValidateModel(Verify, nameof(Verify)))
        {
            await LoadAsync(user);
            return Page();
        }

        var code = Verify.Code.Replace(" ", string.Empty).Replace("-", string.Empty);
        var isValid = await _userManager.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, code);
        if (!isValid)
        {
            ErrorMessage = _localizer["That code didn't verify — check the time on your device and try the next code."];
            await LoadAsync(user);
            return Page();
        }

        await _userManager.SetTwoFactorEnabledAsync(user, true);
        var recoveryCodes = await _userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
        FreshRecoveryCodes = recoveryCodes?.ToList();
        StatusMessage = _localizer["Two-factor authentication is now enabled. Save these recovery codes — each is shown only once."];

        await LoadAsync(user);
        return Page();
    }

    public async Task<IActionResult> OnPostDisableAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        await _userManager.SetTwoFactorEnabledAsync(user, false);
        await _userManager.ResetAuthenticatorKeyAsync(user);
        StatusMessage = _localizer["Two-factor authentication disabled. If this account's role requires it, you'll be asked to set it up again next time you sign in."];

        await LoadAsync(user);
        return Page();
    }

    public async Task<IActionResult> OnPostRegenerateRecoveryCodesAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null || !await _userManager.GetTwoFactorEnabledAsync(user))
        {
            return Challenge();
        }

        var recoveryCodes = await _userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
        FreshRecoveryCodes = recoveryCodes?.ToList();
        StatusMessage = _localizer["New recovery codes generated — the old ones no longer work. Save these now."];

        await LoadAsync(user);
        return Page();
    }

    private async Task LoadAsync(ApplicationUser user)
    {
        IsEnabled = await _userManager.GetTwoFactorEnabledAsync(user);
        var roles = await _userManager.GetRolesAsync(user);
        IsMandatoryForThisAccount = roles.Contains(RoleNames.Admin) || roles.Contains(RoleNames.SystemAdmin);
        RecoveryCodesRemaining = await _userManager.CountRecoveryCodesAsync(user);

        if (!IsEnabled)
        {
            var unformattedKey = await _userManager.GetAuthenticatorKeyAsync(user);
            if (string.IsNullOrEmpty(unformattedKey))
            {
                await _userManager.ResetAuthenticatorKeyAsync(user);
                unformattedKey = await _userManager.GetAuthenticatorKeyAsync(user);
            }

            SharedKey = FormatKey(unformattedKey!);
            AuthenticatorUri = string.Format(
                AuthenticatorUriFormat,
                UrlEncoder.Default.Encode(Issuer),
                UrlEncoder.Default.Encode(user.Email ?? user.UserName ?? "account"),
                unformattedKey);
        }
    }

    private static string FormatKey(string unformattedKey)
    {
        var result = new StringBuilder();
        var position = 0;
        while (position + 4 < unformattedKey.Length)
        {
            result.Append(unformattedKey.AsSpan(position, 4)).Append(' ');
            position += 4;
        }

        if (position < unformattedKey.Length)
        {
            result.Append(unformattedKey.AsSpan(position));
        }

        return result.ToString().ToLowerInvariant();
    }
}
