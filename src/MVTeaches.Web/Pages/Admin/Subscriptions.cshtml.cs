using System.Globalization;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using MVTeaches.Application.Ledger;
using MVTeaches.Application.Subscriptions;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Common;
using MVTeaches.Domain.People;
using MVTeaches.Domain.Subscriptions;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using MVTeaches.Web.Resources;
using NodaTime;

namespace MVTeaches.Web.Pages.Admin;

/// <summary>
/// §23 (pricing plans) + §19.2/§20.2 (subscriptions + the entitlement ledger) —
/// the missing link that closes "purchase and payment" end to end: a pricing
/// plan gets created here, a subscription gets purchased against it here (or
/// admin-granted for free per D-13), and the existing, already-tested
/// /Admin/Payments screen is what confirms the payment that activates it.
/// </summary>
[Authorize(Roles = RoleNames.Admin + "," + RoleNames.SystemAdmin)]
public class SubscriptionsModel : PageModel
{
    private readonly MvTeachesDbContext _db;
    private readonly ISubscriptionService _subscriptions;
    private readonly IEntitlementBalanceQuery _balances;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public SubscriptionsModel(MvTeachesDbContext db, ISubscriptionService subscriptions,
        IEntitlementBalanceQuery balances, UserManager<ApplicationUser> userManager,
        IStringLocalizer<SharedResource> localizer)
    {
        _db = db;
        _subscriptions = subscriptions;
        _balances = balances;
        _userManager = userManager;
        _localizer = localizer;
    }

    public record PlanRow(long Id, string CountryName, string CourseName, string? LevelCode, SessionType SessionType,
        int SessionsCount, int MinutesTotal, decimal Amount, string Currency, int ValidityDays);

    public record SubscriptionRow(long Id, long StudentId, string StudentName, string CourseName, string LevelCode,
        decimal Price, string Currency, SubscriptionStatus Status, SubscriptionOrigin Origin, int BalanceMinutes,
        LocalDate ExpiresOn);

    public IReadOnlyList<PlanRow> Plans { get; set; } = Array.Empty<PlanRow>();
    public IReadOnlyList<SubscriptionRow> RecentSubscriptions { get; set; } = Array.Empty<SubscriptionRow>();
    // Fully qualified to avoid ambiguity with the sibling MVTeaches.Web.Pages.Student namespace.
    public IReadOnlyList<MVTeaches.Domain.People.Student> Students { get; set; } = Array.Empty<MVTeaches.Domain.People.Student>();
    public IReadOnlyList<Country> Countries { get; set; } = Array.Empty<Country>();
    public IReadOnlyList<Course> Courses { get; set; } = Array.Empty<Course>();
    public IReadOnlyList<Level> Levels { get; set; } = Array.Empty<Level>();

    /// <summary>Currency codes taken from the configured countries, home market
    /// first — so the admin picks one instead of typing three letters.</summary>
    public IReadOnlyList<string> Currencies { get; set; } = Array.Empty<string>();

    /// <summary>Set when the admin arrived from one student's row; narrows the
    /// subscription list to that student and says whose it is.</summary>
    [BindProperty(SupportsGet = true, Name = "studentId")]
    public long? FilterStudentId { get; set; }

    public string? FilterStudentName { get; set; }

    [BindProperty]
    public CreatePlanInput NewPlan { get; set; } = new();

    [BindProperty]
    public PurchaseInput Purchase { get; set; } = new();

    [BindProperty]
    public GrantInput Grant { get; set; } = new();

    public string? StatusMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public bool IsArabic => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("ar", StringComparison.OrdinalIgnoreCase);

    public string DisplayCountry(Country country) => IsArabic ? country.NameAr : country.NameEn;
    public string DisplayCourse(Course course) => IsArabic ? course.NameAr : course.NameEn;
    public string DisplayLevel(Level level) => IsArabic ? level.NameAr : level.NameEn;

    // Every picked id below is nullable on purpose: with a non-nullable long an
    // untouched <select> posts "", ModelState.Clear() drops the binding error,
    // and [Required] then passes on the defaulted 0 — the service ends up called
    // with student id 0. Same fix already documented in Teacher/PublishSlots.
    public class CreatePlanInput
    {
        [Required] public int? CountryId { get; set; }
        [Required] public long? CourseId { get; set; }
        public int? LevelId { get; set; }
        [Required] public SessionType SessionType { get; set; }
        [Required, Range(1, int.MaxValue)] public int SessionsCount { get; set; }
        [Required, Range(1, int.MaxValue)] public int MinutesTotal { get; set; }
        [Required, Range(0, double.MaxValue)] public decimal Amount { get; set; }
        [Required, StringLength(3, MinimumLength = 3)] public string Currency { get; set; } = string.Empty;
        [Required, Range(1, int.MaxValue)] public int ValidityDays { get; set; }
    }

    public class PurchaseInput
    {
        [Required] public long? StudentId { get; set; }
        [Required] public long? PricingPlanId { get; set; }
        [Required] public SubscriptionOrigin Origin { get; set; } = SubscriptionOrigin.GuardianPurchase;
    }

    public class GrantInput
    {
        [Required] public long? StudentId { get; set; }
        [Required] public int? CountryId { get; set; }
        [Required] public long? CourseId { get; set; }
        [Required] public int? LevelId { get; set; }
        [Required] public SessionType SessionType { get; set; }
        [Required, Range(1, int.MaxValue)] public int SessionsCount { get; set; }
        [Required, Range(1, int.MaxValue)] public int MinutesTotal { get; set; }
        [Required, Range(1, int.MaxValue)] public int ValidityDays { get; set; }
        [Required] public string Reason { get; set; } = string.Empty;
    }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostCreatePlanAsync()
    {
        ModelState.Clear();
        if (!TryValidateModel(NewPlan, nameof(NewPlan)))
        {
            await LoadAsync();
            return Page();
        }

        var actingUserId = GetCurrentUserId();
        var today = LocalDate.FromDateTime(DateTime.UtcNow);
        await _subscriptions.CreatePricingPlanAsync(NewPlan.CountryId!.Value, NewPlan.CourseId!.Value, NewPlan.LevelId, null,
            NewPlan.SessionType, NewPlan.SessionsCount, NewPlan.MinutesTotal, new Money(NewPlan.Amount, NewPlan.Currency),
            NewPlan.ValidityDays, today, actingUserId, HttpContext.RequestAborted);

        StatusMessage = _localizer["Pricing plan created."];
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostPurchaseAsync()
    {
        ModelState.Clear();
        if (!TryValidateModel(Purchase, nameof(Purchase)))
        {
            await LoadAsync();
            return Page();
        }

        var actingUserId = GetCurrentUserId();
        var result = await _subscriptions.PurchaseFromPlanAsync(Purchase.StudentId!.Value, Purchase.PricingPlanId!.Value,
            actingUserId, Purchase.Origin, isAdminInitiated: true, HttpContext.RequestAborted);

        // Owner decision 2026-08-30 rule 4: "Manual payments must use the same
        // level/session-type restrictions" — surfaced here as a clear refusal
        // rather than a generic error, since this is an admin working the
        // exception path by hand and needs to know exactly why it was refused.
        ErrorMessage = result.Outcome switch
        {
            PurchaseFromPlanOutcome.Purchased => null,
            PurchaseFromPlanOutcome.PlanNotFound => _localizer["Pricing plan not found."].Value,
            PurchaseFromPlanOutcome.PlanNotPublishedForAnyLevel => _localizer["This plan has no specific level (or is inactive) and cannot be purchased - every published package must be tied to exactly one level."].Value,
            PurchaseFromPlanOutcome.StudentHasNoAssignedLevel => _localizer["This student has no current assigned level yet - a level must be assigned before any package can be purchased."].Value,
            PurchaseFromPlanOutcome.LevelMismatch => _localizer["This plan's level does not match the student's current assigned level."].Value,
            _ => _localizer["Could not record this purchase."].Value,
        };
        if (result.Outcome == PurchaseFromPlanOutcome.Purchased)
        {
            StatusMessage = _localizer["Subscription purchase created as draft.",
                result.SubscriptionId!.Value, result.Price!];
        }
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostGrantAsync()
    {
        ModelState.Clear();
        if (!TryValidateModel(Grant, nameof(Grant)))
        {
            await LoadAsync();
            return Page();
        }

        var actingUserId = GetCurrentUserId();
        var result = await _subscriptions.GrantAdminSubscriptionAsync(Grant.StudentId!.Value, Grant.CountryId!.Value,
            Grant.CourseId!.Value, Grant.LevelId!.Value, Grant.SessionType, Grant.SessionsCount, Grant.MinutesTotal,
            Grant.ValidityDays, actingUserId, Grant.Reason, HttpContext.RequestAborted);

        StatusMessage = _localizer["Subscription granted and activated immediately.", result.SubscriptionId];
        await LoadAsync();
        return Page();
    }

    private long GetCurrentUserId() => long.Parse(_userManager.GetUserId(User)!);

    private async Task LoadAsync()
    {
        Countries = await _db.Countries.Where(c => c.IsActive).OrderBy(c => c.NameEn).ToListAsync();
        Courses = await _db.Courses.Where(c => c.IsActive).OrderBy(c => c.NameEn).ToListAsync();
        Levels = await _db.Levels.Where(l => l.IsActive).OrderBy(l => l.SortOrder).ToListAsync();
        Students = await _db.Students.OrderBy(s => s.FullName).Take(200).ToListAsync();

        // Home market first (countries are seeded in that order), so the default
        // selection is the currency this centre bills in without any code being
        // written into the page.
        Currencies = (await _db.Countries.Where(c => c.IsActive)
                .OrderBy(c => c.Id)
                .Select(c => c.CurrencyCode)
                .ToListAsync())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var countryByI = Countries.ToDictionary(c => c.Id, DisplayCountry);
        var courseByI = Courses.ToDictionary(c => c.Id, DisplayCourse);
        var levelByI = Levels.ToDictionary(l => l.Id, l => l.Code);
        var studentByI = Students.ToDictionary(s => s.Id, s => s.FullName);

        var plans = await _db.PricingPlans.Where(p => p.IsActive).OrderByDescending(p => p.Id).ToListAsync();
        Plans = plans.Select(p => new PlanRow(p.Id, countryByI.GetValueOrDefault(p.CountryId, "?"),
            courseByI.GetValueOrDefault(p.CourseId, "?"), p.LevelId.HasValue ? levelByI.GetValueOrDefault(p.LevelId.Value) : null,
            p.SessionType, p.SessionsCount, p.MinutesTotal, p.Amount.Amount, p.Amount.Currency, p.ValidityDays)).ToList();

        var subs = await _db.Subscriptions
            .Where(s => FilterStudentId == null || s.StudentId == FilterStudentId)
            .OrderByDescending(s => s.Id)
            .Take(50)
            .ToListAsync();

        // Names for exactly the rows being printed — the 200-row picker window
        // is not a reliable name source once a school has more students than it.
        var neededStudentIds = subs.Select(s => s.StudentId)
            .Concat(FilterStudentId is null ? Array.Empty<long>() : new[] { FilterStudentId.Value })
            .Distinct()
            .ToList();
        var namesForRows = await _db.Students
            .Where(s => neededStudentIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.FullName);
        FilterStudentName = FilterStudentId is null ? null : namesForRows.GetValueOrDefault(FilterStudentId.Value);

        var rows = new List<SubscriptionRow>();
        foreach (var s in subs)
        {
            var balance = await _balances.GetSubscriptionBalanceAsync(s.Id, HttpContext.RequestAborted);
            rows.Add(new SubscriptionRow(s.Id, s.StudentId,
                namesForRows.GetValueOrDefault(s.StudentId) ?? studentByI.GetValueOrDefault(s.StudentId, string.Empty),
                courseByI.GetValueOrDefault(s.CourseId, "?"), levelByI.GetValueOrDefault(s.LevelId, "?"),
                s.Price.Amount, s.Price.Currency, s.Status, s.Origin, balance, s.ExpiresOn));
        }

        RecentSubscriptions = rows;
    }
}
