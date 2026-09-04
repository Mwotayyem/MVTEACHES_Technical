using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using MVTeaches.Application.People;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.People;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using MVTeaches.Web.Display;
using MVTeaches.Web.Identity;
using MVTeaches.Web.Resources;
using MVTeaches.Application.Ledger;
using MVTeaches.Domain.Payments;
using MVTeaches.Domain.Subscriptions;
using MVTeaches.Domain.Scheduling;
using NodaTime;

namespace MVTeaches.Web.Pages.Admin;

/// <summary>
/// §14 — the first slice of the Student/Guardian register. Real self-registration
/// is phone+OTP via WhatsApp (§7), which is genuinely blocked (see
/// docs/deployment/STATUS.md) — this page is the honest interim: an admin enters
/// what staff already collect over the phone, driving the exact same Student/
/// Guardian/Guardianship/StudentLevel domain state machines. Deliberately no
/// edit/delete yet — only the forward moves the state machines themselves allow.
///
/// UI pass: every id the admin used to type by hand is now a named picker, and
/// the required ids/dates are nullable so [Required] actually fires. Before
/// this, an untouched &lt;select&gt; posted an empty string, ModelState.Clear()
/// wiped the binding error, [Required] passed on a non-nullable long that had
/// defaulted to 0, and the service was called with student id 0 (or a date of
/// birth of 0001-01-01). Same fix and same reasoning as the one already
/// documented in Teacher/PublishSlots.cshtml.cs.
/// </summary>
[Authorize(Roles = RoleNames.Admin + "," + RoleNames.SystemAdmin)]
[Authorize(Policy = PermissionKeys.StudentsView)]
public class StudentsModel : PageModel
{
    private readonly MvTeachesDbContext _db;
    private readonly IStudentAdmissionService _admissions;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly IEntitlementBalanceQuery _balances;
    private readonly IClock _clock;
    private readonly IAuthorizationService _authorizationService;

    public StudentsModel(MvTeachesDbContext db, IStudentAdmissionService admissions, UserManager<ApplicationUser> userManager,
        IStringLocalizer<SharedResource> localizer, IEntitlementBalanceQuery balances, IClock clock,
        IAuthorizationService authorizationService)
    {
        _db = db;
        _admissions = admissions;
        _userManager = userManager;
        _localizer = localizer;
        _balances = balances;
        _clock = clock;
        _authorizationService = authorizationService;
    }

    /// <summary>One line of the register. Everything after Guardians is
    /// there so the list can be READ instead of opened: what they are on, how
    /// far through it they are, what is still owed, and when it ends. All of
    /// it is derived from rows already loaded below - no stored summary.</summary>
    /// <summary>Owner decision 2026-09-04: the register carries each guardian's
    /// ID as well as their name, because unlinking a wrongly-attached guardian
    /// needs to say WHICH one. Names alone were enough only while there was no
    /// way to correct the link.</summary>
    public record GuardianLink(long GuardianId, string FullName);

    public record StudentRow(long Id, string FullName, string CountryName, StudentStatus Status,
        string? CurrentLevelCode, IReadOnlyList<StudentsModel.GuardianLink> Guardians,
        StudentLifecycleState State, string? PackageName, string? Currency,
        decimal Billed, decimal Paid, decimal Outstanding, int RemainingMinutes, int PurchasedMinutes,
        LocalDate? StartsOn, LocalDate? ExpiresOn, int UpcomingLessonCount,
        bool HasReachablePhone)
    {
        // Outstanding comes from MoneyStanding as a sum of PER-SUBSCRIPTION
        // shortfalls, each clamped at zero before adding up — never re-derived
        // here as Billed minus Paid, which could let one open subscription's
        // overpayment silently cancel out a different one's real shortfall.
        public int PaidPercent => Billed <= 0m ? 100
            : (int)Math.Round(Math.Clamp((double)(Paid / Billed) * 100d, 0d, 100d));

        public int UsedPercent => PurchasedMinutes <= 0 ? 0
            : (int)Math.Round(Math.Clamp((PurchasedMinutes - RemainingMinutes) * 100d / PurchasedMinutes, 0d, 100d));

        public bool NeedsAttention => StudentLifecycle.NeedsAttention(State);
    }

    public IReadOnlyList<StudentRow> Students { get; set; } = Array.Empty<StudentRow>();

    /// <summary>How many students sit in each state, for the filter chips.
    /// Counted from <see cref="Students"/> itself so a chip can never claim a
    /// number the list below does not show.</summary>
    public IReadOnlyDictionary<StudentLifecycleState, int> StateCounts { get; set; } =
        new Dictionary<StudentLifecycleState, int>();

    /// <summary>Display filter only - it hides rows, it changes nothing.</summary>
    [BindProperty(SupportsGet = true, Name = "state")]
    public string? StateFilter { get; set; }

    public IReadOnlyList<StudentRow> VisibleStudents =>
        Enum.TryParse<StudentLifecycleState>(StateFilter, ignoreCase: true, out var wanted)
            ? Students.Where(s => s.State == wanted).ToList()
            : Students;
    // Fully qualified to avoid ambiguity with the sibling MVTeaches.Web.Pages.Guardian namespace.
    public IReadOnlyList<MVTeaches.Domain.People.Guardian> Guardians { get; set; } = Array.Empty<MVTeaches.Domain.People.Guardian>();
    public IReadOnlyList<Country> Countries { get; set; } = Array.Empty<Country>();
    public IReadOnlyList<Level> Levels { get; set; } = Array.Empty<Level>();

    [BindProperty]
    public RegisterGuardianInput NewGuardian { get; set; } = new();

    [BindProperty]
    public RegisterStudentInput NewStudent { get; set; } = new();

    [BindProperty]
    public LinkGuardianInput Link { get; set; } = new();

    [BindProperty]
    public AssignLevelInput LevelAssignment { get; set; } = new();

    public string? StatusMessage { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>Set when the admin clicked "set the level now" / "link a
    /// guardian" on one row: the correction forms further down open with
    /// that student already chosen, so the admin never re-finds the name in
    /// a 200-row picker. Display convenience only — the posted student id is
    /// still whatever the form itself carries.</summary>
    [BindProperty(SupportsGet = true, Name = "studentId")]
    public long? FocusStudentId { get; set; }

    public string? FocusStudentName { get; set; }

    public bool IsArabic => System.Globalization.CultureInfo.CurrentUICulture
        .TwoLetterISOLanguageName.Equals("ar", StringComparison.OrdinalIgnoreCase);

    public string DisplayCountry(Country country) => IsArabic ? country.NameAr : country.NameEn;
    public string DisplayLevel(Level level) => IsArabic ? level.NameAr : level.NameEn;

    /// <summary>A picker label: the student's own name plus what an admin needs
    /// to tell two similar names apart — never the internal row id.</summary>
    public string PickerLabel(StudentRow student)
    {
        var level = student.CurrentLevelCode ?? _localizer["No level"].Value;
        var status = _localizer["StudentStatus." + student.Status].Value;
        return $"{student.FullName} — {level} — {status}";
    }

    public class RegisterGuardianInput
    {
        [Required(ErrorMessage = "Enter an email address."), EmailAddress(ErrorMessage = "This is not a valid email address.")]
        public string Email { get; set; } = string.Empty;

        [Required(ErrorMessage = "Enter a temporary password."), MinLength(8, ErrorMessage = "The password must be at least 8 characters.")]
        public string Password { get; set; } = string.Empty;

        [Required(ErrorMessage = "Enter the full name.")]
        public string FullName { get; set; } = string.Empty;

        /// <summary>Owner decision 2026-09-04: the centre must be able to reach
        /// the person responsible for a child. Stored on this guardian's own
        /// Identity user (AspNetUsers.PhoneNumber) - no schema change.</summary>
        [Required(ErrorMessage = "Enter a phone number."), Phone(ErrorMessage = "This is not a valid phone number.")]
        public string PhoneNumber { get; set; } = string.Empty;
    }

    public class RegisterStudentInput
    {
        [Required(ErrorMessage = "Choose a country.")]
        public int? CountryId { get; set; }

        [Required(ErrorMessage = "Enter the full name.")]
        public string FullName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Enter the date of birth.")]
        public DateOnly? DateOfBirth { get; set; }

        [EmailAddress]
        public string? LoginEmail { get; set; }

        [MinLength(8)]
        public string? LoginPassword { get; set; }

        /// <summary>Owner decision 2026-09-04: required only when this student
        /// is being given their OWN login, which is the independent-learner
        /// case the centre must be able to phone directly. A child registered
        /// without a login has no AspNetUsers row to store a number on, and a
        /// Student row has no phone column - for them the guardian's number is
        /// the one that counts, and the guardian form makes it mandatory.
        /// Enforced in OnPostRegisterStudentAsync, not by an attribute, because
        /// the rule is conditional on another field.</summary>
        [Phone(ErrorMessage = "This is not a valid phone number.")]
        public string? PhoneNumber { get; set; }
    }

    public class LinkGuardianInput
    {
        [Required(ErrorMessage = "Choose a guardian.")]
        public long? GuardianId { get; set; }

        [Required(ErrorMessage = "Choose a student.")]
        public long? StudentId { get; set; }

        [Required]
        public GuardianRelationship Relationship { get; set; }

        public bool IsPrimary { get; set; }
    }

    /// <summary>Active courses, for the "which course is this level in?" picker.</summary>
    public IReadOnlyList<MVTeaches.Domain.Catalog.Course> Courses { get; set; } =
        Array.Empty<MVTeaches.Domain.Catalog.Course>();

    public class AssignLevelInput
    {
        [Required(ErrorMessage = "Choose a student.")]
        public long? StudentId { get; set; }

        /// <summary>Owner decision 2026-09-04: a level has no meaning without a
        /// course, so this is required rather than defaulted — a silent default
        /// would be exactly the wrong-course write the column exists to stop.</summary>
        [Required(ErrorMessage = "Choose a course.")]
        public long? CourseId { get; set; }

        [Required(ErrorMessage = "Choose a level.")]
        public int? LevelId { get; set; }

        [Required(ErrorMessage = "Write the reason for this decision.")]
        public string Reason { get; set; } = string.Empty;
    }

    public async Task OnGetAsync()
    {
        await LoadAsync();
        if (FocusStudentId is not null)
        {
            LevelAssignment.StudentId ??= FocusStudentId;
            Link.StudentId ??= FocusStudentId;
        }
    }

    public async Task<IActionResult> OnPostRegisterGuardianAsync()
    {
        if (await this.RequirePermissionAsync(_authorizationService, PermissionKeys.StudentsManage) is { } deny)
        {
            return deny;
        }

        // Every [BindProperty] group on this page is bound and auto-validated on
        // every POST regardless of which named handler is invoked (a well-known
        // Razor Pages multi-form gotcha) — clearing the whole slate before
        // re-validating just the relevant group is the only reliable way to
        // avoid failing on some OTHER form's empty, irrelevant required fields.
        ModelState.Clear();
        if (!TryValidateModel(NewGuardian, nameof(NewGuardian)))
        {
            await LoadAsync();
            return Page();
        }

        var result = await _admissions.RegisterGuardianAsync(NewGuardian.Email, NewGuardian.Password, NewGuardian.FullName,
            NewGuardian.PhoneNumber, HttpContext.RequestAborted);
        if (result.Outcome == RegisterGuardianOutcome.LoginFailed)
        {
            ErrorMessage = _localizer["Could not create the guardian's login: {0}", string.Join("; ", result.Errors ?? Array.Empty<string>())].Value;
        }
        else
        {
            StatusMessage = _localizer["Guardian '{0}' registered.", NewGuardian.FullName].Value;
        }

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostRegisterStudentAsync()
    {
        if (await this.RequirePermissionAsync(_authorizationService, PermissionKeys.StudentsManage) is { } deny)
        {
            return deny;
        }

        ModelState.Clear();
        if (!TryValidateModel(NewStudent, nameof(NewStudent)))
        {
            await LoadAsync();
            return Page();
        }

        // Owner decision 2026-09-04 (phone capture): a student getting their own
        // login is a person the centre deals with directly, so a number is
        // mandatory for them. Checked here rather than with an attribute
        // because it depends on whether a login is being created at all.
        var givingThemALogin = !string.IsNullOrWhiteSpace(NewStudent.LoginEmail)
            && !string.IsNullOrWhiteSpace(NewStudent.LoginPassword);
        if (givingThemALogin && string.IsNullOrWhiteSpace(NewStudent.PhoneNumber))
        {
            ErrorMessage = _localizer["A phone number is required for a student who gets their own login."].Value;
            await LoadAsync();
            return Page();
        }

        var dateOfBirth = NewStudent.DateOfBirth!.Value;
        var dob = new LocalDate(dateOfBirth.Year, dateOfBirth.Month, dateOfBirth.Day);
        var result = await _admissions.RegisterStudentAsync(NewStudent.CountryId!.Value, NewStudent.FullName, dob,
            NewStudent.LoginEmail, NewStudent.LoginPassword, NewStudent.PhoneNumber, HttpContext.RequestAborted);

        if (result.Outcome == RegisterStudentOutcome.LoginFailed)
        {
            ErrorMessage = _localizer["Could not create the student's login: {0}", string.Join("; ", result.Errors ?? Array.Empty<string>())].Value;
        }
        else
        {
            StatusMessage = _localizer["Student '{0}' registered (pending verification).", NewStudent.FullName].Value;
        }

        await LoadAsync();
        return Page();
    }

    /// <summary>Owner decision 2026-09-04: the way out of a wrong guardian link.
    /// Guarded by StudentsManage exactly like every other correction on this
    /// page — server-side, so hiding the button is a courtesy and this check is
    /// the rule. Removes the link and nothing else; see
    /// IStudentAdmissionService.UnlinkGuardianAsync for what survives.</summary>
    public async Task<IActionResult> OnPostUnlinkGuardianAsync(long guardianId, long studentId, string? unlinkReason)
    {
        if (await this.RequirePermissionAsync(_authorizationService, PermissionKeys.StudentsManage) is { } deny)
        {
            return deny;
        }

        // This handler's inputs arrive as route/form values rather than a bound
        // model, so the page-wide ModelState (populated by every OTHER form on
        // this page) has nothing to say about them.
        ModelState.Clear();

        var actingUserId = GetCurrentUserId();
        var result = await _admissions.UnlinkGuardianAsync(guardianId, studentId, actingUserId,
            unlinkReason ?? string.Empty, HttpContext.RequestAborted);

        ErrorMessage = result.Outcome switch
        {
            UnlinkGuardianOutcome.ReasonRequired =>
                _localizer["Write why this guardian is being unlinked — it is recorded against the change."].Value,
            UnlinkGuardianOutcome.NotLinked =>
                _localizer["That guardian is not linked to this student."].Value,
            _ => null,
        };
        if (result.Outcome == UnlinkGuardianOutcome.Unlinked)
        {
            StatusMessage = _localizer["Guardian unlinked. The student, their packages, payments and remaining hours are all unchanged — link the correct guardian now."].Value;
        }

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostLinkGuardianAsync()
    {
        if (await this.RequirePermissionAsync(_authorizationService, PermissionKeys.StudentsManage) is { } deny)
        {
            return deny;
        }

        ModelState.Clear();
        if (!TryValidateModel(Link, nameof(Link)))
        {
            await LoadAsync();
            return Page();
        }

        var actingUserId = GetCurrentUserId();
        var result = await _admissions.LinkGuardianAsync(Link.GuardianId!.Value, Link.StudentId!.Value, Link.Relationship,
            Link.IsPrimary, actingUserId, HttpContext.RequestAborted);

        ErrorMessage = result.Outcome switch
        {
            LinkGuardianOutcome.PrimaryConflict => _localizer["This student already has a primary guardian — un-primary the existing one first."].Value,
            LinkGuardianOutcome.AlreadyLinked => _localizer["This guardian is already linked to this student."].Value,
            // Owner decision 2026-09-04: one responsible guardian per student in
            // the MVP. There is no replace-the-guardian path yet, so the message
            // says so plainly rather than implying a self-service way around it.
            LinkGuardianOutcome.StudentAlreadyHasGuardian =>
                _localizer["This student already has a guardian. In this version a student may have only one guardian, and there is no way to replace one yet."].Value,
            _ => null,
        };
        if (result.Outcome == LinkGuardianOutcome.Linked)
        {
            StatusMessage = _localizer["Guardian linked."].Value;
        }

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostVerifyAsync(long studentId)
    {
        if (await this.RequirePermissionAsync(_authorizationService, PermissionKeys.StudentsManage) is { } deny)
        {
            return deny;
        }

        // This handler's inputs arrive as route/form values rather than a bound
        // model, so the page-wide ModelState holds nothing but the OTHER forms'
        // unfilled [Required] errors - which would otherwise be rendered on top
        // of this handler's own success message.
        ModelState.Clear();

        // A missing or unknown id is the only thing this handler can be wrong
        // about, so it says so itself. Without this, VerifyStudentAsync throws
        // "Student not found." and the admin gets a 500 page instead of a
        // sentence about the one field this action actually has.
        if (studentId <= 0 || !await _db.Students.AnyAsync(s => s.Id == studentId))
        {
            ErrorMessage = _localizer["Choose which student to confirm — that student was not found."].Value;
            await LoadAsync();
            return Page();
        }

        await _admissions.VerifyStudentAsync(studentId, HttpContext.RequestAborted);
        StatusMessage = _localizer["Student marked verified."].Value;
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAssignLevelAsync()
    {
        if (await this.RequirePermissionAsync(_authorizationService, PermissionKeys.StudentsManage) is { } deny)
        {
            return deny;
        }

        ModelState.Clear();
        if (!TryValidateModel(LevelAssignment, nameof(LevelAssignment)))
        {
            await LoadAsync();
            return Page();
        }

        var actingUserId = GetCurrentUserId();
        // Owner decision 2026-09-04 (multi-course levels): the admin picks the
        // course as well as the level. Only that course's current row is
        // superseded, so setting a Spanish level leaves an English one standing.
        await _admissions.AssignLevelAsync(LevelAssignment.StudentId!.Value, LevelAssignment.CourseId!.Value,
            LevelAssignment.LevelId!.Value, actingUserId, LevelAssignment.Reason, HttpContext.RequestAborted);
        StatusMessage = _localizer["Level assigned."].Value;
        await LoadAsync();
        return Page();
    }

    private long GetCurrentUserId() => long.Parse(_userManager.GetUserId(User)!);

    /// <summary>Owner decision 2026-09-04: a student's placements read as
    /// "English B2 · Spanish A1". Null when they have been placed in nothing,
    /// which the register renders as its own "awaiting a level" chip.</summary>
    private static string? LevelLabel(IReadOnlyList<MVTeaches.Domain.Placement.StudentLevel> held,
        IReadOnlyDictionary<long, string> courseNames, IReadOnlyDictionary<int, string> levelCodes)
    {
        if (held.Count == 0)
        {
            return null;
        }

        return string.Join(" · ", held
            .OrderBy(l => l.CourseId)
            .Select(l => $"{courseNames.GetValueOrDefault(l.CourseId, "?")} {levelCodes.GetValueOrDefault(l.LevelId, "?")}"));
    }

    private async Task LoadAsync()
    {
        Countries = await _db.Countries.Where(c => c.IsActive).OrderBy(c => c.NameEn).ToListAsync();
        Levels = await _db.Levels.Where(l => l.IsActive).OrderBy(l => l.SortOrder).ToListAsync();
        Guardians = await _db.Guardians.OrderBy(g => g.FullName).ToListAsync();
        // Owner decision 2026-09-04: the level form now asks which course.
        Courses = await _db.Courses.Where(c => c.IsActive).OrderBy(c => c.Id).ToListAsync();

        var students = await _db.Students
            .OrderByDescending(s => s.Id)
            .Take(200)
            .ToListAsync();

        var countryByI = Countries.ToDictionary(c => c.Id, DisplayCountry);
        var levelByI = Levels.ToDictionary(l => l.Id, l => l.Code);

        // Grouped, never ToDictionary: owner decision 2026-09-04 gives a student
        // one current level PER COURSE, so the student id alone is no longer a
        // unique key and ToDictionary would throw on the second course.
        var currentLevels = await _db.StudentLevels.Where(l => l.IsCurrent).ToListAsync();
        var currentLevelsByStudent = currentLevels.GroupBy(l => l.StudentId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var guardianships = await _db.Guardianships.ToListAsync();
        var guardianNamesByGuardianId = Guardians.ToDictionary(g => g.Id, g => g.FullName);
        var guardiansByStudent = guardianships
            .GroupBy(g => g.StudentId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<GuardianLink>)g
                .Select(x => new GuardianLink(x.GuardianId, guardianNamesByGuardianId.GetValueOrDefault(x.GuardianId, "?")))
                .ToList());

        // --- everything the register needs to be readable, in bulk reads ----
        var studentIds = students.Select(s => s.Id).ToList();
        var courseNames = await _db.Courses.ToDictionaryAsync(c => c.Id, c => c.NameEn);

        var subscriptions = await _db.Subscriptions
            .Where(sub => studentIds.Contains(sub.StudentId))
            .ToListAsync();
        // One read for every balance, using the same SUM(delta_minutes) the
        // single-subscription path uses (D-36) - never a stored counter.
        var balanceBySubscription = await _balances.GetSubscriptionBalancesAsync(
            subscriptions.Select(sub => sub.Id).ToList(), HttpContext.RequestAborted);

        var payments = await _db.Payments
            .Where(pay => studentIds.Contains(pay.StudentId))
            .ToListAsync();

        var now = _clock.GetCurrentInstant();
        var enrollments = await _db.SessionEnrollments
            .Where(e => studentIds.Contains(e.StudentId) && e.State == EnrollmentState.Active)
            .ToListAsync();
        var enrolledSessionIds = enrollments.Select(e => e.SessionId).Distinct().ToList();
        var sessionsById = await _db.ClassSessions
            .Where(cs => enrolledSessionIds.Contains(cs.Id))
            .ToDictionaryAsync(cs => cs.Id);
        var attendedStudentIds = (await _db.AttendanceRecords
                .Where(a => studentIds.Contains(a.StudentId) && a.IsPresent)
                .Select(a => a.StudentId)
                .Distinct()
                .ToListAsync())
            .ToHashSet();

        // Owner decision 2026-09-04 (phone capture): a number is mandatory for
        // every NEW registration, but the accounts already in the system predate
        // that and must not break — so they are flagged rather than blocked, and
        // an admin can see at a glance which families the centre cannot ring.
        // "Reachable" is deliberately generous: a number on the student's OWN
        // row, or on their login, or on any linked guardian's login satisfies
        // it — which is exactly the rule the registration forms enforce going
        // forward. Student.PhoneNumber (added 2026-09-04) is checked first
        // because it is the only one a child with no login can ever have.
        var studentsOwnPhoneIds = await _db.Students
            .Where(s => studentIds.Contains(s.Id) && s.PhoneNumber != null && s.PhoneNumber != "")
            .Select(s => s.Id)
            .ToListAsync();
        var studentLoginPhoneIds = await _db.Students
            .Where(s => studentIds.Contains(s.Id) && s.UserId != null)
            .Join(_db.Users, s => s.UserId, u => u.Id, (s, u) => new { s.Id, u.PhoneNumber })
            .Where(x => x.PhoneNumber != null && x.PhoneNumber != "")
            .Select(x => x.Id)
            .ToListAsync();
        var guardianPhoneStudentIds = await _db.Guardianships
            .Where(g => studentIds.Contains(g.StudentId))
            .Join(_db.Guardians, g => g.GuardianId, gu => gu.Id, (g, gu) => new { g.StudentId, gu.UserId })
            .Join(_db.Users, x => x.UserId, u => u.Id, (x, u) => new { x.StudentId, u.PhoneNumber })
            .Where(x => x.PhoneNumber != null && x.PhoneNumber != "")
            .Select(x => x.StudentId)
            .ToListAsync();
        var reachableStudentIds = studentsOwnPhoneIds
            .Concat(studentLoginPhoneIds)
            .Concat(guardianPhoneStudentIds)
            .ToHashSet();

        var subscriptionsByStudent = subscriptions.GroupBy(sub => sub.StudentId)
            .ToDictionary(g => g.Key, g => g.ToList());
        var paymentsByStudent = payments.GroupBy(pay => pay.StudentId)
            .ToDictionary(g => g.Key, g => g.ToList());
        var enrollmentsByStudent = enrollments.GroupBy(e => e.StudentId)
            .ToDictionary(g => g.Key, g => g.ToList());

        Students = students.Select(s =>
        {
            var subs = subscriptionsByStudent.GetValueOrDefault(s.Id, new List<Subscription>());
            var pays = paymentsByStudent.GetValueOrDefault(s.Id, new List<Payment>());
            var running = subs.FirstOrDefault(sub => sub.Status == SubscriptionStatus.Active);

            // The lessons still ahead of this student, soonest first.
            var upcoming = enrollmentsByStudent.GetValueOrDefault(s.Id, new List<SessionEnrollment>())
                .Select(e => sessionsById.GetValueOrDefault(e.SessionId))
                .Where(cs => cs is not null && cs.StartsAtUtc > now && cs.Status != ClassSessionStatus.Cancelled)
                .Select(cs => cs!)
                .OrderBy(cs => cs.StartsAtUtc)
                .ToList();
            var nextLesson = upcoming.FirstOrDefault();

            // Money is reported in ONE currency - the running package's, or the
            // newest open package's. D-53 forbids adding two currencies
            // together, so a second currency is shown on the profile, never
            // folded in here. See MoneyStanding's own remarks for why "paid"
            // is scoped to payments actually tied to a Draft/Active
            // subscription, not every confirmed payment ever in that currency.
            var (currency, money) = MoneyStanding.ComputePrimary(subs, pays);

            var remainingMinutes = subs.Where(sub => sub.Status == SubscriptionStatus.Active)
                .Sum(sub => balanceBySubscription.GetValueOrDefault(sub.Id));
            var purchasedMinutes = subs.Where(sub => sub.Status == SubscriptionStatus.Active)
                .Sum(sub => sub.MinutesTotal);

            var state = StudentLifecycle.Classify(new StudentLifecycleFacts(
                s.Status,
                pays.Any(pay => pay.Status == PaymentStatus.Pending),
                subs.Any(sub => sub.Status == SubscriptionStatus.Draft),
                running is not null,
                subs.Count > 0,
                remainingMinutes,
                attendedStudentIds.Contains(s.Id),
                upcoming.Count,
                nextLesson?.DurationMinutes,
                running?.ExpiresOn,
                nextLesson is null ? null : nextLesson.StartsAtUtc.InUtc().Date));

            return new StudentRow(
                s.Id,
                s.FullName,
                countryByI.GetValueOrDefault(s.CountryId, _localizer["Not specified"].Value),
                s.Status,
                // "English B2 · Spanish A1" — every course the student is
                // currently placed in, rather than an arbitrary one of them.
                LevelLabel(currentLevelsByStudent.GetValueOrDefault(s.Id,
                    new List<MVTeaches.Domain.Placement.StudentLevel>()), courseNames, levelByI),
                guardiansByStudent.GetValueOrDefault(s.Id, Array.Empty<GuardianLink>()),
                state,
                running is null ? null : $"{courseNames.GetValueOrDefault(running.CourseId, "?")} / {levelByI.GetValueOrDefault(running.LevelId, "?")}",
                currency,
                money.Billed,
                money.Paid,
                money.Outstanding,
                remainingMinutes,
                purchasedMinutes,
                running?.StartsOn,
                running?.ExpiresOn,
                upcoming.Count,
                reachableStudentIds.Contains(s.Id));
        }).ToList();

        StateCounts = Students.GroupBy(row => row.State).ToDictionary(g => g.Key, g => g.Count());

        FocusStudentName = FocusStudentId is null
            ? null
            : Students.FirstOrDefault(s => s.Id == FocusStudentId)?.FullName;
    }
}
