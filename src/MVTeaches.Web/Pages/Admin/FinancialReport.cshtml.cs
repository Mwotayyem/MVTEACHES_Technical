using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MVTeaches.Application.Reports;
using MVTeaches.Domain.Common;
using MVTeaches.Domain.Finance;
using MVTeaches.Infrastructure.Identity;
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

    public FinancialReportModel(IFinancialReportService reports, IOperatingExpenseService expenses, UserManager<ApplicationUser> userManager)
    {
        _reports = reports;
        _expenses = expenses;
        _userManager = userManager;
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

    [BindProperty]
    public NewExpenseInput NewExpense { get; set; } = new();

    public string? StatusMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public class NewExpenseInput
    {
        [Required] public int CountryId { get; set; }
        [Required] public string Category { get; set; } = string.Empty;
        [Required, Range(0.001, double.MaxValue)] public decimal Amount { get; set; }
        [Required, StringLength(3, MinimumLength = 3)] public string Currency { get; set; } = string.Empty;
        [Required] public DateOnly IncurredOn { get; set; }
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
        var incurredOn = new LocalDate(NewExpense.IncurredOn.Year, NewExpense.IncurredOn.Month, NewExpense.IncurredOn.Day);
        var result = await _expenses.RecordAsync(NewExpense.CountryId, NewExpense.Category,
            new Money(NewExpense.Amount, NewExpense.Currency), incurredOn, NewExpense.Note, actingUserId, HttpContext.RequestAborted);

        StatusMessage = result.Outcome == RecordExpenseOutcome.Recorded ? "Expense recorded." : null;
        ErrorMessage = result.Outcome switch
        {
            RecordExpenseOutcome.PayrollCategoryNotAllowed => "Teacher payroll must never be entered as a manual expense — it is already counted automatically.",
            RecordExpenseOutcome.InvalidAmount => "The amount must be positive.",
            _ => null,
        };

        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        if (From == default || To == default)
        {
            // Default to the current UTC calendar month on first load.
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            From = new DateOnly(today.Year, today.Month, 1);
            To = today;
        }

        if (To < From)
        {
            ModelState.AddModelError(string.Empty, "The end date must not be before the start date.");
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
