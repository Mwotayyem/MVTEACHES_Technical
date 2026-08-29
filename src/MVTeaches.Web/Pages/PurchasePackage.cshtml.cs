using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Placement;
using MVTeaches.Application.Subscriptions;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Subscriptions;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;

namespace MVTeaches.Web.Pages;

/// <summary>
/// Owner decision 2026-08-30 rules 1 and 4. Rule 1: "Until a placement result
/// exists, the student must not purchase a package — show a clear CTA to take
/// the free test instead. After a result, the student sees/books only
/// published packages matching that exact level." Rule 4: "Group package
/// books only Group sessions; Private books only Private." Every restriction
/// here is enforced a second time, server-side, by
/// ISubscriptionService.PurchaseFromPlanAsync itself (PlanNotPublishedForAnyLevel/
/// StudentHasNoAssignedLevel/LevelMismatch/Unauthorized) — this page hiding an
/// ineligible plan is a convenience, never the actual guard. Shared by Student
/// and Guardian accounts exactly like /PlacementTest, for the same reason:
/// the service, not this page, is the authority on "acting user must be the
/// student themself or an active guardian."
///
/// Owner decision 2026-08-30 rule 4 / production honesty: online payment has
/// no provider selected in this environment. This page creates a Draft
/// subscription (D-38's snapshot) and says so plainly — it never claims a
/// payment succeeded, since PurchaseFromPlanAsync itself never activates one.
/// </summary>
[Authorize(Roles = RoleNames.Student + "," + RoleNames.Guardian)]
public class PurchasePackageModel : PageModel
{
    private readonly MvTeachesDbContext _db;
    private readonly ISubscriptionService _subscriptions;
    private readonly IPlacementAttemptService _attempts;
    private readonly UserManager<ApplicationUser> _userManager;

    public PurchasePackageModel(MvTeachesDbContext db, ISubscriptionService subscriptions,
        IPlacementAttemptService attempts, UserManager<ApplicationUser> userManager)
    {
        _db = db;
        _subscriptions = subscriptions;
        _attempts = attempts;
        _userManager = userManager;
    }

    public record ChildOption(long StudentId, string FullName);
    public record PlanRow(long Id, string CourseName, string LevelCode, SessionType SessionType,
        int SessionsCount, int MinutesTotal, decimal Amount, string Currency, int ValidityDays);

    public bool NoProfileLinked { get; set; }
    public bool IsGuardian { get; set; }
    public IReadOnlyList<ChildOption> Children { get; set; } = Array.Empty<ChildOption>();
    public long? SelectedStudentId { get; set; }
    public string? SelectedStudentName { get; set; }

    /// <summary>Rule 1's gate: no completed placement result yet.</summary>
    public bool NeedsPlacementTest { get; set; }
    public string? CurrentLevelCode { get; set; }
    public IReadOnlyList<PlanRow> EligiblePlans { get; set; } = Array.Empty<PlanRow>();

    public string? StatusMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(long? studentId)
    {
        await LoadAsync(studentId);
        return Page();
    }

    public async Task<IActionResult> OnPostPurchaseAsync(long studentId, long pricingPlanId)
    {
        var actingUserId = long.Parse(_userManager.GetUserId(User)!);
        var origin = IsGuardianRole() ? SubscriptionOrigin.GuardianPurchase : SubscriptionOrigin.SelfPurchase;

        var result = await _subscriptions.PurchaseFromPlanAsync(studentId, pricingPlanId, actingUserId, origin,
            isAdminInitiated: false, HttpContext.RequestAborted);

        if (result.Outcome == PurchaseFromPlanOutcome.Purchased)
        {
            // Deliberately does NOT say "payment received" — no online
            // payment provider is configured in this environment, and
            // claiming one succeeded here would be a false confirmation.
            StatusMessage = $"Package requested (subscription #{result.SubscriptionId}, {result.Price}) — " +
                "it stays pending until the centre confirms your payment. Online payment is not yet available; " +
                "please contact the centre to arrange payment.";
        }
        else
        {
            ErrorMessage = result.Outcome switch
            {
                PurchaseFromPlanOutcome.Unauthorized => "Not authorized for this student.",
                PurchaseFromPlanOutcome.PlanNotFound => "Package not found.",
                PurchaseFromPlanOutcome.PlanNotPublishedForAnyLevel => "This package is no longer available.",
                PurchaseFromPlanOutcome.StudentHasNoAssignedLevel => "A placement result is required before purchasing a package.",
                PurchaseFromPlanOutcome.LevelMismatch => "This package no longer matches the student's current level.",
                _ => "Could not record this purchase.",
            };
        }

        await LoadAsync(studentId);
        return Page();
    }

    private bool IsGuardianRole() => User.IsInRole(RoleNames.Guardian);

    private async Task LoadAsync(long? studentId)
    {
        var userId = long.Parse(_userManager.GetUserId(User)!);
        IsGuardian = IsGuardianRole();

        if (IsGuardian)
        {
            var guardian = await _db.Guardians.FirstOrDefaultAsync(g => g.UserId == userId);
            if (guardian is null)
            {
                NoProfileLinked = true;
                return;
            }

            Children = await _db.Guardianships
                .Where(g => g.GuardianId == guardian.Id)
                .Join(_db.Students, g => g.StudentId, s => s.Id, (g, s) => new ChildOption(s.Id, s.FullName))
                .ToListAsync();

            if (studentId is null)
            {
                return; // show the child picker only
            }

            SelectedStudentId = studentId;
            SelectedStudentName = Children.FirstOrDefault(c => c.StudentId == studentId)?.FullName;
        }
        else
        {
            var student = await _db.Students.FirstOrDefaultAsync(s => s.UserId == userId);
            if (student is null)
            {
                NoProfileLinked = true;
                return;
            }

            SelectedStudentId = student.Id;
            SelectedStudentName = student.FullName;
        }

        if (SelectedStudentId is null)
        {
            return;
        }

        // Reuses IPlacementAttemptService's own IDOR check rather than
        // re-implementing "self or active guardian" a third time on this
        // page — its Unauthorized status is this page's authorization gate too.
        var eligibility = await _attempts.GetEligibilityAsync(SelectedStudentId.Value, userId, HttpContext.RequestAborted);
        if (eligibility.Status == PlacementEligibilityStatus.Unauthorized)
        {
            SelectedStudentId = null; // never display or act on a child that isn't actually theirs
            return;
        }

        if (eligibility.CurrentLevelId is null)
        {
            NeedsPlacementTest = true;
            return;
        }

        var level = await _db.Levels.FirstOrDefaultAsync(l => l.Id == eligibility.CurrentLevelId.Value);
        CurrentLevelCode = level?.Code;

        var courseNames = await _db.Courses.ToDictionaryAsync(c => c.Id, c => c.NameEn);
        var plans = await _db.PricingPlans
            .Where(p => p.IsActive && p.LevelId == eligibility.CurrentLevelId.Value)
            .OrderBy(p => p.SessionType)
            .ToListAsync();

        EligiblePlans = plans.Select(p => new PlanRow(p.Id, courseNames.GetValueOrDefault(p.CourseId, "?"),
            CurrentLevelCode ?? "?", p.SessionType, p.SessionsCount, p.MinutesTotal, p.Amount.Amount, p.Amount.Currency,
            p.ValidityDays)).ToList();
    }
}
