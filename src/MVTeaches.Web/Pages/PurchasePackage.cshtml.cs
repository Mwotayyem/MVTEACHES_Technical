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
    private readonly IPromoCodeService _promoCodes;
    private readonly IFileStorageService _files;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public PurchasePackageModel(MvTeachesDbContext db, ISubscriptionService subscriptions,
        IPlacementAttemptService attempts, IPaymentService payments, IPaymentMethodConfigService methods,
        IFileStorageService files, UserManager<ApplicationUser> userManager, IStringLocalizer<SharedResource> localizer,
        IPromoCodeService promoCodes)
    {
        _promoCodes = promoCodes;
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

    /// <summary>Owner decision 2026-09-04: this student's own login may not buy,
    /// because a guardian is registered as responsible for them. Set only on the
    /// student's own view — a guardian looking at their child never sees it.
    /// Hiding the buttons is a courtesy so nobody presses one that is certain to
    /// fail; PurchaseFromPlanAsync refuses the POST regardless of this flag,
    /// which is what actually enforces the rule.</summary>
    public bool PurchasedByGuardianOnly { get; set; }

    /// <summary>Owner decision 2026-09-04 (multi-course levels): one entry per
    /// course this student is placed in. There is no such thing as "the
    /// student's level" any more — someone studying English and Spanish holds
    /// two, and each one opens a different shelf of packages.</summary>
    public record CoursePlacement(long CourseId, string CourseName, string LevelCode);

    public IReadOnlyList<CoursePlacement> CurrentPlacements { get; set; } = Array.Empty<CoursePlacement>();
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

    /// <summary>Owner decision 2026-09-05 (promo codes). What the family typed,
    /// kept so the box is not cleared under them when the page comes back, and
    /// so the Buy buttons can carry it through. It is six characters of text
    /// and nothing else - no price, no percentage - and the server re-prices it
    /// from the database on every use.</summary>
    [BindProperty(SupportsGet = true, Name = "promoCode")]
    public string? PromoCodeInput { get; set; }

    /// <summary>The quote the SERVER produced for the checked code, per plan.
    /// Only ever filled by asking IPromoCodeService; the page cannot compute a
    /// discount of its own.</summary>
    public IReadOnlyDictionary<long, PromoCodeQuote> PromoQuotesByPlan { get; set; } =
        new Dictionary<long, PromoCodeQuote>();

    /// <summary>Set when a code was typed and cannot be used, so the screen can
    /// say which of "expired", "not for this package" or "used up" applies.</summary>
    public string? PromoMessage { get; set; }
    public bool PromoAccepted { get; set; }

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

    /// <summary>Checks a code and shows what it is worth, without buying
    /// anything. The figures come back from the service - this handler does no
    /// arithmetic of its own, and neither does the page.</summary>
    public async Task<IActionResult> OnPostApplyPromoAsync(long studentId)
    {
        ModelState.Clear();
        await LoadAsync(studentId);

        if (string.IsNullOrWhiteSpace(PromoCodeInput))
        {
            PromoMessage = _localizer["Enter a promo code first."].Value;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostPurchaseAsync(long studentId, long pricingPlanId)
    {
        var actingUserId = long.Parse(_userManager.GetUserId(User)!);
        var origin = IsGuardianRole() ? SubscriptionOrigin.GuardianPurchase : SubscriptionOrigin.SelfPurchase;

        // The CODE travels; the price does not. Whatever the form sent for a
        // discount would be ignored - PurchaseFromPlanAsync re-prices from the
        // plan and the code's own stored percentage.
        var result = await _subscriptions.PurchaseFromPlanAsync(studentId, pricingPlanId, actingUserId, origin,
            isAdminInitiated: false, HttpContext.RequestAborted, PromoCodeInput);

        // Owner report 2026-09-05: loaded BEFORE the messages are built, because
        // every message below has to be able to name the child. A guardian with
        // two daughters bought a package for the first, then read "you already
        // have a request for this package (#3) still awaiting payment" while
        // buying for the second, and reasonably read it as the FIRST child's
        // request blocking the second. It never was - the guard is keyed on the
        // student (see SubscriptionService.PurchaseFromPlanAsync) and cannot
        // see a sibling at all - but a message about a family's child that does
        // not say which child is not a message about anything.
        await LoadAsync(studentId);
        var childName = SelectedStudentName ?? _localizer["this student"].Value;

        if (result.Outcome == PurchaseFromPlanOutcome.Purchased)
        {
            // A 100% code leaves nothing to pay, so the package is already
            // active and there is no payment step to send anyone to.
            StatusMessage = result.ActivatedWithoutPayment
                ? _localizer["The promo code covered the whole price — {0}'s package (#{1}) is active now. There is nothing to pay and no transfer to send.",
                    childName, result.SubscriptionId!].Value
                : _localizer["Package requested for {0} (request #{1}, {2}) — choose a payment method below to continue.",
                    childName, result.SubscriptionId!, result.Price!].Value;
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
                // Owner decision 2026-09-04 (duplicate-purchase guard): both of
                // these name what the payer already has, so the next step is
                // obvious — finish paying that request, or use up those hours —
                // instead of leaving them to press the same button again.
                PurchaseFromPlanOutcome.DraftAlreadyAwaitingPayment =>
                    _localizer["{0} already has a request for this same package (request #{1}) still awaiting payment. Finish paying that request below instead of requesting it again. This is about {0} only — a package for another child is requested and paid for separately.",
                        childName, result.SubscriptionId!].Value,
                PurchaseFromPlanOutcome.ActivePackageStillHasBalance =>
                    _localizer["{0} already has an active package on this same plan with hours still remaining. The same package cannot be bought again for {0} until those hours are used — another child's package is separate.",
                        childName].Value,
                // Refused, never silently charged at full price.
                PurchaseFromPlanOutcome.PromoCodeRejected => DescribePromoRejection(result.PromoRejection),
                PurchaseFromPlanOutcome.StudentIsUnderGuardianCare =>
                    _localizer["Packages for this student are purchased by their guardian. Please ask your guardian to buy it from their own account, or contact the centre."].Value,
                _ => _localizer["Could not record this purchase."].Value,
            };
        }

        return Page(); // already loaded above, so the messages could name the child
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

            // See PurchasedByGuardianOnly — display only; the service is the guard.
            PurchasedByGuardianOnly = await _db.Guardianships.AnyAsync(g => g.StudentId == student.Id);
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

        // Not placed in ANY course: rule 1's gate, unchanged in meaning.
        if (eligibility.CurrentLevels.Count == 0)
        {
            NeedsPlacementTest = true;
            return;
        }

        var courseNames = await _db.Courses.ToDictionaryAsync(c => c.Id, c => c.NameEn);
        var levelCodes = await _db.Levels.ToDictionaryAsync(l => l.Id, l => l.Code);

        CurrentPlacements = eligibility.CurrentLevels
            .Select(p => new CoursePlacement(p.CourseId, courseNames.GetValueOrDefault(p.CourseId, "?"),
                levelCodes.GetValueOrDefault(p.LevelId, "?")))
            .OrderBy(p => p.CourseName)
            .ToList();

        // Owner decision 2026-09-04 (multi-course levels): a package belongs to
        // a (course, level) PAIR. Matching on the level alone — which is what
        // this did — offered a student placed at B2 in English every OTHER
        // course's B2 package as well, in subjects they have never been placed
        // in and cannot attend. The two Contains() clauses narrow the query;
        // the pair check below is what actually decides, since a student
        // holding English B2 and Spanish A1 must not be shown English A1.
        var placedCourseIds = eligibility.CurrentLevels.Select(p => p.CourseId).Distinct().ToList();
        var placedLevelIds = eligibility.CurrentLevels.Select(p => p.LevelId).Distinct().ToList();
        var placements = eligibility.CurrentLevels.Select(p => (p.CourseId, p.LevelId)).ToHashSet();

        // PricingPlan.LevelId is nullable ("applies to every level"), and
        // PurchaseFromPlanAsync refuses such a plan outright
        // (PlanNotPublishedForAnyLevel). Excluding it here as well keeps this
        // page showing exactly what the service would accept.
        var candidatePlans = await _db.PricingPlans
            .Where(p => p.IsActive && p.LevelId != null
                        && placedCourseIds.Contains(p.CourseId) && placedLevelIds.Contains(p.LevelId.Value))
            .ToListAsync();

        EligiblePlans = candidatePlans
            .Where(p => placements.Contains((p.CourseId, p.LevelId!.Value)))
            .Select(p => new PlanRow(p.Id, courseNames.GetValueOrDefault(p.CourseId, "?"),
                levelCodes.GetValueOrDefault(p.LevelId!.Value, "?"), p.SessionType, p.SessionsCount, p.MinutesTotal,
                p.Amount.Amount, p.Amount.Currency, p.ValidityDays))
            .OrderBy(p => p.CourseName)
            .ThenBy(p => p.SessionType)
            .ToList();

        await LoadPromoQuotesAsync();
    }

    /// <summary>Asks the service what the typed code is worth on each package
    /// the family can actually buy. Every figure shown on screen comes from
    /// here; the page never multiplies anything by a percentage itself, which
    /// is what keeps the displayed price and the charged price the same number
    /// by construction rather than by agreement.</summary>
    private async Task LoadPromoQuotesAsync()
    {
        PromoQuotesByPlan = new Dictionary<long, PromoCodeQuote>();
        PromoAccepted = false;

        if (string.IsNullOrWhiteSpace(PromoCodeInput) || SelectedStudentId is null || EligiblePlans.Count == 0)
        {
            return;
        }

        var quotes = new Dictionary<long, PromoCodeQuote>();
        PromoCodeRejection? lastRejection = null;

        foreach (var plan in EligiblePlans)
        {
            var applied = await _promoCodes.ApplyAsync(PromoCodeInput, plan.Id, SelectedStudentId.Value,
                HttpContext.RequestAborted);
            if (applied.Accepted)
            {
                quotes[plan.Id] = applied.Quote!;
            }
            else
            {
                lastRejection = applied.Rejection;
            }
        }

        PromoQuotesByPlan = quotes;
        PromoAccepted = quotes.Count > 0;

        if (quotes.Count > 0)
        {
            PromoMessage = _localizer["Code applied. The new price is shown on each package it covers."].Value;
        }
        else if (lastRejection is not null)
        {
            PromoMessage = DescribePromoRejection(lastRejection);
        }
    }

    /// <summary>One message per reason, so the family is told what to do next
    /// rather than that "something" was wrong. A code that does not exist and a
    /// code that is malformed are deliberately given the SAME answer - the
    /// service does that too - so nobody can learn the shape of a real code by
    /// watching this message change.</summary>
    private string DescribePromoRejection(PromoCodeRejection? rejection) => rejection switch
    {
        PromoCodeRejection.Inactive => _localizer["This promo code is no longer available."].Value,
        PromoCodeRejection.NotStartedYet => _localizer["This promo code cannot be used yet."].Value,
        PromoCodeRejection.Expired => _localizer["This promo code has expired."].Value,
        PromoCodeRejection.NotForThisPackage => _localizer["This promo code does not apply to this package."].Value,
        PromoCodeRejection.TotalLimitReached => _localizer["This promo code has been fully used."].Value,
        PromoCodeRejection.StudentLimitReached => _localizer["This promo code has already been used for this student."].Value,
        _ => _localizer["That promo code was not recognised."].Value,
    };
}
