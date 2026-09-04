using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using MVTeaches.Application.Subscriptions;
using MVTeaches.Domain.Subscriptions;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using MVTeaches.Web.Identity;
using MVTeaches.Web.Resources;
using NodaTime;

namespace MVTeaches.Web.Pages.Admin;

/// <summary>
/// Owner decision 2026-09-05: discount codes for packages.
///
/// <para>Guarded by its own <see cref="PermissionKeys.PromoCodesManage"/> and
/// deliberately not by SubscriptionsManage — selling a package and deciding
/// what the centre's prices may be discounted to are different jobs. Every
/// handler here re-checks that key server-side; hiding a form is only ever a
/// courtesy.</para>
///
/// <para>The admin never invents a code. "Generate" produces six characters
/// from A–Z/0–9, checked against the table so a taken one is not offered; the
/// unique index behind <c>code</c> is what actually makes a duplicate
/// impossible, including for a hand-made request that skips this screen.</para>
/// </summary>
[Authorize(Roles = RoleNames.Admin + "," + RoleNames.SystemAdmin)]
[Authorize(Policy = PermissionKeys.PromoCodesManage)]
public class PromoCodesModel : PageModel
{
    private readonly MvTeachesDbContext _db;
    private readonly IPromoCodeService _promoCodes;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IAuthorizationService _authorizationService;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public PromoCodesModel(MvTeachesDbContext db, IPromoCodeService promoCodes,
        UserManager<ApplicationUser> userManager, IAuthorizationService authorizationService,
        IStringLocalizer<SharedResource> localizer)
    {
        _db = db;
        _promoCodes = promoCodes;
        _userManager = userManager;
        _authorizationService = authorizationService;
        _localizer = localizer;
    }

    public record PlanOption(long Id, string Label);

    /// <summary><paramref name="Uses"/> is counted from the subscriptions that
    /// actually carry the code, so it can never disagree with them.</summary>
    public record PromoCodeRow(long Id, string Code, int DiscountPercent, bool IsActive,
        LocalDate? StartsOn, LocalDate? EndsOn, int? MaxTotalUses, int? MaxUsesPerStudent,
        int Uses, IReadOnlyList<string> PlanLabels);

    public IReadOnlyList<PromoCodeRow> Codes { get; set; } = Array.Empty<PromoCodeRow>();
    public IReadOnlyList<PlanOption> Plans { get; set; } = Array.Empty<PlanOption>();

    /// <summary>A freshly generated, unused code, put in the form so the admin
    /// only has to choose the discount. Regenerated on every GET and by the
    /// Generate button — never saved until Create is pressed.</summary>
    public string SuggestedCode { get; set; } = string.Empty;

    [BindProperty]
    public NewCodeInput NewCode { get; set; } = new();

    [BindProperty]
    public EditCodeInput EditCode { get; set; } = new();

    public string? StatusMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public class NewCodeInput
    {
        [Required(ErrorMessage = "Generate a code first.")]
        public string Code { get; set; } = string.Empty;

        [Required(ErrorMessage = "Enter a discount percentage.")]
        [Range(1, 100, ErrorMessage = "A discount must be between 1 and 100 percent.")]
        public int? DiscountPercent { get; set; }

        public bool IsActive { get; set; } = true;

        public DateOnly? StartsOn { get; set; }
        public DateOnly? EndsOn { get; set; }

        [Range(1, int.MaxValue, ErrorMessage = "A usage limit must be at least 1, or empty for unlimited.")]
        public int? MaxTotalUses { get; set; }

        [Range(1, int.MaxValue, ErrorMessage = "A usage limit must be at least 1, or empty for unlimited.")]
        public int? MaxUsesPerStudent { get; set; }

        /// <summary>True means every package, and the ticked list is ignored.</summary>
        public bool AppliesToAllPlans { get; set; } = true;

        public List<long> PricingPlanIds { get; set; } = new();
    }

    public class EditCodeInput
    {
        [Required] public long? Id { get; set; }

        [Required(ErrorMessage = "Enter a discount percentage.")]
        [Range(1, 100, ErrorMessage = "A discount must be between 1 and 100 percent.")]
        public int? DiscountPercent { get; set; }

        public DateOnly? StartsOn { get; set; }
        public DateOnly? EndsOn { get; set; }

        [Range(1, int.MaxValue, ErrorMessage = "A usage limit must be at least 1, or empty for unlimited.")]
        public int? MaxTotalUses { get; set; }

        [Range(1, int.MaxValue, ErrorMessage = "A usage limit must be at least 1, or empty for unlimited.")]
        public int? MaxUsesPerStudent { get; set; }

        public bool AppliesToAllPlans { get; set; } = true;
        public List<long> PricingPlanIds { get; set; } = new();
    }

    public async Task OnGetAsync()
    {
        await LoadAsync();
        SuggestedCode = await _promoCodes.GenerateUnusedCodeAsync(HttpContext.RequestAborted);
        NewCode.Code = SuggestedCode;
    }

    /// <summary>Just another code in the box — nothing is written. The admin may
    /// press this as often as they like before saving.</summary>
    public async Task<IActionResult> OnPostGenerateAsync()
    {
        if (await this.RequirePermissionAsync(_authorizationService, PermissionKeys.PromoCodesManage) is { } deny)
        {
            return deny;
        }

        // This handler's whole input is a button press, so the page-wide
        // ModelState holds only the other forms' unfilled [Required] fields.
        ModelState.Clear();

        await LoadAsync();
        SuggestedCode = await _promoCodes.GenerateUnusedCodeAsync(HttpContext.RequestAborted);
        NewCode.Code = SuggestedCode;
        return Page();
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        if (await this.RequirePermissionAsync(_authorizationService, PermissionKeys.PromoCodesManage) is { } deny)
        {
            return deny;
        }

        ModelState.Clear();
        if (!TryValidateModel(NewCode, nameof(NewCode)))
        {
            await LoadAsync();
            SuggestedCode = NewCode.Code;
            return Page();
        }

        var result = await _promoCodes.CreateAsync(NewCode.Code, NewCode.DiscountPercent!.Value, NewCode.IsActive,
            ToLocalDate(NewCode.StartsOn), ToLocalDate(NewCode.EndsOn), NewCode.MaxTotalUses, NewCode.MaxUsesPerStudent,
            NewCode.AppliesToAllPlans, NewCode.PricingPlanIds, GetCurrentUserId(), HttpContext.RequestAborted);

        if (result.Outcome == CreatePromoCodeOutcome.Created)
        {
            StatusMessage = _localizer["Promo code {0} created.", NewCode.Code].Value;
        }
        else
        {
            ErrorMessage = Describe(result.Outcome);
        }

        await LoadAsync();
        SuggestedCode = result.Outcome == CreatePromoCodeOutcome.Created
            ? await _promoCodes.GenerateUnusedCodeAsync(HttpContext.RequestAborted)
            : NewCode.Code;
        NewCode = new NewCodeInput { Code = SuggestedCode };
        return Page();
    }

    public async Task<IActionResult> OnPostUpdateAsync()
    {
        if (await this.RequirePermissionAsync(_authorizationService, PermissionKeys.PromoCodesManage) is { } deny)
        {
            return deny;
        }

        ModelState.Clear();
        if (!TryValidateModel(EditCode, nameof(EditCode)))
        {
            await LoadAsync();
            return Page();
        }

        var outcome = await _promoCodes.UpdateAsync(EditCode.Id!.Value, EditCode.DiscountPercent!.Value,
            ToLocalDate(EditCode.StartsOn), ToLocalDate(EditCode.EndsOn), EditCode.MaxTotalUses,
            EditCode.MaxUsesPerStudent, EditCode.AppliesToAllPlans, EditCode.PricingPlanIds,
            HttpContext.RequestAborted);

        if (outcome == UpdatePromoCodeOutcome.Updated)
        {
            StatusMessage = _localizer["Promo code updated. Packages already bought keep the discount they were bought with."].Value;
        }
        else
        {
            ErrorMessage = outcome switch
            {
                UpdatePromoCodeOutcome.NotFound => _localizer["Promo code not found."].Value,
                UpdatePromoCodeOutcome.InvalidDiscountPercent => _localizer["A discount must be between 1 and 100 percent."].Value,
                UpdatePromoCodeOutcome.InvalidWindow => _localizer["A promo code cannot end before it starts."].Value,
                UpdatePromoCodeOutcome.InvalidUsageLimit => _localizer["A usage limit must be at least 1, or empty for unlimited."].Value,
                _ => _localizer["Choose at least one package, or set the code to apply to every package."].Value,
            };
        }

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostSetActiveAsync(long promoCodeId, bool isActive)
    {
        if (await this.RequirePermissionAsync(_authorizationService, PermissionKeys.PromoCodesManage) is { } deny)
        {
            return deny;
        }

        ModelState.Clear();

        var found = await _promoCodes.SetActiveAsync(promoCodeId, isActive, HttpContext.RequestAborted);
        if (found)
        {
            StatusMessage = isActive
                ? _localizer["Promo code enabled."].Value
                : _localizer["Promo code disabled. It stops working straight away; packages already bought are unaffected."].Value;
        }
        else
        {
            ErrorMessage = _localizer["Promo code not found."].Value;
        }

        await LoadAsync();
        return Page();
    }

    private string Describe(CreatePromoCodeOutcome outcome) => outcome switch
    {
        CreatePromoCodeOutcome.MalformedCode => _localizer["A promo code must be exactly 6 characters, using A-Z and 0-9 only."].Value,
        CreatePromoCodeOutcome.InvalidDiscountPercent => _localizer["A discount must be between 1 and 100 percent."].Value,
        CreatePromoCodeOutcome.InvalidWindow => _localizer["A promo code cannot end before it starts."].Value,
        CreatePromoCodeOutcome.InvalidUsageLimit => _localizer["A usage limit must be at least 1, or empty for unlimited."].Value,
        CreatePromoCodeOutcome.DuplicateCode => _localizer["That code already exists — generate another one."].Value,
        _ => _localizer["Choose at least one package, or set the code to apply to every package."].Value,
    };

    private static LocalDate? ToLocalDate(DateOnly? date) =>
        date is { } d ? new LocalDate(d.Year, d.Month, d.Day) : null;

    private long GetCurrentUserId() => long.Parse(_userManager.GetUserId(User)!);

    private async Task LoadAsync()
    {
        var courseNames = await _db.Courses.ToDictionaryAsync(c => c.Id, c => c.NameEn);
        var levelCodes = await _db.Levels.ToDictionaryAsync(l => l.Id, l => l.Code);

        var plans = await _db.PricingPlans.Where(p => p.IsActive).OrderBy(p => p.CourseId).ToListAsync();
        Plans = plans.Select(p => new PlanOption(p.Id,
                $"{courseNames.GetValueOrDefault(p.CourseId, "?")} " +
                $"{(p.LevelId is null ? "" : levelCodes.GetValueOrDefault(p.LevelId.Value, "?"))} — " +
                $"{p.Amount.Amount:0.###} {p.Amount.Currency}"))
            .ToList();

        var codes = await _db.PromoCodes.OrderByDescending(p => p.Id).ToListAsync();
        var ids = codes.Select(c => c.Id).ToList();
        var uses = await _promoCodes.CountUsesAsync(ids, HttpContext.RequestAborted);
        var scopes = await _promoCodes.GetPlanScopesAsync(ids, HttpContext.RequestAborted);
        var planLabels = Plans.ToDictionary(p => p.Id, p => p.Label);

        Codes = codes.Select(c => new PromoCodeRow(c.Id, c.Code, c.DiscountPercent, c.IsActive,
                c.StartsOn, c.EndsOn, c.MaxTotalUses, c.MaxUsesPerStudent,
                uses.GetValueOrDefault(c.Id),
                scopes.GetValueOrDefault(c.Id, Array.Empty<long>())
                    .Select(id => planLabels.GetValueOrDefault(id, $"#{id}")).ToList()))
            .ToList();
    }
}
