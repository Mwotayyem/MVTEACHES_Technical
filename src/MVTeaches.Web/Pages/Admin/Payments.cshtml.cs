using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using MVTeaches.Application.Payments;
using MVTeaches.Domain.Common;
using MVTeaches.Domain.Payments;
using MVTeaches.Domain.People;
using MVTeaches.Domain.Subscriptions;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using MVTeaches.Web.Display;
using MVTeaches.Web.Resources;

namespace MVTeaches.Web.Pages.Admin;

/// <summary>
/// §22 (D-11/D-38/D-14) — the manual channel's admin surface: record a
/// payment a student/guardian reported (bank transfer/CliQ + proof, per
/// D-39/D-11), then confirm or reject it. All business logic lives in
/// IPaymentService, already tested against real PostgreSQL; this page is a
/// thin form + list over it, same as every other admin screen so far.
///
/// Owner decision 2026-08-30 (Section 6): the user-facing confirm action is
/// deliberately labeled "تأكيد استلام المبلغ وتفعيل الباقة" / "Confirm
/// receipt of the amount and activate the package" — a click here means the
/// admin has actually verified the bank/CliQ account, not merely acknowledged
/// a receipt upload. The optional received-amount/currency fields exist only
/// for the discrepancy case (an international transfer's fee/shortfall, or a
/// different currency arriving) — left blank, confirming means exactly what
/// it always meant: the full requested amount, in the requested currency.
/// </summary>
[Authorize(Roles = RoleNames.Admin + "," + RoleNames.SystemAdmin)]
public class PaymentsModel : PageModel
{
    private readonly MvTeachesDbContext _db;
    private readonly IPaymentService _payments;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public PaymentsModel(MvTeachesDbContext db, IPaymentService payments, UserManager<ApplicationUser> userManager,
        IStringLocalizer<SharedResource> localizer)
    {
        _db = db;
        _payments = payments;
        _userManager = userManager;
        _localizer = localizer;
    }

    /// <summary><paramref name="SubscriptionId"/> and the three package
    /// figures beside it were added after the owner confirmed 20 JOD against a
    /// package that had 10 left owing. The screen never said what a payment
    /// was funding or how much that package still needed, so there was nothing
    /// on it to notice the mistake against.</summary>
    public record PaymentRow(long Id, long StudentId, string StudentName, decimal Amount, string Currency, PaymentMethod Method,
        PaymentStatus Status, string ReferenceCode, string? RejectionReason, string? PayerDisplayName,
        NodaTime.LocalDate? TransferDate, string? BankReferenceNumber, bool HasReceipt, bool HasSubmittedTransferDetails,
        decimal? ReceivedAmount, string? ReceivedCurrency,
        long? SubscriptionId, string? PackageLabel, decimal? PackagePrice, decimal? PackagePaid, decimal? PackageRemaining);

    /// <summary>The payments on one package, read together, plus where that
    /// package stands. The flat list mixed a package's own instalments with
    /// money that was never attached to any package, so an admin could not
    /// tell whether a package was settled, overpaid, or still short.</summary>
    public record PaymentGroup(long? SubscriptionId, string Title, string? Currency,
        decimal? Price, decimal? Paid, decimal? Remaining, SubscriptionStatus? Status,
        IReadOnlyList<PaymentRow> Payments);

    /// <summary><paramref name="StudentId"/> is what lets the picker below
    /// show only the chosen student's own unpaid packages. Before it, the list
    /// offered every student's draft at once, so an admin recording Zaid's
    /// payment could see — and pick — Heba's package. The server has always
    /// validated the pair; this stops the wrong pair being offered at all.</summary>
    public record DraftSubscriptionRow(long Id, long StudentId, string StudentName, string LevelCode, decimal Price,
        string Currency, decimal ConfirmedReceived, decimal RemainingOwed);

    public IReadOnlyList<PaymentRow> PendingPayments { get; set; } = Array.Empty<PaymentRow>();
    public IReadOnlyList<PaymentRow> RecentPayments { get; set; } = Array.Empty<PaymentRow>();
    // Fully qualified to avoid ambiguity with the sibling MVTeaches.Web.Pages.Student namespace.
    public IReadOnlyList<MVTeaches.Domain.People.Student> Students { get; set; } = Array.Empty<MVTeaches.Domain.People.Student>();

    /// <summary>Currency codes actually configured for the active countries —
    /// so an admin picks JOD from a list instead of typing three letters that
    /// nothing would have corrected.</summary>
    public IReadOnlyList<string> Currencies { get; set; } = Array.Empty<string>();

    /// <summary>Set when the admin arrived here from one student's row, so the
    /// page shows that student's money only and says whose it is. Read-only
    /// narrowing of the same lists — never a different query.</summary>
    [BindProperty(SupportsGet = true, Name = "studentId")]
    public long? FilterStudentId { get; set; }

    public string? FilterStudentName { get; set; }

    /// <summary>Draft subscriptions awaiting their activating payment (D-38) —
    /// see /Admin/Subscriptions, which is where these get created.</summary>
    public IReadOnlyList<DraftSubscriptionRow> DraftSubscriptions { get; set; } = Array.Empty<DraftSubscriptionRow>();

    /// <summary>The subscriptions belonging to the student currently in view
    /// that still owe money. When this is non-empty the recording form leads
    /// with them, because a payment from a student who owes on a package is
    /// almost always for that package - and recording it as unattached money
    /// (which is what happened) leaves the package unfunded while the family
    /// has already paid.</summary>
    public IReadOnlyList<DraftSubscriptionRow> OwedPackagesForFilteredStudent =>
        FilterStudentId is null
            ? Array.Empty<DraftSubscriptionRow>()
            : DraftSubscriptions.Where(d => d.StudentId == FilterStudentId.Value && d.RemainingOwed > 0m).ToList();

    /// <summary>Set by the "complete the remaining amount" button, which
    /// pre-fills the form for exactly that package and exactly what it still
    /// needs. Display and pre-fill only - the server still validates the
    /// student/subscription pair and still recomputes the remaining balance
    /// itself at confirmation time.</summary>
    [BindProperty(SupportsGet = true, Name = "complete")]
    public long? CompleteSubscriptionId { get; set; }

    public DraftSubscriptionRow? CompletingPackage { get; set; }

    /// <summary>Recent payments gathered by the package they funded.</summary>
    public IReadOnlyList<PaymentGroup> RecentGroups { get; set; } = Array.Empty<PaymentGroup>();

    [BindProperty]
    public RecordInput NewPayment { get; set; } = new();

    [BindProperty]
    public string? RejectReason { get; set; }

    public string? StatusMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public class RecordInput
    {
        // Nullable so [Required] actually fires when the picker is left alone:
        // on a non-nullable long an untouched <select> posts "", the binding
        // error is wiped by ModelState.Clear() below, and [Required] then
        // passes on the defaulted 0. Same fix as Teacher/PublishSlots.cshtml.cs.
        [Required(ErrorMessage = "Choose a student.")]
        public long? StudentId { get; set; }

        /// <summary>Optional — ties this payment to a Draft subscription so
        /// confirming it can activate that subscription and post the Purchase
        /// ledger entry (D-38, already implemented in IPaymentService). Leave
        /// blank for a generic payment with no subscription attached.</summary>
        public long? SubscriptionId { get; set; }

        [Required(ErrorMessage = "Enter the amount."), Range(0.001, double.MaxValue, ErrorMessage = "The amount must be greater than zero.")]
        public decimal Amount { get; set; }

        [Required(ErrorMessage = "Choose a currency."), StringLength(3, MinimumLength = 3)]
        public string Currency { get; set; } = string.Empty;

        [Required]
        public PaymentMethod Method { get; set; }
    }

    public async Task OnGetAsync()
    {
        await LoadAsync();

        if (CompleteSubscriptionId is null)
        {
            return;
        }

        CompletingPackage = DraftSubscriptions.FirstOrDefault(d => d.Id == CompleteSubscriptionId.Value);
        if (CompletingPackage is null)
        {
            CompleteSubscriptionId = null;
            return;
        }

        NewPayment.StudentId = CompletingPackage.StudentId;
        NewPayment.SubscriptionId = CompletingPackage.Id;
        NewPayment.Amount = CompletingPackage.RemainingOwed;
        NewPayment.Currency = CompletingPackage.Currency;
    }

    public async Task<IActionResult> OnPostRecordAsync()
    {
        // See StudentsModel's OnPostRegisterGuardianAsync remarks: with more than
        // one [BindProperty] group on a page, the whole slate must be cleared
        // before re-validating just this handler's own group.
        ModelState.Clear();
        if (!TryValidateModel(NewPayment, nameof(NewPayment)))
        {
            await LoadAsync();
            return Page();
        }

        var request = new RecordPaymentRequest(NewPayment.StudentId!.Value, NewPayment.SubscriptionId, PayerUserId: null,
            new Money(NewPayment.Amount, NewPayment.Currency), NewPayment.Method, ProofFileId: null);
        var result = await _payments.RecordManualPaymentAsync(request, HttpContext.RequestAborted);
        StatusMessage = _localizer["Payment recorded — reference {0}, awaiting confirmation.", result.ReferenceCode].Value;

        await LoadAsync();
        return Page();
    }

    /// <summary><paramref name="receivedAmount"/>/<paramref name="receivedCurrency"/>
    /// are left null on the ordinary path (both come through as null/empty
    /// from an untouched form) — only a discrepancy the admin actually typed
    /// in reaches IPaymentService.ConfirmAsync as a real override.</summary>
    public async Task<IActionResult> OnPostConfirmAsync(long paymentId, decimal? receivedAmount, string? receivedCurrency)
    {
        // This handler does not use NewPayment at all, but Razor Pages
        // validates every [BindProperty] on the page regardless - so an
        // untouched recording form posted alongside a confirmation produced a
        // red "choose a currency / the amount must be greater than zero" block
        // next to the real outcome message. Confusing on a good day; actively
        // misleading next to the new "more than is owed" refusal.
        ModelState.Clear();

        var confirmedByUserId = long.Parse(_userManager.GetUserId(User)!);
        Money? actuallyReceived = receivedAmount is not null && !string.IsNullOrWhiteSpace(receivedCurrency)
            ? new Money(receivedAmount.Value, receivedCurrency)
            : null;

        var result = await _payments.ConfirmAsync(paymentId, confirmedByUserId, HttpContext.RequestAborted, actuallyReceived);

        StatusMessage = result.Outcome switch
        {
            ConfirmPaymentOutcome.Confirmed => _localizer["Payment confirmed and package activated."].Value,
            ConfirmPaymentOutcome.AlreadyConfirmed => _localizer["This payment was already confirmed."].Value,
            ConfirmPaymentOutcome.ConfirmedButSubscriptionNotYetFullyFunded =>
                _localizer["Payment confirmed as received, but the package needs more funds before it can activate (shortfall or currency mismatch) — it stays inactive until resolved."].Value,
            _ => null,
        };
        ErrorMessage = result.Outcome switch
        {
            ConfirmPaymentOutcome.NotFound => _localizer["Payment not found."].Value,
            ConfirmPaymentOutcome.NotPending => _localizer["This payment is no longer pending (already rejected)."].Value,
            // Nothing was written: the payment is still Pending and can be
            // confirmed again with the right figure.
            // Worded for what this payment actually is: a package instalment
            // has a package to talk about, a standalone one does not.
            ConfirmPaymentOutcome.ReceivedAmountExceedsWhatIsOwed => _localizer[
                await _db.Payments.AnyAsync(p => p.Id == paymentId && p.SubscriptionId != null, HttpContext.RequestAborted)
                    ? "The amount received is larger than the amount still owed on this package. The most that can be confirmed here is {0}. Nothing was recorded — correct the figure and confirm again. If more money really did arrive, record the difference as a separate payment and tell the family."
                    : "The amount received is larger than the amount this payment was recorded for. The most that can be confirmed here is {0}. Nothing was recorded — correct the figure and confirm again, or reject this one and record the real amount instead.",
                _localizer.Money(result.MaximumAcceptable!.Amount, result.MaximumAcceptable!.Currency)].Value,
            _ => null,
        };

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostRejectAsync(long paymentId)
    {
        ModelState.Clear(); // same reason as OnPostConfirmAsync above
        var rejectedByUserId = long.Parse(_userManager.GetUserId(User)!);
        var reason = string.IsNullOrWhiteSpace(RejectReason) ? _localizer["No reason given"].Value : RejectReason;

        try
        {
            await _payments.RejectAsync(paymentId, reason, rejectedByUserId, HttpContext.RequestAborted);
            StatusMessage = _localizer["Payment rejected — the payer will see this reason and can resubmit."].Value;
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
        Students = await _db.Students.OrderBy(s => s.FullName).Take(200).ToListAsync();

        // Ordered by the country rows themselves (the home market is seeded
        // first), so the first option is the currency this centre actually
        // bills in — a default that comes from configured data, not from a
        // currency code written into the page.
        Currencies = (await _db.Countries.Where(c => c.IsActive)
                .OrderBy(c => c.Id)
                .Select(c => c.CurrencyCode)
                .ToListAsync())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var drafts = await _db.Subscriptions
            .Where(s => s.Status == SubscriptionStatus.Draft)
            .Where(s => FilterStudentId == null || s.StudentId == FilterStudentId)
            .OrderByDescending(s => s.Id)
            .Take(100)
            .ToListAsync();

        var pending = await _db.Payments
            .Where(p => p.Status == PaymentStatus.Pending)
            .Where(p => FilterStudentId == null || p.StudentId == FilterStudentId)
            .OrderBy(p => p.CreatedAtUtc)
            .ToListAsync();

        var recent = await _db.Payments
            .Where(p => p.Status != PaymentStatus.Pending)
            .Where(p => FilterStudentId == null || p.StudentId == FilterStudentId)
            .OrderByDescending(p => p.Id)
            .Take(50)
            .ToListAsync();

        // Resolve every name this page will actually print, rather than only the
        // ones that happen to fall inside the 200-row picker window — a payment
        // used to show as "#41" whenever its student sorted outside it.
        var neededStudentIds = drafts.Select(s => s.StudentId)
            .Concat(pending.Select(p => p.StudentId))
            .Concat(recent.Select(p => p.StudentId))
            .Concat(FilterStudentId is null ? Array.Empty<long>() : new[] { FilterStudentId.Value })
            .Distinct()
            .ToList();
        var studentNames = await _db.Students
            .Where(s => neededStudentIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.FullName);

        FilterStudentName = FilterStudentId is null ? null : studentNames.GetValueOrDefault(FilterStudentId.Value);

        var levelCodes = await _db.Levels.ToDictionaryAsync(l => l.Id, l => l.Code);
        var draftRows = new List<DraftSubscriptionRow>();
        foreach (var s in drafts)
        {
            var funding = await _payments.GetSubscriptionFundingStatusAsync(s.Id, HttpContext.RequestAborted);
            draftRows.Add(new DraftSubscriptionRow(s.Id, s.StudentId, studentNames.GetValueOrDefault(s.StudentId, string.Empty),
                levelCodes.GetValueOrDefault(s.LevelId, "—"),
                funding.Price.Amount, funding.Price.Currency, funding.ConfirmedReceived, funding.RemainingOwed.Amount));
        }
        DraftSubscriptions = draftRows;

        // Every subscription any payment on this screen points at - not only
        // the Draft ones. A payment against a package that has since
        // activated still has to say so, or the history reads as a pile of
        // unrelated amounts.
        var subscriptionIds = pending.Concat(recent)
            .Where(p => p.SubscriptionId is not null)
            .Select(p => p.SubscriptionId!.Value)
            .Distinct()
            .ToList();
        var subscriptions = await _db.Subscriptions
            .Where(sub => subscriptionIds.Contains(sub.Id))
            .ToListAsync();
        var courseNames = await _db.Courses.ToDictionaryAsync(c => c.Id, c => c.NameEn);

        var fundingBySubscription = new Dictionary<long, SubscriptionFundingStatus>();
        foreach (var sub in subscriptions)
        {
            fundingBySubscription[sub.Id] = await _payments.GetSubscriptionFundingStatusAsync(sub.Id, HttpContext.RequestAborted);
        }

        string PackageLabel(Subscription sub) =>
            $"{courseNames.GetValueOrDefault(sub.CourseId, "?")} / {levelCodes.GetValueOrDefault(sub.LevelId, "?")}";

        PaymentRow Row(Payment payment)
        {
            var sub = payment.SubscriptionId is null
                ? null
                : subscriptions.FirstOrDefault(x => x.Id == payment.SubscriptionId.Value);
            var status = sub is null ? null : fundingBySubscription.GetValueOrDefault(sub.Id);
            return new PaymentRow(payment.Id, payment.StudentId,
                studentNames.GetValueOrDefault(payment.StudentId, string.Empty),
                payment.Amount.Amount, payment.Amount.Currency, payment.Method, payment.Status,
                payment.ReferenceCode, payment.RejectionReason, payment.PayerDisplayName, payment.TransferDate,
                payment.ProviderTransactionId, payment.ProofFileId is not null, payment.HasSubmittedTransferDetails,
                payment.ReceivedAmount, payment.ReceivedCurrency,
                sub?.Id, sub is null ? null : PackageLabel(sub),
                status?.Price.Amount, status?.ConfirmedReceived, status?.RemainingOwed.Amount);
        }

        PendingPayments = pending.Select(Row).ToList();
        RecentPayments = recent.Select(Row).ToList();

        // Grouped: one block per package, then one block for money that was
        // never attached to a package. Within a block the payments stay in the
        // same order the flat list used.
        RecentGroups = RecentPayments
            .GroupBy(row => row.SubscriptionId)
            .OrderBy(group => group.Key is null ? 1 : 0)
            .ThenByDescending(group => group.Key ?? 0)
            .Select(group =>
            {
                if (group.Key is null)
                {
                    return new PaymentGroup(null, _localizer["Not against any package"].Value, null,
                        null, null, null, null, group.ToList());
                }

                var sub = subscriptions.FirstOrDefault(x => x.Id == group.Key.Value);
                var status = fundingBySubscription.GetValueOrDefault(group.Key.Value);
                return new PaymentGroup(group.Key, sub is null ? $"#{group.Key}" : PackageLabel(sub),
                    status?.Price.Currency, status?.Price.Amount, status?.ConfirmedReceived,
                    status?.RemainingOwed.Amount, sub?.Status, group.ToList());
            })
            .ToList();
    }
}
