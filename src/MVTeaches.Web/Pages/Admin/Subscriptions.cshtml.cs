using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Ledger;
using MVTeaches.Application.Subscriptions;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Common;
using MVTeaches.Domain.People;
using MVTeaches.Domain.Subscriptions;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
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

    public SubscriptionsModel(MvTeachesDbContext db, ISubscriptionService subscriptions,
        IEntitlementBalanceQuery balances, UserManager<ApplicationUser> userManager)
    {
        _db = db;
        _subscriptions = subscriptions;
        _balances = balances;
        _userManager = userManager;
    }

    public record PlanRow(long Id, string CountryCode, string CourseName, string? LevelCode, SessionType SessionType,
        int SessionsCount, int MinutesTotal, decimal Amount, string Currency, int ValidityDays);

    public record SubscriptionRow(long Id, string StudentName, string CourseName, string LevelCode,
        decimal Price, string Currency, SubscriptionStatus Status, SubscriptionOrigin Origin, int BalanceMinutes,
        LocalDate ExpiresOn);

    public IReadOnlyList<PlanRow> Plans { get; set; } = Array.Empty<PlanRow>();
    public IReadOnlyList<SubscriptionRow> RecentSubscriptions { get; set; } = Array.Empty<SubscriptionRow>();
    // Fully qualified to avoid ambiguity with the sibling MVTeaches.Web.Pages.Student namespace.
    public IReadOnlyList<MVTeaches.Domain.People.Student> Students { get; set; } = Array.Empty<MVTeaches.Domain.People.Student>();
    public IReadOnlyList<Country> Countries { get; set; } = Array.Empty<Country>();
    public IReadOnlyList<Course> Courses { get; set; } = Array.Empty<Course>();
    public IReadOnlyList<Level> Levels { get; set; } = Array.Empty<Level>();

    [BindProperty]
    public CreatePlanInput NewPlan { get; set; } = new();

    [BindProperty]
    public PurchaseInput Purchase { get; set; } = new();

    [BindProperty]
    public GrantInput Grant { get; set; } = new();

    public string? StatusMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public class CreatePlanInput
    {
        [Required] public int CountryId { get; set; }
        [Required] public long CourseId { get; set; }
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
        [Required] public long StudentId { get; set; }
        [Required] public long PricingPlanId { get; set; }
        [Required] public int LevelId { get; set; }
        [Required] public SubscriptionOrigin Origin { get; set; } = SubscriptionOrigin.GuardianPurchase;
    }

    public class GrantInput
    {
        [Required] public long StudentId { get; set; }
        [Required] public int CountryId { get; set; }
        [Required] public long CourseId { get; set; }
        [Required] public int LevelId { get; set; }
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
        await _subscriptions.CreatePricingPlanAsync(NewPlan.CountryId, NewPlan.CourseId, NewPlan.LevelId, null,
            NewPlan.SessionType, NewPlan.SessionsCount, NewPlan.MinutesTotal, new Money(NewPlan.Amount, NewPlan.Currency),
            NewPlan.ValidityDays, today, actingUserId, HttpContext.RequestAborted);

        StatusMessage = "Pricing plan created.";
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
        var result = await _subscriptions.PurchaseFromPlanAsync(Purchase.StudentId, Purchase.PricingPlanId,
            Purchase.LevelId, Purchase.Origin, actingUserId, HttpContext.RequestAborted);

        StatusMessage = $"Subscription #{result.SubscriptionId} created as Draft ({result.Price}) — " +
            "record and confirm the matching payment on /Admin/Payments to activate it.";
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
        var result = await _subscriptions.GrantAdminSubscriptionAsync(Grant.StudentId, Grant.CountryId, Grant.CourseId,
            Grant.LevelId, Grant.SessionsCount, Grant.MinutesTotal, Grant.ValidityDays, actingUserId, Grant.Reason,
            HttpContext.RequestAborted);

        StatusMessage = $"Subscription #{result.SubscriptionId} granted and activated immediately (D-13, no payment).";
        await LoadAsync();
        return Page();
    }

    private long GetCurrentUserId() => long.Parse(_userManager.GetUserId(User)!);

    private async Task LoadAsync()
    {
        Countries = await _db.Countries.Where(c => c.IsActive).OrderBy(c => c.NameEn).ToListAsync();
        Courses = await _db.Courses.Where(c => c.IsActive).OrderBy(c => c.NameEn).ToListAsync();
        Levels = await _db.Levels.Where(l => l.IsActive).OrderBy(l => l.SortOrder).ToListAsync();
        Students = await _db.Students.OrderByDescending(s => s.Id).Take(200).ToListAsync();

        var countryByI = Countries.ToDictionary(c => c.Id, c => c.Code);
        var courseByI = Courses.ToDictionary(c => c.Id, c => c.NameEn);
        var levelByI = Levels.ToDictionary(l => l.Id, l => l.Code);
        var studentByI = Students.ToDictionary(s => s.Id, s => s.FullName);

        var plans = await _db.PricingPlans.Where(p => p.IsActive).OrderByDescending(p => p.Id).ToListAsync();
        Plans = plans.Select(p => new PlanRow(p.Id, countryByI.GetValueOrDefault(p.CountryId, "?"),
            courseByI.GetValueOrDefault(p.CourseId, "?"), p.LevelId.HasValue ? levelByI.GetValueOrDefault(p.LevelId.Value) : null,
            p.SessionType, p.SessionsCount, p.MinutesTotal, p.Amount.Amount, p.Amount.Currency, p.ValidityDays)).ToList();

        var subs = await _db.Subscriptions.OrderByDescending(s => s.Id).Take(50).ToListAsync();
        var rows = new List<SubscriptionRow>();
        foreach (var s in subs)
        {
            var balance = await _balances.GetSubscriptionBalanceAsync(s.Id, HttpContext.RequestAborted);
            rows.Add(new SubscriptionRow(s.Id, studentByI.GetValueOrDefault(s.StudentId, $"#{s.StudentId}"),
                courseByI.GetValueOrDefault(s.CourseId, "?"), levelByI.GetValueOrDefault(s.LevelId, "?"),
                s.Price.Amount, s.Price.Currency, s.Status, s.Origin, balance, s.ExpiresOn));
        }

        RecentSubscriptions = rows;
    }
}
