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

    public record PaymentRow(long Id, long StudentId, string StudentName, decimal Amount, string Currency, PaymentMethod Method,
        PaymentStatus Status, string ReferenceCode, string? RejectionReason, string? PayerDisplayName,
        NodaTime.LocalDate? TransferDate, string? BankReferenceNumber, bool HasReceipt, bool HasSubmittedTransferDetails,
        decimal? ReceivedAmount, string? ReceivedCurrency);

    public record DraftSubscriptionRow(long Id, string StudentName, string LevelCode, decimal Price, string Currency,
        decimal ConfirmedReceived, decimal RemainingOwed);

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
        [Required]
        public long? StudentId { get; set; }

        /// <summary>Optional — ties this payment to a Draft subscription so
        /// confirming it can activate that subscription and post the Purchase
        /// ledger entry (D-38, already implemented in IPaymentService). Leave
        /// blank for a generic payment with no subscription attached.</summary>
        public long? SubscriptionId { get; set; }

        [Required, Range(0.001, double.MaxValue)]
        public decimal Amount { get; set; }

        [Required, StringLength(3, MinimumLength = 3)]
        public string Currency { get; set; } = string.Empty;

        [Required]
        public PaymentMethod Method { get; set; }
    }

    public async Task OnGetAsync() => await LoadAsync();

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
            _ => null,
        };

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostRejectAsync(long paymentId)
    {
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
            draftRows.Add(new DraftSubscriptionRow(s.Id, studentNames.GetValueOrDefault(s.StudentId, string.Empty),
                levelCodes.GetValueOrDefault(s.LevelId, "—"),
                funding.Price.Amount, funding.Price.Currency, funding.ConfirmedReceived, funding.RemainingOwed.Amount));
        }
        DraftSubscriptions = draftRows;

        PendingPayments = pending.Select(p => ToRow(p, studentNames)).ToList();
        RecentPayments = recent.Select(p => ToRow(p, studentNames)).ToList();
    }

    private static PaymentRow ToRow(Payment p, IReadOnlyDictionary<long, string> studentNames) =>
        new(p.Id, p.StudentId, studentNames.GetValueOrDefault(p.StudentId, string.Empty), p.Amount.Amount, p.Amount.Currency,
            p.Method, p.Status, p.ReferenceCode, p.RejectionReason, p.PayerDisplayName, p.TransferDate,
            p.ProviderTransactionId, p.ProofFileId is not null, p.HasSubmittedTransferDetails, p.ReceivedAmount, p.ReceivedCurrency);
}
