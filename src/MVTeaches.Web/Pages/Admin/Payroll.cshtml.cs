using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using MVTeaches.Application.Payroll;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Delivery;
using MVTeaches.Domain.Payroll;
using MVTeaches.Domain.People;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using MVTeaches.Web.Display;
using MVTeaches.Web.Resources;
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
    private readonly IStringLocalizer<SharedResource> _localizer;

    public PayrollModel(MvTeachesDbContext db, IPayrollService payroll, UserManager<ApplicationUser> userManager,
        IStringLocalizer<SharedResource> localizer)
    {
        _db = db;
        _payroll = payroll;
        _userManager = userManager;
        _localizer = localizer;
    }

    public record DeclaredRow(long SessionId, string TeacherName, string SessionLabel, int DeclaredMinutes, string? Note);
    public record PeriodRow(long Id, string CountryName, string CurrencyCode, LocalDate Start, LocalDate End,
        PayrollPeriodStatus Status, int LineCount, decimal TotalAmount);

    public IReadOnlyList<DeclaredRow> DeclaredDeliveries { get; set; } = Array.Empty<DeclaredRow>();
    public IReadOnlyList<PeriodRow> Periods { get; set; } = Array.Empty<PeriodRow>();
    public IReadOnlyList<Country> Countries { get; set; } = Array.Empty<Country>();

    [BindProperty]
    public OpenPeriodInput NewPeriod { get; set; } = new();

    [BindProperty]
    public string? RejectReason { get; set; }

    public string? StatusMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public bool IsArabic => System.Globalization.CultureInfo.CurrentUICulture
        .TwoLetterISOLanguageName.Equals("ar", StringComparison.OrdinalIgnoreCase);

    public class OpenPeriodInput
    {
        // All three are nullable so [Required] actually fires. Left as
        // non-nullable value types, an empty form posted CountryId 0 and a
        // period running 0001-01-01 → 0001-01-01, which is exactly the
        // "01/01/0001" an admin was seeing in the period list.
        [Required]
        public int? CountryId { get; set; }

        [Required]
        public DateOnly? Start { get; set; }

        [Required]
        public DateOnly? End { get; set; }
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
                VerifyDeliveryOutcome.SameActorAsDeclarer => _localizer["You verified your own declaration — a different admin must verify it."].Value,
                VerifyDeliveryOutcome.NoApplicableRate => _localizer["No teacher rate applies for this session yet — create one before verifying."].Value,
                VerifyDeliveryOutcome.DeliveryNotFound => _localizer["Delivery not found."].Value,
                VerifyDeliveryOutcome.NotDeclared => _localizer["This delivery hasn't been declared yet."].Value,
                _ => null,
            };
            if (result.Outcome == VerifyDeliveryOutcome.Verified)
            {
                StatusMessage = _localizer["Delivery verified."].Value;
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
        var reason = string.IsNullOrWhiteSpace(RejectReason) ? _localizer["No reason given"].Value : RejectReason;

        try
        {
            await _payroll.RejectAsync(sessionId, rejectedByUserId, reason, HttpContext.RequestAborted);
            StatusMessage = _localizer["Delivery rejected."].Value;
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

        var startDate = NewPeriod.Start!.Value;
        var endDate = NewPeriod.End!.Value;
        var start = new LocalDate(startDate.Year, startDate.Month, startDate.Day);
        var end = new LocalDate(endDate.Year, endDate.Month, endDate.Day);

        try
        {
            await _payroll.OpenPeriodAsync(NewPeriod.CountryId!.Value, start, end, HttpContext.RequestAborted);
            StatusMessage = _localizer["Payroll period opened."].Value;
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            // UNIQUE(country_id, period_start, period_end) — a real, expected
            // conflict (the admin double-clicked, or this exact period already
            // exists), not a bug; surface it the same friendly way every other
            // page in this app handles a genuine unique-constraint hit.
            ErrorMessage = _localizer["A payroll period for this country and date range already exists."].Value;
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
        StatusMessage = _localizer["{0} payroll line(s) added.", created].Value;
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostMoveToReviewAsync(long periodId)
    {
        try
        {
            await _payroll.MoveToReviewAsync(periodId, HttpContext.RequestAborted);
            StatusMessage = _localizer["Period moved to review."].Value;
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
            StatusMessage = _localizer["Period approved and locked — its lines can no longer change."].Value;
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
            StatusMessage = _localizer["Period marked paid."].Value;
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
            StatusMessage = _localizer["Period closed."].Value;
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

        // A declared delivery used to be listed as a bare "#57" — the admin
        // verifying it needs to see which lesson that actually was.
        var declaredSessionIds = declared.Select(d => d.SessionId).Distinct().ToList();
        var declaredSessions = await _db.ClassSessions
            .Where(s => declaredSessionIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => new { s.StartsAtUtc, s.ScheduleTimeZone, s.LevelId });
        var levelCodes = await _db.Levels.ToDictionaryAsync(l => l.Id, l => l.Code);

        DeclaredDeliveries = declared.Select(d =>
        {
            var session = declaredSessions.GetValueOrDefault(d.SessionId);
            var label = session is null
                ? _localizer.NotSpecified()
                : $"{_localizer.SessionOption(session.StartsAtUtc, session.ScheduleTimeZone)} · {levelCodes.GetValueOrDefault(session.LevelId, "?")}";
            return new DeclaredRow(d.SessionId, teacherNames.GetValueOrDefault(d.TeacherId, string.Empty), label,
                d.DeclaredMinutes ?? 0, d.TeacherNote);
        }).ToList();

        var periods = await _db.PayrollPeriods.OrderByDescending(p => p.Id).ToListAsync();
        var linesByPeriod = await _db.PayrollLines
            .GroupBy(l => l.PeriodId)
            .Select(g => new { PeriodId = g.Key, Count = g.Count(), Total = g.Sum(l => l.Amount) })
            .ToDictionaryAsync(g => g.PeriodId, g => (g.Count, g.Total));
        // The list header says "Country" — so it must show the country, not the
        // row id that used to sit under it.
        var countryById = await _db.Countries.ToDictionaryAsync(c => c.Id, c => new { c.NameEn, c.NameAr, c.CurrencyCode });
        Periods = periods.Select(p =>
        {
            var (count, total) = linesByPeriod.GetValueOrDefault(p.Id, (0, 0m));
            var country = countryById.GetValueOrDefault(p.CountryId);
            var countryName = country is null
                ? _localizer.NotSpecified()
                : (IsArabic ? country.NameAr : country.NameEn);
            return new PeriodRow(p.Id, countryName, country?.CurrencyCode ?? string.Empty,
                p.PeriodStart, p.PeriodEnd, p.Status, count, total);
        }).ToList();
    }
}
