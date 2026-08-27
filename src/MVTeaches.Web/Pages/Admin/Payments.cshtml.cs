using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Payments;
using MVTeaches.Domain.Common;
using MVTeaches.Domain.Payments;
using MVTeaches.Domain.People;
using MVTeaches.Domain.Subscriptions;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;

namespace MVTeaches.Web.Pages.Admin;

/// <summary>
/// §22 (D-11/D-38/D-14) — the manual channel's admin surface: record a
/// payment a student/guardian reported (bank transfer/CliQ + proof, per
/// D-39/D-11), then confirm or reject it. All business logic lives in
/// IPaymentService, already tested against real PostgreSQL; this page is a
/// thin form + list over it, same as every other admin screen so far.
/// </summary>
[Authorize(Roles = RoleNames.Admin + "," + RoleNames.SystemAdmin)]
public class PaymentsModel : PageModel
{
    private readonly MvTeachesDbContext _db;
    private readonly IPaymentService _payments;
    private readonly UserManager<ApplicationUser> _userManager;

    public PaymentsModel(MvTeachesDbContext db, IPaymentService payments, UserManager<ApplicationUser> userManager)
    {
        _db = db;
        _payments = payments;
        _userManager = userManager;
    }

    public record PaymentRow(long Id, string StudentName, decimal Amount, string Currency, PaymentMethod Method,
        PaymentStatus Status, string ReferenceCode, string? RejectionReason);

    public record DraftSubscriptionRow(long Id, string StudentName, decimal Amount, string Currency);

    public IReadOnlyList<PaymentRow> PendingPayments { get; set; } = Array.Empty<PaymentRow>();
    public IReadOnlyList<PaymentRow> RecentPayments { get; set; } = Array.Empty<PaymentRow>();
    // Fully qualified to avoid ambiguity with the sibling MVTeaches.Web.Pages.Student namespace.
    public IReadOnlyList<MVTeaches.Domain.People.Student> Students { get; set; } = Array.Empty<MVTeaches.Domain.People.Student>();

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
        [Required]
        public long StudentId { get; set; }

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

        var request = new RecordPaymentRequest(NewPayment.StudentId, NewPayment.SubscriptionId, PayerUserId: null,
            new Money(NewPayment.Amount, NewPayment.Currency), NewPayment.Method, ProofFileId: null);
        var result = await _payments.RecordManualPaymentAsync(request, HttpContext.RequestAborted);
        StatusMessage = $"Payment recorded — reference {result.ReferenceCode}, awaiting confirmation.";

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostConfirmAsync(long paymentId)
    {
        var confirmedByUserId = long.Parse(_userManager.GetUserId(User)!);
        var result = await _payments.ConfirmAsync(paymentId, confirmedByUserId, HttpContext.RequestAborted);

        StatusMessage = result.Outcome switch
        {
            ConfirmPaymentOutcome.Confirmed => "Payment confirmed.",
            ConfirmPaymentOutcome.AlreadyConfirmed => "This payment was already confirmed.",
            _ => null,
        };
        ErrorMessage = result.Outcome switch
        {
            ConfirmPaymentOutcome.NotFound => "Payment not found.",
            ConfirmPaymentOutcome.NotPending => "This payment is no longer pending (already rejected).",
            _ => null,
        };

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostRejectAsync(long paymentId)
    {
        var rejectedByUserId = long.Parse(_userManager.GetUserId(User)!);
        var reason = string.IsNullOrWhiteSpace(RejectReason) ? "No reason given" : RejectReason;

        try
        {
            await _payments.RejectAsync(paymentId, reason, rejectedByUserId, HttpContext.RequestAborted);
            StatusMessage = "Payment rejected.";
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
        Students = await _db.Students.OrderByDescending(s => s.Id).Take(200).ToListAsync();
        var studentNames = Students.ToDictionary(s => s.Id, s => s.FullName);

        var drafts = await _db.Subscriptions
            .Where(s => s.Status == SubscriptionStatus.Draft)
            .OrderByDescending(s => s.Id)
            .Take(100)
            .ToListAsync();
        DraftSubscriptions = drafts.Select(s => new DraftSubscriptionRow(
            s.Id, studentNames.GetValueOrDefault(s.StudentId, $"#{s.StudentId}"), s.Price.Amount, s.Price.Currency)).ToList();

        var pending = await _db.Payments
            .Where(p => p.Status == PaymentStatus.Pending)
            .OrderBy(p => p.CreatedAtUtc)
            .ToListAsync();
        PendingPayments = pending.Select(p => ToRow(p, studentNames)).ToList();

        var recent = await _db.Payments
            .Where(p => p.Status != PaymentStatus.Pending)
            .OrderByDescending(p => p.Id)
            .Take(50)
            .ToListAsync();
        RecentPayments = recent.Select(p => ToRow(p, studentNames)).ToList();
    }

    private static PaymentRow ToRow(Payment p, IReadOnlyDictionary<long, string> studentNames) =>
        new(p.Id, studentNames.GetValueOrDefault(p.StudentId, $"#{p.StudentId}"), p.Amount.Amount, p.Amount.Currency,
            p.Method, p.Status, p.ReferenceCode, p.RejectionReason);
}
