using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Web.Resources;

namespace MVTeaches.Web.Pages.Admin;

/// <summary>
/// Security review 2026-09-02 (Review Required — Authorization), Stage 1:
/// the one page in the whole admin area deliberately restricted to
/// SystemAdmin ALONE — every other Admin/* page authorizes Admin and
/// SystemAdmin together, but a plain Admin managing who else gets to be an
/// Admin (or what they can touch) would defeat the entire point of this
/// rollout. This is what makes "only SystemAdmin manages admin permissions"
/// true by construction: a plain Admin cannot even load this page to try.
///
/// Creates plain Admin accounts only — never SystemAdmin. Who holds
/// SystemAdmin stays a deliberate, out-of-band decision for this stage (the
/// same one-time Bootstrap mechanism already used for the very first admin
/// account), not something this screen can grant, precisely so nobody can
/// ever create themselves a second Owner account from inside the app.
///
/// A newly created Admin starts with ZERO permission claims — no default
/// grant of any kind. The owner's own Stage 1 instruction was explicit:
/// "لا تعطِ أي صلاحيات تلقائيًا إلا إذا كان هناك سبب واضح" (grant nothing
/// automatically unless there's a clear reason) — and Stage 1 names no such
/// reason, so nothing is granted. A SystemAdmin must tick every permission
/// this new Admin needs, deliberately, right after creating the account.
/// </summary>
[Authorize(Roles = RoleNames.SystemAdmin)]
public class AdminUsersModel : PageModel
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public AdminUsersModel(UserManager<ApplicationUser> userManager, IStringLocalizer<SharedResource> localizer)
    {
        _userManager = userManager;
        _localizer = localizer;
    }

    public record AdminRow(long Id, string Email, IReadOnlyList<string> GrantedPermissions);

    public IReadOnlyList<AdminRow> Admins { get; set; } = Array.Empty<AdminRow>();

    /// <summary>The full Stage 1 key list, in display order — both the
    /// checkbox grid below and OnPostSavePermissionsAsync's own validation
    /// (a posted value outside this list is silently dropped, never stored)
    /// read from this SAME source, so a key can never be offered in the UI
    /// without also being one this page is willing to persist.</summary>
    public IReadOnlyList<string> AllPermissionKeys => PermissionKeys.All;

    [BindProperty]
    public NewAdminInput NewAdmin { get; set; } = new();

    [BindProperty]
    public long EditingAdminId { get; set; }

    [BindProperty]
    public List<string> Granted { get; set; } = new();

    public string? StatusMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public class NewAdminInput
    {
        [Required(ErrorMessage = "Enter an email address."), EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required(ErrorMessage = "Enter a temporary password."), MinLength(10, ErrorMessage = "The password must be at least 10 characters.")]
        public string Password { get; set; } = string.Empty;
    }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostCreateAdminAsync()
    {
        ModelState.Clear(); // this page has more than one [BindProperty] group — see Payments.cshtml.cs's own remarks on the same fix
        if (!TryValidateModel(NewAdmin, nameof(NewAdmin)))
        {
            await LoadAsync();
            return Page();
        }

        var user = new ApplicationUser { UserName = NewAdmin.Email, Email = NewAdmin.Email, EmailConfirmed = true };
        var result = await _userManager.CreateAsync(user, NewAdmin.Password);
        if (!result.Succeeded)
        {
            // Already-localized (LocalizedIdentityErrorDescriber) — same pattern as ChangePasswordModel.
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            await LoadAsync();
            return Page();
        }

        await _userManager.AddToRoleAsync(user, RoleNames.Admin);
        StatusMessage = _localizer[
            "Admin account created for {0} with no permissions yet — grant what they need below. Tell them the temporary password through a separate, secure channel (never this screen or a chat log), and that they must open \"Change password\" after their first sign-in.",
            NewAdmin.Email].Value;
        NewAdmin = new NewAdminInput();
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostSavePermissionsAsync()
    {
        ModelState.Clear();

        var user = await _userManager.FindByIdAsync(EditingAdminId.ToString());
        if (user is null)
        {
            ErrorMessage = _localizer["Account not found."].Value;
            await LoadAsync();
            return Page();
        }

        if (await _userManager.IsInRoleAsync(user, RoleNames.SystemAdmin))
        {
            // Defense in depth: the view below never renders a SystemAdmin row
            // with an editable form in the first place, so this should be
            // unreachable through the UI — refused here too regardless, since
            // SystemAdmin must never depend on anything this screen writes.
            return Forbid();
        }

        // Anything outside the known Stage 1 list is dropped rather than
        // stored — this page can only ever grant what PermissionKeys.All
        // actually names, never an arbitrary string a crafted request added.
        var requested = Granted.Where(k => PermissionKeys.All.Contains(k)).ToHashSet();
        var current = (await _userManager.GetClaimsAsync(user))
            .Where(c => c.Type == PermissionKeys.ClaimType)
            .ToList();

        foreach (var claim in current.Where(c => !requested.Contains(c.Value)))
        {
            await _userManager.RemoveClaimAsync(user, claim);
        }

        foreach (var key in requested.Where(k => current.All(c => c.Value != k)))
        {
            await _userManager.AddClaimAsync(user, new Claim(PermissionKeys.ClaimType, key));
        }

        StatusMessage = _localizer["Permissions updated. This takes effect on this admin's very next request — they do not need to sign out and back in."].Value;
        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        var adminRoleUsers = await _userManager.GetUsersInRoleAsync(RoleNames.Admin);
        var rows = new List<AdminRow>();
        foreach (var user in adminRoleUsers.OrderBy(u => u.Email, StringComparer.OrdinalIgnoreCase))
        {
            // A user holding BOTH Admin and SystemAdmin (e.g. this staging
            // environment's own bootstrap account) is never listed here as an
            // editable target — SystemAdmin's access is an unconditional
            // bypass regardless of any claim, so nothing on this screen could
            // ever mean anything for that account anyway.
            if (await _userManager.IsInRoleAsync(user, RoleNames.SystemAdmin))
            {
                continue;
            }

            var granted = (await _userManager.GetClaimsAsync(user))
                .Where(c => c.Type == PermissionKeys.ClaimType)
                .Select(c => c.Value)
                .ToList();
            rows.Add(new AdminRow(user.Id, user.Email ?? user.UserName ?? string.Empty, granted));
        }

        Admins = rows;
    }
}
