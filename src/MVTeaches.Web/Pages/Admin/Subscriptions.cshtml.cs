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
using MVTeaches.Web.Identity;
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
[Authorize(Policy = PermissionKeys.SubscriptionsView)]
public class SubscriptionsModel : PageModel
{
    private readonly MvTeachesDbContext _db;
    private readonly ISubscriptionService _subscriptions;
    private readonly IEntitlementBalanceQuery _balances;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly IAuthorizationService _authorizationService;

    public SubscriptionsModel(MvTeachesDbContext db, ISubscriptionService subscriptions,
        IEntitlementBalanceQuery balances, UserManager<ApplicationUser> userManager,
        IStringLocalizer<SharedResource> localizer, IAuthorizationService authorizationService)
    {
        _db = db;
        _subscriptions = subscriptions;
        _balances = balances;
        _userManager = userManager;
        _localizer = localizer;
        _authorizationService = authorizationService;
    }

    public record PlanRow(long Id, string CountryName, string CourseName, int? LevelId, string? LevelCode,
        SessionType SessionType, int SessionsCount, int MinutesTotal, decimal Amount, string Currency, int ValidityDays);

    public record SubscriptionRow(long Id, long StudentId, string StudentName, string CourseName, string LevelCode,
        decimal Price, string Currency, SubscriptionStatus Status, SubscriptionOrigin Origin, int BalanceMinutes,
        LocalDate ExpiresOn);

    /// <summary>A student as this screen needs them: the name to pick by, and
    /// the level that decides which packages are even legal for them. The plan
    /// picker filters on that level, so a package for the wrong level can no
    /// longer be chosen and then refused after the fact.</summary>
    public record StudentPickRow(long Id, string FullName, int? CurrentLevelId, string? CurrentLevelCode);

    public IReadOnlyList<StudentPickRow> StudentPicks { get; set; } = Array.Empty<StudentPickRow>();

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
        [Required(ErrorMessage = "Choose a country.")] public int? CountryId { get; set; }
        [Required(ErrorMessage = "Choose a course.")] public long? CourseId { get; set; }
        public int? LevelId { get; set; }
        [Required] public SessionType SessionType { get; set; }
        [Required, Range(1, int.MaxValue, ErrorMessage = "Enter how many sessions the package includes.")] public int SessionsCount { get; set; }

        /// <summary>The admin thinks in sessions and in how long one session
        /// runs — never in a bag of total minutes. The total the ledger
        /// actually stores is the product of the two, computed below, so
        /// nothing about what is saved changes; only what has to be typed.
        /// (D-95: the scheduled duration is the financial truth.)</summary>
        [Required, Range(1, 600, ErrorMessage = "Enter how long one session runs, in minutes.")] public int MinutesPerSession { get; set; }

        public int MinutesTotal => SessionsCount * MinutesPerSession;
        [Required, Range(0, double.MaxValue, ErrorMessage = "Enter the price.")] public decimal Amount { get; set; }
        [Required(ErrorMessage = "Choose a currency."), StringLength(3, MinimumLength = 3)] public string Currency { get; set; } = string.Empty;
        [Required, Range(1, int.MaxValue, ErrorMessage = "Enter how many days the package stays valid.")] public int ValidityDays { get; set; }
    }

    public class PurchaseInput
    {
        [Required(ErrorMessage = "Choose a student.")] public long? StudentId { get; set; }
        [Required(ErrorMessage = "Choose a pricing plan.")] public long? PricingPlanId { get; set; }
        [Required] public SubscriptionOrigin Origin { get; set; } = SubscriptionOrigin.GuardianPurchase;
    }

    /// <summary>Granting a package for free is now expressed the way the
    /// admin actually thinks about it: pick the student, see their level, pick
    /// one of the packages already published for that level, say why. Every
    /// value the service needs — country, course, level, type, sessions,
    /// minutes, validity — is read off that published package, so the admin
    /// never types a minute count. The manual fields are still here for the
    /// rare grant that matches no published package; they are only read when
    /// no package was chosen, and the service call is identical either way.</summary>
    public class GrantInput
    {
        [Required(ErrorMessage = "Choose a student.")] public long? StudentId { get; set; }

        public long? PricingPlanId { get; set; }

        public int? CountryId { get; set; }
        public long? CourseId { get; set; }
        public int? LevelId { get; set; }
        public SessionType SessionType { get; set; }
        public int SessionsCount { get; set; }
        public int MinutesTotal { get; set; }
        public int ValidityDays { get; set; }

        [Required(ErrorMessage = "Write the reason for this decision.")] public string Reason { get; set; } = string.Empty;
    }

    public async Task OnGetAsync()
    {
        await LoadAsync();
        if (FilterStudentId is not null)
        {
            Purchase.StudentId ??= FilterStudentId;
            Grant.StudentId ??= FilterStudentId;
        }
    }

    public async Task<IActionResult> OnPostCreatePlanAsync()
    {
        if (await this.RequirePermissionAsync(_authorizationService, PermissionKeys.SubscriptionsManage) is { } deny)
        {
            return deny;
        }

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
        if (await this.RequirePermissionAsync(_authorizationService, PermissionKeys.SubscriptionsManage) is { } deny)
        {
            return deny;
        }

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
            // Owner decision 2026-09-04 (duplicate-purchase guard): the same
            // refusal an admin gets, naming the subscription that is already
            // in the way so it can be checked rather than guessed at. An
            // admin who genuinely must add a package on top still has the
            // separate, reason-carrying "give a student a package" grant path.
            PurchaseFromPlanOutcome.DraftAlreadyAwaitingPayment =>
                _localizer["This student already has a draft request for this exact plan (subscription #{0}) still awaiting payment - record the payment against that one instead of creating a second request.",
                    result.SubscriptionId!].Value,
            PurchaseFromPlanOutcome.ActivePackageStillHasBalance =>
                _localizer["This student already holds a live subscription for this exact plan (subscription #{0}) with hours still remaining - the same package cannot be sold again until those hours are used.",
                    result.SubscriptionId!].Value,
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
        if (await this.RequirePermissionAsync(_authorizationService, PermissionKeys.SubscriptionsManage) is { } deny)
        {
            return deny;
        }

        ModelState.Clear();
        if (!TryValidateModel(Grant, nameof(Grant)))
        {
            await LoadAsync();
            return Page();
        }

        int countryId;
        long courseId;
        int levelId;
        SessionType sessionType;
        int sessionsCount;
        int minutesTotal;
        int validityDays;

        if (Grant.PricingPlanId is not null)
        {
            // Read the shape of the gift off the published package, exactly as
            // a paid purchase of it would have been shaped. No new rule: the
            // same seven values the manual path already passed.
            var plan = await _db.PricingPlans.FirstOrDefaultAsync(p => p.Id == Grant.PricingPlanId.Value);
            if (plan is null || plan.LevelId is null)
            {
                ErrorMessage = _localizer["Pricing plan not found."].Value;
                await LoadAsync();
                return Page();
            }

            countryId = plan.CountryId;
            courseId = plan.CourseId;
            levelId = plan.LevelId.Value;
            sessionType = plan.SessionType;
            sessionsCount = plan.SessionsCount;
            minutesTotal = plan.MinutesTotal;
            validityDays = plan.ValidityDays;
        }
        else if (Grant.CountryId is not null && Grant.CourseId is not null && Grant.LevelId is not null
                 && Grant.SessionsCount > 0 && Grant.MinutesTotal > 0 && Grant.ValidityDays > 0)
        {
            countryId = Grant.CountryId.Value;
            courseId = Grant.CourseId.Value;
            levelId = Grant.LevelId.Value;
            sessionType = Grant.SessionType;
            sessionsCount = Grant.SessionsCount;
            minutesTotal = Grant.MinutesTotal;
            validityDays = Grant.ValidityDays;
        }
        else
        {
            ErrorMessage = _localizer["Choose one of the published packages, or fill in every field under the manual option."].Value;
            await LoadAsync();
            return Page();
        }

        var actingUserId = GetCurrentUserId();
        var result = await _subscriptions.GrantAdminSubscriptionAsync(Grant.StudentId!.Value, countryId,
            courseId, levelId, sessionType, sessionsCount, minutesTotal,
            validityDays, actingUserId, Grant.Reason, HttpContext.RequestAborted);

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
            courseByI.GetValueOrDefault(p.CourseId, "?"), p.LevelId,
            p.LevelId.HasValue ? levelByI.GetValueOrDefault(p.LevelId.Value) : null,
            p.SessionType, p.SessionsCount, p.MinutesTotal, p.Amount.Amount, p.Amount.Currency, p.ValidityDays)).ToList();

        var currentLevels = await _db.StudentLevels.Where(l => l.IsCurrent).ToListAsync();
        var currentLevelByStudent = currentLevels.ToDictionary(l => l.StudentId, l => l.LevelId);
        StudentPicks = Students.Select(st =>
        {
            var levelId = currentLevelByStudent.TryGetValue(st.Id, out var found) ? found : (int?)null;
            return new StudentPickRow(st.Id, st.FullName, levelId,
                levelId is null ? null : levelByI.GetValueOrDefault(levelId.Value));
        }).ToList();

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
