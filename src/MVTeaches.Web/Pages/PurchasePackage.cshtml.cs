using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using MVTeaches.Application.Files;
using MVTeaches.Application.Payments;
using MVTeaches.Application.Placement;
using MVTeaches.Application.Subscriptions;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Payments;
using MVTeaches.Domain.Subscriptions;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using MVTeaches.Web.Resources;
using NodaTime;

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
/// Owner decision 2026-08-30 (manual payment methods): once a Draft
/// subscription exists, this page also drives the SELF-SERVICE half of the
/// manual-transfer flow — requesting a Payment against it
/// (IPaymentService.RequestOwnPaymentAsync, safe for self-service precisely
/// because the amount is read from the subscription's own price snapshot,
/// never supplied by the caller), then reporting the transfer the payer
/// actually sent (AttachTransferDetailsAsync, the same self-or-guardian IDOR
/// guard as everywhere else, isAdminInitiated: false). Cash is deliberately
/// never offered here — a cash receipt is always admin-recorded in person,
/// never a self-service upload. A receipt is mandatory for every submission,
/// and the pre-transfer warnings plus the not-preset read-acknowledgment are
/// shown every time, matching Section 5 exactly.
/// </summary>
[Authorize(Roles = RoleNames.Student + "," + RoleNames.Guardian)]
public class PurchasePackageModel : PageModel
{
    private readonly MvTeachesDbContext _db;
    private readonly ISubscriptionService _subscriptions;
    private readonly IPlacementAttemptService _attempts;
    private readonly IPaymentService _payments;
    private readonly IPaymentMethodConfigService _methods;
    private readonly IFileStorageService _files;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public PurchasePackageModel(MvTeachesDbContext db, ISubscriptionService subscriptions,
        IPlacementAttemptService attempts, IPaymentService payments, IPaymentMethodConfigService methods,
        IFileStorageService files, UserManager<ApplicationUser> userManager, IStringLocalizer<SharedResource> localizer)
    {
        _db = db;
        _subscriptions = subscriptions;
        _attempts = attempts;
        _payments = payments;
        _methods = methods;
        _files = files;
        _userManager = userManager;
        _localizer = localizer;
    }

    public record ChildOption(long StudentId, string FullName);

    /// <summary>An offer poster the centre published (owner decision
    /// 2026-09-01). Advertising only - it grants nothing and prices nothing.
    /// The purchase list below is still built purely from the pricing plans
    /// published for this student's own level.</summary>
    public record PosterRow(long Id, string Title, string? Details, bool HasImage, long? ImageFileId);
    public record PlanRow(long Id, string CourseName, string LevelCode, SessionType SessionType,
        int SessionsCount, int MinutesTotal, decimal Amount, string Currency, int ValidityDays);

    /// <summary>Owner decision 2026-08-30 (shortfall/top-up policy): a Draft
    /// subscription is offered here for a NEW payment request whenever it
    /// still owes money and has no payment currently Pending — whether this
    /// is its very first request (ConfirmedReceived is 0) or a supplementary
    /// one after an earlier transfer arrived short.</summary>
    public record DraftSubscriptionRow(long Id, decimal Price, string Currency, decimal ConfirmedReceived, decimal RemainingOwed);

    public record PaymentRow(long Id, long? SubscriptionId, decimal Amount, string Currency, PaymentMethod Method,
        PaymentStatus Status, string ReferenceCode, bool HasSubmittedTransferDetails, string? RejectionReason,
        decimal? ReceivedAmount, string? ReceivedCurrency);

    public bool NoProfileLinked { get; set; }
    public bool IsGuardian { get; set; }
    public IReadOnlyList<ChildOption> Children { get; set; } = Array.Empty<ChildOption>();
    public long? SelectedStudentId { get; set; }
    public string? SelectedStudentName { get; set; }

    /// <summary>Rule 1's gate: no completed placement result yet.</summary>
    public bool NeedsPlacementTest { get; set; }
    public string? CurrentLevelCode { get; set; }
    public IReadOnlyList<PlanRow> EligiblePlans { get; set; } = Array.Empty<PlanRow>();

    /// <summary>Active posters, in the order the admin chose. Loaded before
    /// the placement-test gate so a student who cannot buy anything yet still
    /// sees what the centre is offering.</summary>
    public IReadOnlyList<PosterRow> Posters { get; set; } = Array.Empty<PosterRow>();

    /// <summary>Draft subscriptions with no Payment request against them yet
    /// — a payer picks a payment method for one of these next.</summary>
    public IReadOnlyList<DraftSubscriptionRow> UnpaidDraftSubscriptions { get; set; } = Array.Empty<DraftSubscriptionRow>();

    /// <summary>Never includes Cash (see class remarks) — the only methods
    /// this self-service page may ever offer.</summary>
    public IReadOnlyList<PaymentMethodConfig> ActiveMethods { get; set; } = Array.Empty<PaymentMethodConfig>();
    public IReadOnlyList<PaymentRow> OwnPayments { get; set; } = Array.Empty<PaymentRow>();

    [BindProperty]
    public TransferInput Transfer { get; set; } = new();

    public string? StatusMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public class TransferInput
    {
        [Required] public long PaymentId { get; set; }
        public string? PayerDisplayName { get; set; }
        public DateOnly? TransferDate { get; set; }
        public string? BankReferenceNumber { get; set; }
        public IFormFile? Receipt { get; set; }

        /// <summary>Section 5's not-preset read-acknowledgment — never
        /// defaulted true, and re-checked server-side below, since a
        /// checkbox's client-side "checked" attribute is never trusted on
        /// its own.</summary>
        public bool Acknowledged { get; set; }
    }

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
            StatusMessage = _localizer["Package requested (subscription #{0}, {1}) — choose a payment method below to continue.", result.SubscriptionId!, result.Price!].Value;
        }
        else
        {
            ErrorMessage = result.Outcome switch
            {
                PurchaseFromPlanOutcome.Unauthorized => _localizer["Not authorized for this student."].Value,
                PurchaseFromPlanOutcome.PlanNotFound => _localizer["Package not found."].Value,
                PurchaseFromPlanOutcome.PlanNotPublishedForAnyLevel => _localizer["This package is no longer available."].Value,
                PurchaseFromPlanOutcome.StudentHasNoAssignedLevel => _localizer["A placement result is required before purchasing a package."].Value,
                PurchaseFromPlanOutcome.LevelMismatch => _localizer["This package no longer matches the student's current level."].Value,
                _ => _localizer["Could not record this purchase."].Value,
            };
        }

        await LoadAsync(studentId);
        return Page();
    }

    public async Task<IActionResult> OnPostRequestPaymentAsync(long studentId, long subscriptionId, long paymentMethodConfigId)
    {
        var actingUserId = long.Parse(_userManager.GetUserId(User)!);
        var result = await _payments.RequestOwnPaymentAsync(studentId, subscriptionId, paymentMethodConfigId, actingUserId, HttpContext.RequestAborted);

        StatusMessage = result.Outcome == RequestOwnPaymentOutcome.Requested
            ? _localizer["Payment of {0} {1} requested — reference {2}. Read the instructions below, then send your transfer and report it here.",
                result.RequestedAmount!.Amount, result.RequestedAmount!.Currency, result.ReferenceCode!].Value
            : null;
        ErrorMessage = result.Outcome switch
        {
            RequestOwnPaymentOutcome.Unauthorized => _localizer["Not authorized for this student."].Value,
            RequestOwnPaymentOutcome.SubscriptionNotFound => _localizer["Package request not found."].Value,
            RequestOwnPaymentOutcome.SubscriptionNotDraft => _localizer["This package request is no longer awaiting payment."].Value,
            RequestOwnPaymentOutcome.AlreadyRequested => _localizer["A payment request already exists for this package — report your transfer against it below."].Value,
            RequestOwnPaymentOutcome.PaymentMethodNotFound => _localizer["Please choose a valid payment method."].Value,
            RequestOwnPaymentOutcome.AlreadyFullyFunded => _localizer["This package is already fully funded — nothing left to pay."].Value,
            _ => null,
        };

        await LoadAsync(studentId);
        return Page();
    }

    public async Task<IActionResult> OnPostSubmitTransferAsync(long studentId)
    {
        var actingUserId = long.Parse(_userManager.GetUserId(User)!);

        // Section 5: a receipt and the read-acknowledgment are both
        // mandatory for every self-service transfer-review submission —
        // checked here, server-side, never trusted from the form alone.
        if (!Transfer.Acknowledged)
        {
            ErrorMessage = _localizer["You must confirm you've read the instructions above before submitting."].Value;
            await LoadAsync(studentId);
            return Page();
        }

        if (Transfer.Receipt is null || Transfer.Receipt.Length == 0)
        {
            ErrorMessage = _localizer["A receipt is required to submit your transfer for review."].Value;
            await LoadAsync(studentId);
            return Page();
        }

        await using var stream = Transfer.Receipt.OpenReadStream();
        var payment = await _db.Payments.FirstOrDefaultAsync(p => p.Id == Transfer.PaymentId, HttpContext.RequestAborted);
        var uploadResult = await _files.SaveAsync(stream, nameof(MVTeaches.Domain.Files.FilePurpose.PaymentProof),
            Transfer.Receipt.FileName, actingUserId, HttpContext.RequestAborted, payment?.StudentId);

        if (uploadResult.Outcome != SaveUploadOutcome.Saved)
        {
            ErrorMessage = uploadResult.Outcome switch
            {
                SaveUploadOutcome.RejectedContentType => _localizer["The receipt must be a JPEG, PNG, or PDF file."].Value,
                SaveUploadOutcome.RejectedTooLarge => _localizer["The receipt file is too large."].Value,
                _ => _localizer["The receipt could not be uploaded."].Value,
            };
            await LoadAsync(studentId);
            return Page();
        }

        var transferDate = Transfer.TransferDate.HasValue
            ? new LocalDate(Transfer.TransferDate.Value.Year, Transfer.TransferDate.Value.Month, Transfer.TransferDate.Value.Day)
            : (LocalDate?)null;

        var attachResult = await _payments.AttachTransferDetailsAsync(Transfer.PaymentId, actingUserId, isAdminInitiated: false,
            Transfer.PayerDisplayName, transferDate, Transfer.BankReferenceNumber, uploadResult.DocumentId, HttpContext.RequestAborted);

        StatusMessage = attachResult.Outcome == AttachTransferDetailsOutcome.Attached
            ? _localizer["Transfer details submitted — thank you. The centre will confirm once the money is verified."].Value
            : null;
        ErrorMessage = attachResult.Outcome switch
        {
            AttachTransferDetailsOutcome.NotFound => _localizer["Payment not found."].Value,
            AttachTransferDetailsOutcome.Unauthorized => _localizer["Not authorized for this student."].Value,
            AttachTransferDetailsOutcome.NotPending => _localizer["This payment is no longer pending."].Value,
            AttachTransferDetailsOutcome.DuplicateReference => _localizer["This bank reference looks like it was already used for another payment — please double check it."].Value,
            _ => null,
        };

        await LoadAsync(studentId);
        return Page();
    }

    private bool IsGuardianRole() => User.IsInRole(RoleNames.Guardian);

    private async Task LoadAsync(long? studentId)
    {
        var userId = long.Parse(_userManager.GetUserId(User)!);
        IsGuardian = IsGuardianRole();

        // Cash is deliberately excluded — see class remarks.
        ActiveMethods = (await _methods.ListActiveAsync(HttpContext.RequestAborted))
            .Where(m => m.Type != PaymentMethod.Cash).ToList();

        Posters = (await _db.PromotionalPosters.AsNoTracking()
                .Where(poster => poster.IsActive)
                .OrderBy(poster => poster.SortOrder).ThenBy(poster => poster.Id)
                .ToListAsync())
            .Select(poster => new PosterRow(poster.Id, poster.Title, poster.Details,
                poster.ImageFileId is not null, poster.ImageFileId))
            .ToList();

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

        var payments = await _db.Payments.Where(p => p.StudentId == SelectedStudentId).OrderByDescending(p => p.Id).ToListAsync();
        OwnPayments = payments.Select(p => new PaymentRow(p.Id, p.SubscriptionId, p.Amount.Amount, p.Amount.Currency,
            p.Method, p.Status, p.ReferenceCode, p.HasSubmittedTransferDetails, p.RejectionReason,
            p.ReceivedAmount, p.ReceivedCurrency)).ToList();

        // Owner decision 2026-08-30 (shortfall/top-up policy): a subscription
        // is excluded from "can request a new payment" ONLY while it has a
        // payment currently Pending (awaiting transfer or under review) —
        // NOT because it has ever had any payment at all. A Confirmed-but-
        // short payment must never permanently block a legitimate
        // supplementary request for the same subscription.
        var pendingSubscriptionIds = payments.Where(p => p.Status == PaymentStatus.Pending && p.SubscriptionId is not null)
            .Select(p => p.SubscriptionId!.Value).ToHashSet();
        var drafts = await _db.Subscriptions
            .Where(s => s.StudentId == SelectedStudentId && s.Status == SubscriptionStatus.Draft)
            .ToListAsync();
        var eligibleDrafts = new List<DraftSubscriptionRow>();
        foreach (var s in drafts.Where(s => !pendingSubscriptionIds.Contains(s.Id)))
        {
            var funding = await _payments.GetSubscriptionFundingStatusAsync(s.Id, HttpContext.RequestAborted);
            if (!funding.IsFullyFunded)
            {
                eligibleDrafts.Add(new DraftSubscriptionRow(s.Id, funding.Price.Amount, funding.Price.Currency,
                    funding.ConfirmedReceived, funding.RemainingOwed.Amount));
            }
        }
        UnpaidDraftSubscriptions = eligibleDrafts;

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
