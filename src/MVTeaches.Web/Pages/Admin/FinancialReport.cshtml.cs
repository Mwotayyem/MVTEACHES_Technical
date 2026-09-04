using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using MVTeaches.Application.Payroll;
using MVTeaches.Application.Reports;
using MVTeaches.Domain.Common;
using MVTeaches.Domain.Finance;
using MVTeaches.Domain.Payments;
using MVTeaches.Domain.Payroll;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Domain.Subscriptions;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Web.Display;
using MVTeaches.Web.Identity;
using MVTeaches.Web.Resources;
using NodaTime;

namespace MVTeaches.Web.Pages.Admin;

/// <summary>The owner's own stated MVP scope names "basic financial reports"
/// explicitly (see IFinancialReportService's remarks) — the original three
/// numbers here were built to that letter. Owner decision 2026-08-30 rule 9
/// is a later, explicit, dated instruction naming five more figures by name;
/// this page's extension follows that instruction, not a reversal of the
/// original discipline (see IFinancialReportService's own updated remarks).
///
/// Owner decision 2026-09-04 (Payroll-simplification, Review Required —
/// this reads TeacherRate/ClassSession but writes nothing payroll-related):
/// the owner does not want the open-period/declare/verify/approve/pay
/// workflow to be part of daily admin work in this MVP stage — disbursement
/// happens by hand, outside the system. What the owner still needs day to
/// day is a plain number: a teacher actually gave X hours, the rate is Y,
/// so Z is owed. <see cref="TeacherDues"/> is exactly and only that — a
/// read-only projection computed fresh on every page load. It is entirely
/// independent of PayrollPeriod/PayrollLine/SessionDelivery: no period is
/// ever opened, no delivery is ever declared or verified, nothing here
/// writes a single row. See LoadTeacherDuesAsync for exactly what counts as
/// "actually delivered" and how the rate is found.
/// </summary>
[Authorize(Roles = RoleNames.Admin + "," + RoleNames.SystemAdmin)]
[Authorize(Policy = PermissionKeys.FinancialReportView)]
public class FinancialReportModel : PageModel
{
    private readonly IFinancialReportService _reports;
    private readonly IOperatingExpenseService _expenses;
    private readonly IPayrollRateResolver _rateResolver;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IAuthorizationService _authorizationService;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly MVTeaches.Infrastructure.Persistence.MvTeachesDbContext _db;

    public FinancialReportModel(IFinancialReportService reports, IOperatingExpenseService expenses,
        IPayrollRateResolver rateResolver, UserManager<ApplicationUser> userManager,
        IAuthorizationService authorizationService, IStringLocalizer<SharedResource> localizer,
        MVTeaches.Infrastructure.Persistence.MvTeachesDbContext db)
    {
        _reports = reports;
        _expenses = expenses;
        _rateResolver = rateResolver;
        _userManager = userManager;
        _authorizationService = authorizationService;
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

    /// <summary>One teacher's read-only dues for the report period — see the
    /// class remarks and <see cref="LoadTeacherDuesAsync"/>. <see cref="SessionsMissingRate"/>
    /// counts delivered sessions that could not be priced at all (no
    /// TeacherRate covers that teacher/course/level/age-group combination
    /// yet) — their minutes are still included in <see cref="DeliveredMinutes"/>
    /// so the hour count stays honest, but they contribute nothing to
    /// <see cref="DueByCurrency"/>, which the page must flag rather than
    /// silently understate.</summary>
    public record TeacherDuesRow(long TeacherId, string TeacherName, int SessionCount, int DeliveredMinutes,
        IReadOnlyList<CurrencyAmount> DueByCurrency, int SessionsMissingRate);

    public IReadOnlyList<TeacherDuesRow> TeacherDues { get; set; } = Array.Empty<TeacherDuesRow>();

    /// <summary>Owner decision 2026-09-04 — what students still owe the centre,
    /// one row per currency. Read-only in the strictest sense: it writes
    /// nothing, and it invents nothing either, because
    /// <see cref="MoneyStanding.ComputeByCurrency"/> already computes exactly
    /// this and is already what /Admin/Students and the student profile show.
    /// Reusing it is the whole point — a second, independently written total
    /// would be a second opinion about how much a family owes, and the two
    /// would eventually disagree in front of a parent.
    /// <para>Deliberately NOT filtered to the report's date range, unlike every
    /// other figure on this page. Outstanding money is a standing, not a flow:
    /// a package billed in March and still unpaid in May is owed in May, and
    /// scoping it to the period would report zero for exactly the debts that
    /// have gone unpaid longest. The page says so where it is shown.</para>
    /// <para><see cref="StudentsOwingCount"/> counts distinct students with a
    /// shortfall in ANY currency, so a family owing in two currencies is one
    /// student, not two.</para></summary>
    public record StudentDuesRow(string Currency, decimal Billed, decimal Paid, decimal Outstanding);

    public IReadOnlyList<StudentDuesRow> StudentDues { get; set; } = Array.Empty<StudentDuesRow>();

    public int StudentsOwingCount { get; set; }

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
        if (await this.RequirePermissionAsync(_authorizationService, PermissionKeys.FinancialReportManage) is { } deny)
        {
            return deny;
        }

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
        await LoadTeacherDuesAsync(periodStart, periodEnd, HttpContext.RequestAborted);
        await LoadStudentDuesAsync(HttpContext.RequestAborted);

        var periodLengthDays = Period.Between(periodStart, periodEnd.PlusDays(1), PeriodUnits.Days).Days;
        var previousPeriodEnd = periodStart.PlusDays(-1);
        var previousPeriodStart = previousPeriodEnd.PlusDays(-(periodLengthDays - 1));
        PreviousPeriodReport = await _reports.GenerateAsync(previousPeriodStart, previousPeriodEnd, HttpContext.RequestAborted);
    }

    /// <summary>Owner decision 2026-09-04 — see <see cref="StudentDuesRow"/>.
    /// Loads the open subscriptions and their confirmed payments and hands both
    /// to the shared MoneyStanding helper; no arithmetic happens here and
    /// nothing is written. Takes no period parameters on purpose — see the
    /// record's remarks for why a debt is not a period figure.</summary>
    private async Task LoadStudentDuesAsync(CancellationToken cancellationToken)
    {
        // Only Draft/Active subscriptions can be owed on at all; loading just
        // those keeps this off the full subscription history. MoneyStanding
        // filters to the same two statuses itself, so this is a narrowing of
        // the query, never a second definition of "open".
        var openSubscriptions = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
            _db.Subscriptions.Where(s => s.Status == SubscriptionStatus.Draft || s.Status == SubscriptionStatus.Active),
            cancellationToken);
        if (openSubscriptions.Count == 0)
        {
            StudentDues = Array.Empty<StudentDuesRow>();
            StudentsOwingCount = 0;
            return;
        }

        var openSubscriptionIds = openSubscriptions.Select(s => s.Id).ToHashSet();
        var relevantPayments = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
            _db.Payments.Where(p => p.Status == PaymentStatus.Confirmed
                                    && p.SubscriptionId != null
                                    && openSubscriptionIds.Contains(p.SubscriptionId.Value)),
            cancellationToken);

        StudentDues = MoneyStanding.ComputeByCurrency(openSubscriptions, relevantPayments)
            .OrderByDescending(pair => pair.Value.Outstanding)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new StudentDuesRow(pair.Key, pair.Value.Billed, pair.Value.Paid, pair.Value.Outstanding))
            .ToList();

        // Per student, across all their own open packages — the same helper
        // again, so "this student owes something" means the same thing here as
        // it does on their own row in /Admin/Students.
        var paymentsByStudent = relevantPayments.GroupBy(p => p.StudentId)
            .ToDictionary(g => g.Key, g => (IReadOnlyCollection<Payment>)g.ToList());
        StudentsOwingCount = openSubscriptions
            .GroupBy(s => s.StudentId)
            .Count(g => MoneyStanding.ComputeByCurrency(g.ToList(),
                    paymentsByStudent.GetValueOrDefault(g.Key, Array.Empty<Payment>()))
                .Any(pair => pair.Value.Outstanding > 0m));
    }

    /// <summary>Owner decision 2026-09-04 — see the class remarks. Pure read:
    /// no PayrollPeriod, PayrollLine, or SessionDelivery row is read, written,
    /// or required to exist.
    ///
    /// "Actually delivered" = <see cref="ClassSessionStatus.Completed"/> —
    /// the exact, already-existing signal SessionFinalizationService's own
    /// attendance sweep sets once a session's end time has passed without it
    /// having been cancelled (see ClassSession.MarkCompleted's remarks). A
    /// session still Scheduled (hasn't happened yet), Cancelled, or
    /// NotDelivered contributes zero minutes and zero dues — this is
    /// deliberately narrower than the report's own ScheduledTeachingMinutes
    /// figure above (which counts every non-cancelled session, past or
    /// future) precisely because dues must never count a lesson that has not
    /// actually happened yet.
    ///
    /// The rate is resolved LIVE for each session's own (course, level, age
    /// group) via the same IPayrollRateResolver/most-specific-wins rule
    /// SessionDelivery.Verify uses (§9.2/D-27) — never a snapshotted
    /// SessionDelivery.RateAmount, since this reading must work for a centre
    /// that never opens the Declare/Verify workflow at all. The PerHour/
    /// PerSession amount formula and its 3-decimal rounding are copied
    /// exactly from SessionDelivery.Verify so the two pipelines can never
    /// silently disagree on what a delivered hour is worth.</summary>
    private async Task LoadTeacherDuesAsync(LocalDate periodStart, LocalDate periodEnd, CancellationToken cancellationToken)
    {
        var startInstant = periodStart.AtMidnight().InUtc().ToInstant();
        var endInstant = periodEnd.PlusDays(1).AtMidnight().InUtc().ToInstant();

        var deliveredSessions = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
            _db.ClassSessions
                .Where(s => s.Status == ClassSessionStatus.Completed
                            && s.StartsAtUtc >= startInstant && s.StartsAtUtc < endInstant)
                .Select(s => new
                {
                    s.TeacherId,
                    s.CourseId,
                    s.LevelId,
                    s.AgeGroupId,
                    s.DurationMinutes,
                    s.StartsAtUtc,
                }),
            cancellationToken);

        if (deliveredSessions.Count == 0)
        {
            TeacherDues = Array.Empty<TeacherDuesRow>();
            return;
        }

        var teacherIds = deliveredSessions.Select(s => s.TeacherId).Distinct().ToList();
        var teacherNames = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToDictionaryAsync(
            _db.Teachers.Where(t => teacherIds.Contains(t.Id)), t => t.Id, t => t.FullName, cancellationToken);

        var rows = new List<TeacherDuesRow>();
        foreach (var teacherId in teacherIds)
        {
            var sessions = deliveredSessions.Where(s => s.TeacherId == teacherId).ToList();
            var dueByCurrency = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            var missingRate = 0;

            foreach (var session in sessions)
            {
                var onDate = session.StartsAtUtc.InUtc().Date;
                var resolved = await _rateResolver.ResolveAsync(
                    teacherId, session.CourseId, session.LevelId, session.AgeGroupId, onDate, cancellationToken);
                if (resolved is null)
                {
                    missingRate++;
                    continue;
                }

                // Same formula as SessionDelivery.Verify — a PerHour rate
                // scales with the session's own duration, a PerSession rate
                // is a flat amount regardless of duration.
                var amount = resolved.Unit == RateUnit.PerSession
                    ? Math.Round(resolved.Rate.Amount, 3)
                    : Math.Round(session.DurationMinutes / 60m * resolved.Rate.Amount, 3);

                dueByCurrency[resolved.Rate.Currency] = dueByCurrency.TryGetValue(resolved.Rate.Currency, out var existing)
                    ? existing + amount
                    : amount;
            }

            rows.Add(new TeacherDuesRow(
                teacherId,
                teacherNames.GetValueOrDefault(teacherId, $"#{teacherId}"),
                sessions.Count,
                sessions.Sum(s => s.DurationMinutes),
                dueByCurrency.Select(kv => new CurrencyAmount(kv.Key, kv.Value)).OrderBy(c => c.Currency).ToList(),
                missingRate));
        }

        TeacherDues = rows.OrderByDescending(r => r.DeliveredMinutes).ToList();
    }
}
