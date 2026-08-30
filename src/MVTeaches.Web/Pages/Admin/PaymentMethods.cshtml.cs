using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using MVTeaches.Application.Payments;
using MVTeaches.Domain.Payments;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Web.Resources;

namespace MVTeaches.Web.Pages.Admin;

/// <summary>
/// Owner decision 2026-08-30 (manual payment methods): "لا تنشئ بوابة دفع
/// وهمية" — there is no payment gateway here at all. This page only manages
/// the beneficiary details (CliQ alias, IBAN/SWIFT, bank name, cash) a payer
/// is shown before sending a REAL transfer outside the platform; every
/// confirmation of money actually arriving stays a manual admin action on
/// /Admin/Payments. A method missing its required fields, or not activated
/// here, is never offered to a payer as available (see
/// IPaymentMethodConfigService.ListActiveAsync, the only query
/// purchase/transfer pages are allowed to read from).
/// </summary>
[Authorize(Roles = RoleNames.Admin + "," + RoleNames.SystemAdmin)]
public class PaymentMethodsModel : PageModel
{
    private readonly IPaymentMethodConfigService _methods;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public PaymentMethodsModel(IPaymentMethodConfigService methods, UserManager<ApplicationUser> userManager,
        IStringLocalizer<SharedResource> localizer)
    {
        _methods = methods;
        _userManager = userManager;
        _localizer = localizer;
    }

    public IReadOnlyList<PaymentMethodConfig> Methods { get; set; } = Array.Empty<PaymentMethodConfig>();

    [BindProperty]
    public NewMethodInput NewMethod { get; set; } = new();

    public string? StatusMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public class NewMethodInput
    {
        [Required] public PaymentMethod Type { get; set; }
        public string? BeneficiaryName { get; set; }
        public string? CliqAlias { get; set; }
        public string? Iban { get; set; }
        public string? BankName { get; set; }
        public string? SwiftBic { get; set; }
        public string? CountryName { get; set; }
        public string? Instructions { get; set; }
        [Required] public string AcceptedCurrencies { get; set; } = string.Empty;
    }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostCreateAsync()
    {
        ModelState.Clear();
        if (!TryValidateModel(NewMethod, nameof(NewMethod)))
        {
            await LoadAsync();
            return Page();
        }

        var actingUserId = GetCurrentUserId();
        var currencies = NewMethod.AcceptedCurrencies.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        try
        {
            await _methods.CreateAsync(NewMethod.Type, NewMethod.BeneficiaryName ?? string.Empty, NewMethod.CliqAlias,
                NewMethod.Iban, NewMethod.BankName, NewMethod.SwiftBic, NewMethod.CountryName, NewMethod.Instructions,
                currencies, actingUserId, HttpContext.RequestAborted);
            StatusMessage = _localizer["Payment method added."];
        }
        catch (ArgumentException ex)
        {
            ErrorMessage = ex.Message;
        }

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostDeactivateAsync(long id)
    {
        var actingUserId = GetCurrentUserId();
        await _methods.DeactivateAsync(id, actingUserId, HttpContext.RequestAborted);
        StatusMessage = _localizer["Payment method deactivated."];
        await LoadAsync();
        return Page();
    }

    private long GetCurrentUserId() => long.Parse(_userManager.GetUserId(User)!);

    private async Task LoadAsync() => Methods = await _methods.ListAllAsync(HttpContext.RequestAborted);
}
