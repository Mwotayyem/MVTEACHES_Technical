using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using MVTeaches.Application.Reports;
using MVTeaches.Domain.Common;
using MVTeaches.Domain.Finance;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Web.Resources;
using NodaTime;

namespace MVTeaches.Web.Pages.Admin;

/// <summary>The owner's own stated MVP scope names "basic financial reports"
/// explicitly (see IFinancialReportService's remarks) — the original three
/// numbers here were built to that letter. Owner decision 2026-08-30 rule 9
/// is a later, explicit, dated instruction naming five more figures by name;
/// this page's extension follows that instruction, not a reversal of the
/// original discipline (see IFinancialReportService's own updated remarks).
/// </summary>
[Authorize(Roles = RoleNames.Admin + "," + RoleNames.SystemAdmin)]
public class FinancialReportModel : PageModel
{
    private readonly IFinancialReportService _reports;
    private readonly IOperatingExpenseService _expenses;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly MVTeaches.Infrastructure.Persistence.MvTeachesDbContext _db;

    public FinancialReportModel(IFinancialReportService reports, IOperatingExpenseService expenses,
        UserManager<ApplicationUser> userManager, IStringLocalizer<SharedResource> localizer,
        MVTeaches.Infrastructure.Persistence.MvTeachesDbContext db)
    {
        _reports = reports;
        _expenses = expenses;
        _userManager = userManager;
        _localizer = localizer;
        _db = db;
    }

    [BindProperty(SupportsGet = true)]
    public DateOnly From { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly To { get; set; }

    public FinancialReport? Report { get; set; }

    /// <summary>Owner decision 2026-08-30 rule 9: "month-over-month
    /// comparison" — the immediately preceding period of the SAME length,
    /// computed by calling GenerateAsync a second time rather than the
    /// service inventing a dedicated comparison abstraction.</summary>
    public FinancialReport? PreviousPeriodReport { get; set; }

    public IReadOnlyList<OperatingExpense> Expenses { get; set; } = Array.Empty<OperatingExpense>();

    /// <summary>The expense form used to ask for a raw "Country Id" and a
    /// typed currency code; both are now picked from configured data.</summary>
    public IReadOnlyList<MVTeaches.Domain.Catalog.Country> Countries { get; set; } =
        Array.Empty<MVTeaches.Domain.Catalog.Country>();

    public IReadOnlyList<string> Currencies { get; set; } = Array.Empty<string>();

    /// <summary>Categories this centre has actually used before, offered as
    /// suggestions while typing. Read from the expenses themselves rather than
    /// a list written into the code — what counts as a category is the
    /// centre's business, not this page's.</summary>
    public IReadOnlyList<string> KnownCategories { get; set; } = Array.Empty<string>();

    public bool IsArabic => System.Globalization.CultureInfo.CurrentUICulture
        .TwoLetterISOLanguageName.Equals("ar", StringComparison.OrdinalIgnoreCase);

    public string DisplayCountry(MVTeaches.Domain.Catalog.Country country) => IsArabic ? country.NameAr : country.NameEn;

    [BindProperty]
    public NewExpenseInput NewExpense { get; set; } = new();

    public string? StatusMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public class NewExpenseInput
    {
        // Nullable so [Required] actually fires — a non-nullable int/DateOnly
        // silently passes validation as 0 / 0001-01-01.
        [Required(ErrorMessage = "Choose a country.")] public int? CountryId { get; set; }
        [Required(ErrorMessage = "Enter a category, for example Rent or Marketing.")] public string Category { get; set; } = string.Empty;
        [Required, Range(0.001, double.MaxValue, ErrorMessage = "Enter the amount.")] public decimal Amount { get; set; }
        [Required(ErrorMessage = "Choose a currency."), StringLength(3, MinimumLength = 3)] public string Currency { get; set; } = string.Empty;
        [Required(ErrorMessage = "Enter the date the expense was incurred.")] public DateOnly? IncurredOn { get; set; }
        public string? Note { get; set; }
    }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostRecordExpenseAsync()
    {
        ModelState.Clear();
        if (!TryValidateModel(NewExpense, nameof(NewExpense)))
        {
            await LoadAsync();
            return Page();
        }

        var actingUserId = long.Parse(_userManager.GetUserId(User)!);
        var incurred = NewExpense.IncurredOn!.Value;
        var incurredOn = new LocalDate(incurred.Year, incurred.Month, incurred.Day);
        var result = await _expenses.RecordAsync(NewExpense.CountryId!.Value, NewExpense.Category,
            new Money(NewExpense.Amount, NewExpense.Currency), incurredOn, NewExpense.Note, actingUserId, HttpContext.RequestAborted);

        StatusMessage = result.Outcome == RecordExpenseOutcome.Recorded ? _localizer["Expense recorded."].Value : null;
        ErrorMessage = result.Outcome switch
        {
            RecordExpenseOutcome.PayrollCategoryNotAllowed => _localizer["Teacher payroll must never be entered as a manual expense — it is already counted automatically."].Value,
            RecordExpenseOutcome.InvalidAmount => _localizer["The amount must be positive."].Value,
            _ => null,
        };

        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        Countries = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
            _db.Countries.Where(c => c.IsActive).OrderBy(c => c.Id));
        Currencies = Countries.Select(c => c.CurrencyCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        KnownCategories = (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
                _db.OperatingExpenses.Select(e => e.Category).Distinct()))
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .OrderBy(c => c, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (From == default || To == default)
        {
            // Default to the current UTC calendar month on first load.
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            From = new DateOnly(today.Year, today.Month, 1);
            To = today;
        }

        if (To < From)
        {
            ModelState.AddModelError(string.Empty, _localizer["The end date must not be before the start date."]);
            return;
        }

        var periodStart = new LocalDate(From.Year, From.Month, From.Day);
        var periodEnd = new LocalDate(To.Year, To.Month, To.Day);
        Report = await _reports.GenerateAsync(periodStart, periodEnd, HttpContext.RequestAborted);
        Expenses = await _expenses.ListAsync(periodStart, periodEnd, HttpContext.RequestAborted);

        var periodLengthDays = Period.Between(periodStart, periodEnd.PlusDays(1), PeriodUnits.Days).Days;
        var previousPeriodEnd = periodStart.PlusDays(-1);
        var previousPeriodStart = previousPeriodEnd.PlusDays(-(periodLengthDays - 1));
        PreviousPeriodReport = await _reports.GenerateAsync(previousPeriodStart, previousPeriodEnd, HttpContext.RequestAborted);
    }
}
