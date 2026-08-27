using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Payroll;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Delivery;
using MVTeaches.Domain.Payroll;
using MVTeaches.Domain.People;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;

namespace MVTeaches.Web.Pages.Admin;

/// <summary>
/// §18.1/§18.2 (D-26) — the admin surface over the already-tested
/// declare → verify → aggregate → review → approve → pay → close cycle.
/// This page shows the two things an admin actually needs: deliveries a
/// teacher declared and waiting on verification, and the payroll periods
/// themselves at whatever stage they're in.
/// </summary>
[Authorize(Roles = RoleNames.Admin + "," + RoleNames.SystemAdmin)]
public class PayrollModel : PageModel
{
    private readonly MvTeachesDbContext _db;
    private readonly IPayrollService _payroll;
    private readonly UserManager<ApplicationUser> _userManager;

    public PayrollModel(MvTeachesDbContext db, IPayrollService payroll, UserManager<ApplicationUser> userManager)
    {
        _db = db;
        _payroll = payroll;
        _userManager = userManager;
    }

    public record DeclaredRow(long SessionId, string TeacherName, int DeclaredMinutes, string? Note);
    public record PeriodRow(long Id, int CountryId, LocalDate Start, LocalDate End, PayrollPeriodStatus Status, int LineCount, decimal TotalAmount);

    public IReadOnlyList<DeclaredRow> DeclaredDeliveries { get; set; } = Array.Empty<DeclaredRow>();
    public IReadOnlyList<PeriodRow> Periods { get; set; } = Array.Empty<PeriodRow>();
    public IReadOnlyList<Country> Countries { get; set; } = Array.Empty<Country>();

    [BindProperty]
    public OpenPeriodInput NewPeriod { get; set; } = new();

    [BindProperty]
    public string? RejectReason { get; set; }

    public string? StatusMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public class OpenPeriodInput
    {
        [Required]
        public int CountryId { get; set; }

        [Required]
        public DateOnly Start { get; set; }

        [Required]
        public DateOnly End { get; set; }
    }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostVerifyAsync(long sessionId)
    {
        var verifiedByUserId = long.Parse(_userManager.GetUserId(User)!);

        try
        {
            var result = await _payroll.VerifyAsync(sessionId, verifiedByUserId, note: null, HttpContext.RequestAborted);
            ErrorMessage = result.Outcome switch
            {
                VerifyDeliveryOutcome.SameActorAsDeclarer => "You verified your own declaration — §18.3 rule 3 requires a different admin.",
                VerifyDeliveryOutcome.NoApplicableRate => "No teacher rate applies for this session yet — create one before verifying.",
                VerifyDeliveryOutcome.DeliveryNotFound => "Delivery not found.",
                VerifyDeliveryOutcome.NotDeclared => "This delivery hasn't been declared yet.",
                _ => null,
            };
            if (result.Outcome == VerifyDeliveryOutcome.Verified)
            {
                StatusMessage = "Delivery verified.";
            }
        }
        catch (InvalidOperationException ex)
        {
            ErrorMessage = ex.Message;
        }

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostRejectAsync(long sessionId)
    {
        var rejectedByUserId = long.Parse(_userManager.GetUserId(User)!);
        var reason = string.IsNullOrWhiteSpace(RejectReason) ? "No reason given" : RejectReason;

        try
        {
            await _payroll.RejectAsync(sessionId, rejectedByUserId, reason, HttpContext.RequestAborted);
            StatusMessage = "Delivery rejected.";
        }
        catch (InvalidOperationException ex)
        {
            ErrorMessage = ex.Message;
        }

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostOpenPeriodAsync()
    {
        // See StudentsModel's OnPostRegisterGuardianAsync remarks.
        ModelState.Clear();
        if (!TryValidateModel(NewPeriod, nameof(NewPeriod)))
        {
            await LoadAsync();
            return Page();
        }

        var start = new LocalDate(NewPeriod.Start.Year, NewPeriod.Start.Month, NewPeriod.Start.Day);
        var end = new LocalDate(NewPeriod.End.Year, NewPeriod.End.Month, NewPeriod.End.Day);

        try
        {
            await _payroll.OpenPeriodAsync(NewPeriod.CountryId, start, end, HttpContext.RequestAborted);
            StatusMessage = "Payroll period opened.";
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            // UNIQUE(country_id, period_start, period_end) — a real, expected
            // conflict (the admin double-clicked, or this exact period already
            // exists), not a bug; surface it the same friendly way every other
            // page in this app handles a genuine unique-constraint hit.
            ErrorMessage = "A payroll period for this country and date range already exists.";
        }
        catch (ArgumentException ex)
        {
            ErrorMessage = ex.Message;
        }

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAggregateAsync(long periodId)
    {
        var created = await _payroll.AggregateVerifiedDeliveriesAsync(periodId, HttpContext.RequestAborted);
        StatusMessage = $"{created} payroll line(s) added.";
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostMoveToReviewAsync(long periodId)
    {
        try
        {
            await _payroll.MoveToReviewAsync(periodId, HttpContext.RequestAborted);
            StatusMessage = "Period moved to review.";
        }
        catch (InvalidOperationException ex)
        {
            ErrorMessage = ex.Message;
        }

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostApproveAsync(long periodId)
    {
        var approvedByUserId = long.Parse(_userManager.GetUserId(User)!);
        try
        {
            await _payroll.ApprovePeriodAsync(periodId, approvedByUserId, HttpContext.RequestAborted);
            StatusMessage = "Period approved and locked (§18.3 rule 1).";
        }
        catch (InvalidOperationException ex)
        {
            ErrorMessage = ex.Message;
        }

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostMarkPaidAsync(long periodId)
    {
        try
        {
            await _payroll.MarkPeriodPaidAsync(periodId, HttpContext.RequestAborted);
            StatusMessage = "Period marked paid.";
        }
        catch (InvalidOperationException ex)
        {
            ErrorMessage = ex.Message;
        }

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostCloseAsync(long periodId)
    {
        try
        {
            await _payroll.ClosePeriodAsync(periodId, HttpContext.RequestAborted);
            StatusMessage = "Period closed.";
        }
        catch (InvalidOperationException ex)
        {
            ErrorMessage = ex.Message;
        }

        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        Countries = await _db.Countries.Where(c => c.IsActive).OrderBy(c => c.NameEn).ToListAsync();

        var declared = await _db.SessionDeliveries
            .Where(d => d.State == DeliveryState.Declared)
            .OrderBy(d => d.DeclaredAtUtc)
            .ToListAsync();
        var teacherNames = await _db.Teachers.ToDictionaryAsync(t => t.Id, t => t.FullName);
        DeclaredDeliveries = declared.Select(d => new DeclaredRow(
            d.SessionId, teacherNames.GetValueOrDefault(d.TeacherId, $"#{d.TeacherId}"), d.DeclaredMinutes ?? 0, d.TeacherNote)).ToList();

        var periods = await _db.PayrollPeriods.OrderByDescending(p => p.Id).ToListAsync();
        var linesByPeriod = await _db.PayrollLines
            .GroupBy(l => l.PeriodId)
            .Select(g => new { PeriodId = g.Key, Count = g.Count(), Total = g.Sum(l => l.Amount) })
            .ToDictionaryAsync(g => g.PeriodId, g => (g.Count, g.Total));
        Periods = periods.Select(p =>
        {
            var (count, total) = linesByPeriod.GetValueOrDefault(p.Id, (0, 0m));
            return new PeriodRow(p.Id, p.CountryId, p.PeriodStart, p.PeriodEnd, p.Status, count, total);
        }).ToList();
    }
}
