using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using MVTeaches.Application.Files;
using MVTeaches.Application.Payments;
using MVTeaches.Application.People;
using MVTeaches.Application.Subscriptions;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Common;
using MVTeaches.Domain.Payments;
using MVTeaches.Domain.People;
using MVTeaches.Domain.Subscriptions;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using MVTeaches.Web.Resources;
using NodaTime;

namespace MVTeaches.Web.Pages.Admin;

/// <summary>
/// Owner decision 2026-08-30 (assisted registration &amp; purchase): serves
/// the guardian who calls in and cannot do the steps online themselves —
/// "تسجيل وشراء بمساعدة الإدارة". This is NOT a shortcut around the real
/// placement test, and NOT the free AdminGrant path (D-13): it is a real,
/// paid registration where an admin keys in what the guardian reports over
/// the phone, using the exact same server-side rules a self-service
/// purchase already goes through.
///
/// Deliberately drives ONE correct sequence, with no shortcut anywhere in
/// it: search-or-create the guardian and student (never duplicated),
/// link them, point the student at their OWN real placement test (this
/// page cannot take it for them — IPlacementAttemptService's own IDOR guard
/// already refuses any caller who isn't the student or their guardian, and
/// nothing here works around that), only THEN offer packages matching the
/// level the exam actually produced, purchase via the same
/// ISubscriptionService.PurchaseFromPlanAsync every self-service purchase
/// uses (never an admin-chosen level), and finally record the transfer the
/// guardian reports and let the admin confirm it once the money is
/// actually seen in the account (/Admin/Payments does the confirming — this
/// page only records what the guardian reports).
///
/// This page's own pre-existing sibling, /Admin/Students, still owns the
/// SEPARATE, legitimate "override this student's level for cause" action
/// (IStudentAdmissionService.AssignLevelAsync, mandatory reason,
/// audit-logged) — that capability is untouched and unexpanded; it is
/// simply never surfaced or suggested anywhere in THIS page's own flow,
/// since an onboarding registration is not a correction.
///
/// Security review 2026-09-03 (Review Required — Authorization), Stage 2:
/// gated on Admin.Students.Manage for its GET as well as every POST, unlike
/// Students/StudentDetails which split View (read) from Manage (write) —
/// every handler here, including the draft-package purchase and the manual
/// payment/transfer steps embedded in this one guided flow, exists only to
/// carry a new family through registration, so there is no meaningful
/// "view this page but change nothing" state to offer a View-only Admin. A
/// single page-level policy therefore already protects every handler
/// (Razor Pages applies [Authorize] to the whole page, GET and POST alike —
/// see PageModelPermissionExtensions' own remarks), and no
/// RequirePermissionAsync calls are needed on individual handlers.
/// </summary>
[Authorize(Roles = RoleNames.Admin + "," + RoleNames.SystemAdmin)]
[Authorize(Policy = PermissionKeys.StudentsManage)]
public class AssistedRegistrationModel : PageModel
{
    private readonly MvTeachesDbContext _db;
    private readonly IStudentAdmissionService _admissions;
    private readonly ISubscriptionService _subscriptions;
    private readonly IPaymentService _payments;
    private readonly IPaymentMethodConfigService _methods;
    private readonly IFileStorageService _files;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public AssistedRegistrationModel(MvTeachesDbContext db, IStudentAdmissionService admissions,
        ISubscriptionService subscriptions, IPaymentService payments, IPaymentMethodConfigService methods,
        IFileStorageService files, UserManager<ApplicationUser> userManager, IStringLocalizer<SharedResource> localizer)
    {
        _db = db;
        _admissions = admissions;
        _subscriptions = subscriptions;
        _payments = payments;
        _methods = methods;
        _files = files;
        _userManager = userManager;
        _localizer = localizer;
    }

    public record GuardianSearchRow(long Id, string FullName, string? Email);
    public record StudentSearchRow(long Id, string FullName, StudentStatus Status);
    public record PlanRow(long Id, string CourseName, string LevelCode, SessionType SessionType,
        int SessionsCount, int MinutesTotal, decimal Amount, string Currency);
    public record PaymentRow(long Id, decimal Amount, string Currency, PaymentMethod Method, PaymentStatus Status,
        string ReferenceCode, bool HasSubmittedTransferDetails, decimal? ReceivedAmount, string? ReceivedCurrency);

    /// <summary>Owner decision 2026-08-30 (shortfall/top-up policy): shown so
    /// the admin can see exactly how much is still owed before keying in a
    /// supplementary manual payment for the same subscription.</summary>
    public record DraftSubscriptionRow(long Id, decimal Price, string Currency, decimal ConfirmedReceived, decimal RemainingOwed);

    public IReadOnlyList<GuardianSearchRow> GuardianResults { get; set; } = Array.Empty<GuardianSearchRow>();
    public IReadOnlyList<StudentSearchRow> StudentResults { get; set; } = Array.Empty<StudentSearchRow>();
    public IReadOnlyList<PaymentMethodConfig> ActiveMethods { get; set; } = Array.Empty<PaymentMethodConfig>();

    /// <summary>Everything the pickers on this page need. The screen used to
    /// ask the admin to type a raw "Guardian id" / "Student id" / "Country id"
    /// into a number box — three chances to key in a number that belongs to
    /// somebody else entirely. They are named lists now; no internal id is
    /// ever shown or typed.</summary>
    public IReadOnlyList<Country> Countries { get; set; } = Array.Empty<Country>();
    public IReadOnlyList<GuardianSearchRow> AllGuardians { get; set; } = Array.Empty<GuardianSearchRow>();
    public IReadOnlyList<StudentSearchRow> AllStudents { get; set; } = Array.Empty<StudentSearchRow>();

    /// <summary>Which step of the sequence this admin is actually on, derived
    /// from the state of the chosen student — never stored, never a wizard
    /// session. Display only: every step remains reachable and the server
    /// still enforces the real order (no level, no package).</summary>
    public int CurrentStep { get; set; } = 1;

    public bool IsArabic => System.Globalization.CultureInfo.CurrentUICulture
        .TwoLetterISOLanguageName.Equals("ar", StringComparison.OrdinalIgnoreCase);

    public bool SelectedStudentHasGuardian => SelectedStudentGuardianNames.Count > 0;
    public bool SelectedStudentHasPackage { get; set; }

    public long? SelectedStudentId { get; set; }
    public string? SelectedStudentName { get; set; }
    public IReadOnlyList<string> SelectedStudentGuardianNames { get; set; } = Array.Empty<string>();
    public bool SelectedStudentHasLevel { get; set; }
    public string? SelectedStudentLevelCode { get; set; }
    public IReadOnlyList<PlanRow> EligiblePlans { get; set; } = Array.Empty<PlanRow>();
    public IReadOnlyList<PaymentRow> StudentPayments { get; set; } = Array.Empty<PaymentRow>();
    public IReadOnlyList<DraftSubscriptionRow> SelectedStudentDraftSubscriptions { get; set; } = Array.Empty<DraftSubscriptionRow>();

    [BindProperty]
    public SearchInput Search { get; set; } = new();

    [BindProperty]
    public NewGuardianInput NewGuardian { get; set; } = new();

    [BindProperty]
    public NewStudentInput NewStudent { get; set; } = new();

    [BindProperty]
    public LinkInput Link { get; set; } = new();

    [BindProperty]
    public PurchaseInput Purchase { get; set; } = new();

    [BindProperty]
    public TransferInput Transfer { get; set; } = new();

    public string? StatusMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public class SearchInput
    {
        public string? Query { get; set; }
    }

    public class NewGuardianInput
    {
        [Required(ErrorMessage = "Enter an email address."), EmailAddress(ErrorMessage = "This is not a valid email address.")]
        public string Email { get; set; } = string.Empty;

        [Required(ErrorMessage = "Enter a temporary password."), MinLength(8, ErrorMessage = "The password must be at least 8 characters.")]
        public string Password { get; set; } = string.Empty;

        [Required(ErrorMessage = "Enter the full name.")] public string FullName { get; set; } = string.Empty;
    }

    // Ids and dates are nullable on purpose: on a non-nullable value type an
    // untouched picker posts "", the binding error is dropped by
    // ModelState.Clear(), and [Required] then passes on the defaulted 0 /
    // 0001-01-01. Same fix already documented on /Admin/Students.
    public class NewStudentInput
    {
        [Required(ErrorMessage = "Choose a country.")] public int? CountryId { get; set; }
        [Required(ErrorMessage = "Enter the full name.")] public string FullName { get; set; } = string.Empty;
        [Required(ErrorMessage = "Enter the date of birth.")] public DateOnly? DateOfBirth { get; set; }
    }

    public class LinkInput
    {
        [Required(ErrorMessage = "Choose a guardian.")] public long? GuardianId { get; set; }
        [Required(ErrorMessage = "Choose a student.")] public long? StudentId { get; set; }
        [Required] public GuardianRelationship Relationship { get; set; }
        public bool IsPrimary { get; set; }
    }

    public class PurchaseInput
    {
        [Required(ErrorMessage = "Choose a student.")] public long StudentId { get; set; }
        [Required(ErrorMessage = "Choose a pricing plan.")] public long PricingPlanId { get; set; }
    }

    public class TransferInput
    {
        [Required] public long PaymentId { get; set; }
        public long? PaymentMethodConfigId { get; set; }
        public string? PayerDisplayName { get; set; }
        public DateOnly? TransferDate { get; set; }
        public string? BankReferenceNumber { get; set; }
        public IFormFile? Receipt { get; set; }
    }

    public async Task OnGetAsync(long? studentId, string? q)
    {
        Search.Query = q;
        await LoadAsync(studentId, q);
    }

    public async Task<IActionResult> OnPostSearchAsync()
    {
        await LoadAsync(null, Search.Query);
        return Page();
    }

    public async Task<IActionResult> OnPostRegisterGuardianAsync()
    {
        ModelState.Clear();
        if (!TryValidateModel(NewGuardian, nameof(NewGuardian)))
        {
            await LoadAsync(null, Search.Query);
            return Page();
        }

        var result = await _admissions.RegisterGuardianAsync(NewGuardian.Email, NewGuardian.Password, NewGuardian.FullName, HttpContext.RequestAborted);
        ErrorMessage = result.Outcome == RegisterGuardianOutcome.LoginFailed
            ? _localizer["Could not create the guardian's account: {0}", string.Join("; ", result.Errors ?? Array.Empty<string>())].Value
            : null;
        StatusMessage = result.Outcome == RegisterGuardianOutcome.Registered ? _localizer["Guardian registered."].Value : null;

        await LoadAsync(null, NewGuardian.FullName);
        return Page();
    }

    public async Task<IActionResult> OnPostRegisterStudentAsync()
    {
        ModelState.Clear();
        if (!TryValidateModel(NewStudent, nameof(NewStudent)))
        {
            await LoadAsync(null, Search.Query);
            return Page();
        }

        var dateOfBirth = NewStudent.DateOfBirth!.Value;
        var dob = new LocalDate(dateOfBirth.Year, dateOfBirth.Month, dateOfBirth.Day);
        // No login/password here — this student registers with no independent
        // account yet, exactly the ordinary guardian-only-child case; a login
        // can be added later from /Admin/Students if the family wants one.
        var result = await _admissions.RegisterStudentAsync(NewStudent.CountryId!.Value, NewStudent.FullName, dob,
            loginEmail: null, loginPassword: null, HttpContext.RequestAborted);

        StatusMessage = result.Outcome == RegisterStudentOutcome.Registered
            ? _localizer["Student '{0}' registered — link a guardian, then direct them to the free placement test.", NewStudent.FullName].Value
            : null;

        await LoadAsync(result.StudentId, null);
        return Page();
    }

    public async Task<IActionResult> OnPostLinkAsync()
    {
        ModelState.Clear();
        if (!TryValidateModel(Link, nameof(Link)))
        {
            await LoadAsync(Link.StudentId, null);
            return Page();
        }

        var actingUserId = GetCurrentUserId();
        var result = await _admissions.LinkGuardianAsync(Link.GuardianId!.Value, Link.StudentId!.Value, Link.Relationship, Link.IsPrimary, actingUserId, HttpContext.RequestAborted);
        ErrorMessage = result.Outcome switch
        {
            LinkGuardianOutcome.PrimaryConflict => _localizer["This student already has a primary guardian."].Value,
            LinkGuardianOutcome.AlreadyLinked => _localizer["This guardian is already linked to this student."].Value,
            _ => null,
        };
        StatusMessage = result.Outcome == LinkGuardianOutcome.Linked ? _localizer["Guardian linked."].Value : null;

        await LoadAsync(Link.StudentId, null);
        return Page();
    }

    public async Task<IActionResult> OnPostPurchaseAsync()
    {
        ModelState.Clear();
        if (!TryValidateModel(Purchase, nameof(Purchase)))
        {
            await LoadAsync(Purchase.StudentId, null);
            return Page();
        }

        var actingUserId = GetCurrentUserId();
        // Reuses the SAME level-derived-from-the-student's-own-current-level
        // check the self-service /PurchasePackage flow already goes through
        // (isAdminInitiated only skips the self/guardian IDOR check — it
        // never skips the level/session-type match, and there is no
        // levelId parameter anywhere for this page to supply one on its own).
        var result = await _subscriptions.PurchaseFromPlanAsync(Purchase.StudentId, Purchase.PricingPlanId,
            actingUserId, SubscriptionOrigin.GuardianPurchase, isAdminInitiated: true, HttpContext.RequestAborted);

        if (result.Outcome == PurchaseFromPlanOutcome.Purchased)
        {
            StatusMessage = _localizer["Subscription #{0} created as Draft ({1}) — record the guardian's transfer below once they send it.", result.SubscriptionId!, result.Price!];
        }
        else
        {
            ErrorMessage = result.Outcome switch
            {
                PurchaseFromPlanOutcome.PlanNotFound => _localizer["Package not found."],
                PurchaseFromPlanOutcome.PlanNotPublishedForAnyLevel => _localizer["This package is no longer available."],
                PurchaseFromPlanOutcome.StudentHasNoAssignedLevel => _localizer["A placement result is required before purchasing a package."],
                PurchaseFromPlanOutcome.LevelMismatch => _localizer["This package no longer matches the student's current level."],
                _ => _localizer["Could not record this purchase."],
            };
        }

        await LoadAsync(Purchase.StudentId, null);
        return Page();
    }

    public async Task<IActionResult> OnPostRecordManualPaymentAsync(long studentId, long subscriptionId, decimal amount, string currency, PaymentMethod method, long? paymentMethodConfigId)
    {
        // This handler's own fields are plain parameters, not a [BindProperty]
        // Input class, so there is nothing of its own to re-validate — but
        // ASP.NET Core still binds AND validates every OTHER [BindProperty]
        // model on the page (NewGuardian/NewStudent/Link/Purchase) on every
        // POST, regardless of which handler is invoked. Without clearing that
        // leftover state, submitting this step-5 payment form showed every
        // required-field error from steps 2-4's empty forms ("Enter an email
        // address", "Choose a country", "Choose the guardian" ...) even though
        // none of those forms were touched. Same class of bug as every other
        // handler on this page — see OnPostRegisterGuardianAsync's own remarks.
        ModelState.Clear();

        var request = new RecordPaymentRequest(studentId, subscriptionId, PayerUserId: null,
            new Money(amount, currency), method, ProofFileId: null, paymentMethodConfigId);
        var result = await _payments.RecordManualPaymentAsync(request, HttpContext.RequestAborted);
        StatusMessage = _localizer["Payment request of {0} {1} recorded — reference {2}. Enter the guardian's transfer details below once they send it.",
            amount, currency, result.ReferenceCode];

        await LoadAsync(studentId, null);
        return Page();
    }

    public async Task<IActionResult> OnPostSubmitTransferAsync()
    {
        // Same fix as OnPostRecordManualPaymentAsync above: clear the OTHER
        // bound models' leftover validation errors first, then validate only
        // Transfer's own fields (today just PaymentId, always populated from
        // the hidden field, but this keeps the handler correct if a required
        // field is ever added to TransferInput).
        ModelState.Clear();
        if (!TryValidateModel(Transfer, nameof(Transfer)))
        {
            var paymentForReload = await _db.Payments.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == Transfer.PaymentId, HttpContext.RequestAborted);
            await LoadAsync(paymentForReload?.StudentId, null);
            return Page();
        }

        var actingUserId = GetCurrentUserId();
        long? receiptFileId = null;

        if (Transfer.Receipt is not null && Transfer.Receipt.Length > 0)
        {
            await using var stream = Transfer.Receipt.OpenReadStream();
            var payment = await _db.Payments.FirstOrDefaultAsync(p => p.Id == Transfer.PaymentId, HttpContext.RequestAborted);
            var uploadResult = await _files.SaveAsync(stream, nameof(MVTeaches.Domain.Files.FilePurpose.PaymentProof),
                Transfer.Receipt.FileName, actingUserId, HttpContext.RequestAborted, payment?.StudentId);

            if (uploadResult.Outcome != SaveUploadOutcome.Saved)
            {
                ErrorMessage = uploadResult.Outcome switch
                {
                    SaveUploadOutcome.RejectedContentType => _localizer["The receipt must be a JPEG, PNG, or PDF file."],
                    SaveUploadOutcome.RejectedTooLarge => _localizer["The receipt file is too large."],
                    _ => _localizer["The receipt could not be uploaded."],
                };
                await LoadAsync(payment?.StudentId, null);
                return Page();
            }

            receiptFileId = uploadResult.DocumentId;
        }

        var transferDate = Transfer.TransferDate.HasValue
            ? new LocalDate(Transfer.TransferDate.Value.Year, Transfer.TransferDate.Value.Month, Transfer.TransferDate.Value.Day)
            : (LocalDate?)null;

        var attachResult = await _payments.AttachTransferDetailsAsync(Transfer.PaymentId, actingUserId, isAdminInitiated: true,
            Transfer.PayerDisplayName, transferDate, Transfer.BankReferenceNumber, receiptFileId, HttpContext.RequestAborted);

        // Used to name the raw path "/Admin/Payments" — an internal route
        // shown to the reader, exactly the kind of technical text the rest
        // of this pass removes elsewhere.
        StatusMessage = attachResult.Outcome == AttachTransferDetailsOutcome.Attached
            ? _localizer["Transfer details recorded. An admin will confirm it on the payments screen once the money is seen in the account."].Value
            : null;
        ErrorMessage = attachResult.Outcome switch
        {
            AttachTransferDetailsOutcome.NotFound => _localizer["Payment not found."].Value,
            AttachTransferDetailsOutcome.NotPending => _localizer["This payment is no longer pending."].Value,
            AttachTransferDetailsOutcome.DuplicateReference => _localizer["This bank reference looks like it was already used for another payment — double check with the guardian."].Value,
            _ => null,
        };

        var reloadedPayment = await _db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.Id == Transfer.PaymentId, HttpContext.RequestAborted);
        await LoadAsync(reloadedPayment?.StudentId, null);
        return Page();
    }

    private long GetCurrentUserId() => long.Parse(_userManager.GetUserId(User)!);

    private async Task LoadAsync(long? studentId, string? query)
    {
        ActiveMethods = await _methods.ListActiveAsync(HttpContext.RequestAborted);

        Countries = await _db.Countries.Where(c => c.IsActive).OrderBy(c => c.Id).ToListAsync(HttpContext.RequestAborted);
        AllGuardians = await _db.Guardians
            .OrderBy(g => g.FullName)
            .Take(300)
            .Select(g => new GuardianSearchRow(g.Id, g.FullName, null))
            .ToListAsync(HttpContext.RequestAborted);
        AllStudents = await _db.Students
            .OrderBy(st => st.FullName)
            .Take(300)
            .Select(st => new StudentSearchRow(st.Id, st.FullName, st.Status))
            .ToListAsync(HttpContext.RequestAborted);

        if (!string.IsNullOrWhiteSpace(query))
        {
            GuardianResults = await _db.Guardians
                .Join(_db.Users, g => g.UserId, u => u.Id, (g, u) => new { g.Id, g.FullName, u.Email })
                .Where(x => EF.Functions.ILike(x.FullName, $"%{query}%") || (x.Email != null && EF.Functions.ILike(x.Email, $"%{query}%")))
                .Take(20)
                .Select(x => new GuardianSearchRow(x.Id, x.FullName, x.Email))
                .ToListAsync(HttpContext.RequestAborted);

            StudentResults = await _db.Students
                .Where(s => EF.Functions.ILike(s.FullName, $"%{query}%"))
                .Take(20)
                .Select(s => new StudentSearchRow(s.Id, s.FullName, s.Status))
                .ToListAsync(HttpContext.RequestAborted);
        }

        if (studentId is null)
        {
            return;
        }

        var student = await _db.Students.FirstOrDefaultAsync(s => s.Id == studentId, HttpContext.RequestAborted);
        if (student is null)
        {
            return;
        }

        SelectedStudentId = student.Id;
        SelectedStudentName = student.FullName;

        var guardianIds = await _db.Guardianships.Where(g => g.StudentId == studentId).Select(g => g.GuardianId).ToListAsync(HttpContext.RequestAborted);
        SelectedStudentGuardianNames = await _db.Guardians.Where(g => guardianIds.Contains(g.Id)).Select(g => g.FullName).ToListAsync(HttpContext.RequestAborted);

        // Deliberately a direct, read-only query — never through
        // IPlacementAttemptService, whose own IDOR guard correctly refuses
        // any caller that isn't the student or their guardian, and stays
        // that way: an admin only ever OBSERVES the result here, never acts
        // on the student's own attempt.
        var currentLevelId = await _db.StudentLevels
            .Where(l => l.StudentId == studentId && l.IsCurrent)
            .Select(l => (int?)l.LevelId)
            .FirstOrDefaultAsync(HttpContext.RequestAborted);
        SelectedStudentHasLevel = currentLevelId is not null;

        if (currentLevelId is not null)
        {
            var level = await _db.Levels.FirstOrDefaultAsync(l => l.Id == currentLevelId.Value, HttpContext.RequestAborted);
            SelectedStudentLevelCode = level?.Code;

            var courseNames = await _db.Courses.ToDictionaryAsync(c => c.Id, c => c.NameEn, HttpContext.RequestAborted);
            var plans = await _db.PricingPlans
                .Where(p => p.IsActive && p.LevelId == currentLevelId.Value)
                .ToListAsync(HttpContext.RequestAborted);
            EligiblePlans = plans.Select(p => new PlanRow(p.Id, courseNames.GetValueOrDefault(p.CourseId, "?"),
                SelectedStudentLevelCode ?? "?", p.SessionType, p.SessionsCount, p.MinutesTotal, p.Amount.Amount, p.Amount.Currency)).ToList();
        }

        var payments = await _db.Payments.Where(p => p.StudentId == studentId).OrderByDescending(p => p.Id).ToListAsync(HttpContext.RequestAborted);
        StudentPayments = payments.Select(p => new PaymentRow(p.Id, p.Amount.Amount, p.Amount.Currency, p.Method, p.Status,
            p.ReferenceCode, p.HasSubmittedTransferDetails, p.ReceivedAmount, p.ReceivedCurrency)).ToList();

        // Owner decision 2026-08-30 (shortfall/top-up policy): every Draft
        // subscription for this student, whether it has never had a payment
        // yet or already has a Confirmed-but-short one — the admin needs to
        // see the remaining balance either way before keying in the next
        // (first or supplementary) manual payment below.
        var draftSubscriptionIds = await _db.Subscriptions
            .Where(s => s.StudentId == studentId && s.Status == SubscriptionStatus.Draft)
            .Select(s => s.Id)
            .ToListAsync(HttpContext.RequestAborted);
        var draftRows = new List<DraftSubscriptionRow>();
        foreach (var id in draftSubscriptionIds)
        {
            var funding = await _payments.GetSubscriptionFundingStatusAsync(id, HttpContext.RequestAborted);
            draftRows.Add(new DraftSubscriptionRow(id, funding.Price.Amount, funding.Price.Currency,
                funding.ConfirmedReceived, funding.RemainingOwed.Amount));
        }
        SelectedStudentDraftSubscriptions = draftRows;

        SelectedStudentHasPackage = await _db.Subscriptions
            .AnyAsync(sub => sub.StudentId == studentId, HttpContext.RequestAborted);

        // Where the admin actually stands, read from real state rather than a
        // remembered wizard position — so refreshing, or coming back tomorrow,
        // lands on the same step.
        CurrentStep = !SelectedStudentHasGuardian ? 3
            : !SelectedStudentHasLevel ? 4
            : 5;

        Link.StudentId ??= student.Id;
    }
}
