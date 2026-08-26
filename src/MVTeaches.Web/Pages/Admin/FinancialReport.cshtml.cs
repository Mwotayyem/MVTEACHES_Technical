using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MVTeaches.Application.Reports;
using MVTeaches.Infrastructure.Identity;
using NodaTime;

namespace MVTeaches.Web.Pages.Admin;

/// <summary>The owner's own stated MVP scope names "basic financial reports"
/// explicitly (see IFinancialReportService's remarks) — this page is exactly
/// that: a date range picker and three plain, live numbers, nothing more.</summary>
[Authorize(Roles = RoleNames.Admin + "," + RoleNames.SystemAdmin)]
public class FinancialReportModel : PageModel
{
    private readonly IFinancialReportService _reports;

    public FinancialReportModel(IFinancialReportService reports)
    {
        _reports = reports;
    }

    [BindProperty(SupportsGet = true)]
    public DateOnly From { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly To { get; set; }

    public FinancialReport? Report { get; set; }

    public async Task OnGetAsync()
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
    }
}
