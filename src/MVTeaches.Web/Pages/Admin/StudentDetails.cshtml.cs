using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Ledger;
using MVTeaches.Domain.Certificates;
using MVTeaches.Domain.Payments;
using MVTeaches.Domain.People;
using MVTeaches.Domain.Placement;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Domain.Subscriptions;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using MVTeaches.Web.Display;

namespace MVTeaches.Web.Pages.Admin;

/// <summary>
/// §14 — the Student Profile drill-down the register/list page (Students.cshtml)
/// never had: one screen bringing together everything already built this
/// session for a single student (guardians, level history, subscriptions with
/// live balances, payment history, certificates). Purely a read aggregation
/// over already-tested services/tables — no new business logic here.
/// </summary>
[Authorize(Roles = RoleNames.Admin + "," + RoleNames.SystemAdmin)]
public class StudentDetailsModel : PageModel
{
    private readonly MvTeachesDbContext _db;
    private readonly IEntitlementBalanceQuery _balances;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly NodaTime.IClock _clock;

    public StudentDetailsModel(MvTeachesDbContext db, IEntitlementBalanceQuery balances,
        UserManager<ApplicationUser> userManager, NodaTime.IClock clock)
    {
        _db = db;
        _balances = balances;
        _userManager = userManager;
        _clock = clock;
    }

    public record GuardianRow(long GuardianId, string FullName, GuardianRelationship Relationship, bool IsPrimary, bool CanPay);
    public record LevelHistoryRow(string LevelCode, LevelAssignmentSource Source, bool IsCurrent, NodaTime.Instant EffectiveFromUtc, string? Reason);
    public record SubscriptionRow(long Id, string CourseName, string LevelCode, decimal Price, string Currency,
        SubscriptionStatus Status, SubscriptionOrigin Origin, int BalanceMinutes, NodaTime.LocalDate ExpiresOn,
        int MinutesTotal, int SessionsCount)
    {
        public int UsedMinutes => Math.Max(0, MinutesTotal - BalanceMinutes);

        /// <summary>How much of the package has been consumed, for the bar on
        /// the profile. A zero-minute package can never be "half used", so it
        /// reports 0 rather than dividing by zero.</summary>
        public int UsedPercent => MinutesTotal <= 0 ? 0
            : (int)Math.Round(Math.Clamp(UsedMinutes * 100d / MinutesTotal, 0d, 100d));
    }

    /// <summary>Money for ONE currency. Never a total across currencies: D-53
    /// forbids converting between them automatically, so a student who paid in
    /// two currencies gets two lines, not one invented sum.</summary>
    public record MoneyLine(string Currency, decimal Owed, decimal Paid, decimal Outstanding, decimal AwaitingConfirmation)
    {
        // Outstanding comes from MoneyStanding as a sum of PER-SUBSCRIPTION
        // shortfalls, each clamped at zero before adding up — never Owed
        // minus Paid, which could let one open package's overpayment cancel
        // out a different package's real shortfall.
        public bool IsSettled => Outstanding <= 0m;
        public int PaidPercent => Owed <= 0m ? 100
            : (int)Math.Round(Math.Clamp((double)(Paid / Owed) * 100d, 0d, 100d));
    }

    public record CompensationRow(long Id, CompensationRequestStatus Status, string? Reason,
        NodaTime.Instant RequestedAtUtc, NodaTime.Instant? OriginalStartsAtUtc, string? OriginalTimeZoneId,
        NodaTime.Instant? ReplacementStartsAtUtc, string? ReplacementTimeZoneId, string? RejectionReason);

    /// <summary>A student row has no free-text notes column, and adding one is
    /// a schema change nobody has asked for. "Notes" here is therefore the
    /// written record the system ALREADY keeps about this student — the reason
    /// typed when their level was changed, when a package was created or
    /// extended, when a make-up was asked for or refused, when a payment was
    /// rejected. Every line is something a person actually wrote.</summary>
    public record NoteRow(NodaTime.Instant AtUtc, string Source, string Text);

    /// <summary>A note somebody at the centre actually sat down and wrote
    /// about this student (owner decision 2026-09-01), as opposed to
    /// <see cref="NoteRow"/> above, which is the reason text the system
    /// already collected as a side effect of other actions. Both are shown,
    /// separately, because they answer different questions.
    ///
    /// Internal to the admin. No student-facing or guardian-facing screen
    /// reads these.</summary>
    public record WrittenNoteRow(long Id, StudentNoteCategory Category, string Text,
        string AuthorName, NodaTime.Instant CreatedAtUtc);
    public record PaymentRow(long Id, decimal Amount, string Currency, PaymentMethod Method, PaymentStatus Status,
        string ReferenceCode, NodaTime.Instant CreatedAtUtc);
    public record CertificateRow(string CertificateNumber, string LevelCode, CertificateStatus Status, NodaTime.Instant IssuedAtUtc);

    /// <summary>One row per session this student is (or was) enrolled in,
    /// with whatever attendance was actually recorded for it. Read-only —
    /// the same tables /Teacher/MySessions and the finalizer already write.</summary>
    public record SessionRow(long SessionId, NodaTime.Instant StartsAtUtc, string? TimeZoneId, string LevelCode,
        string TeacherName, ClassSessionStatus SessionStatus, EnrollmentState EnrollmentState,
        bool IsCompensation, bool? WasPresent);

    // Fully qualified to avoid ambiguity with the sibling MVTeaches.Web.Pages.Student namespace.
    public MVTeaches.Domain.People.Student? Student { get; set; }
    public string? CountryName { get; set; }

    /// <summary>Header facts an admin asks for first, all derived from the rows
    /// already loaded below — no stored summary, no new field.</summary>
    public string? CurrentLevelCode { get; set; }

    public int RemainingMinutesOnActivePackages { get; set; }

    public string? PrimaryGuardianName { get; set; }
    public IReadOnlyList<GuardianRow> Guardians { get; set; } = Array.Empty<GuardianRow>();
    public IReadOnlyList<LevelHistoryRow> LevelHistory { get; set; } = Array.Empty<LevelHistoryRow>();
    public IReadOnlyList<SubscriptionRow> Subscriptions { get; set; } = Array.Empty<SubscriptionRow>();
    public IReadOnlyList<PaymentRow> Payments { get; set; } = Array.Empty<PaymentRow>();
    public IReadOnlyList<CertificateRow> Certificates { get; set; } = Array.Empty<CertificateRow>();
    public IReadOnlyList<SessionRow> Sessions { get; set; } = Array.Empty<SessionRow>();

    public IReadOnlyList<CompensationRow> Compensations { get; set; } = Array.Empty<CompensationRow>();
    public IReadOnlyList<NoteRow> Notes { get; set; } = Array.Empty<NoteRow>();

    public IReadOnlyList<WrittenNoteRow> WrittenNotes { get; set; } = Array.Empty<WrittenNoteRow>();

    [BindProperty]
    public NewNoteInput NewNote { get; set; } = new();

    public string? StatusMessage { get; set; }

    public class NewNoteInput
    {
        public StudentNoteCategory Category { get; set; } = StudentNoteCategory.Learning;

        [Required(ErrorMessage = "Write the note first.")]
        [StringLength(2000)]
        public string Text { get; set; } = string.Empty;
    }

    /// <summary>One line per currency this student has been billed or has paid
    /// in — see <see cref="MoneyLine"/> for why these are never added up.</summary>
    public IReadOnlyList<MoneyLine> MoneyLines { get; set; } = Array.Empty<MoneyLine>();

    /// <summary>Minutes bought and minutes left across the ACTIVE packages, so
    /// the profile can show one bar for "how much of what they bought is
    /// gone". Draft packages are excluded: nothing has been consumed from a
    /// package that never activated.</summary>
    public int PurchasedMinutesOnActivePackages { get; set; }

    public int UsedMinutesOnActivePackages => Math.Max(0, PurchasedMinutesOnActivePackages - RemainingMinutesOnActivePackages);

    public int PackageUsedPercent => PurchasedMinutesOnActivePackages <= 0 ? 0
        : (int)Math.Round(Math.Clamp(UsedMinutesOnActivePackages * 100d / PurchasedMinutesOnActivePackages, 0d, 100d));

    /// <summary>Share of the sessions that were actually held (attended plus
    /// missed) where the student was present. Sessions not yet held are not in
    /// the denominator — an unheld lesson is not a missed one.</summary>
    public int AttendancePercent => (AttendedSessionCount + MissedSessionCount) <= 0 ? 0
        : (int)Math.Round(AttendedSessionCount * 100d / (AttendedSessionCount + MissedSessionCount));

    public SessionRow? NextSession { get; set; }

    /// <summary>Which panel of the profile is open. Display only.</summary>
    [BindProperty(SupportsGet = true, Name = "tab")]
    public string ActiveTab { get; set; } = "summary";

    /// <summary>Header counts an admin reads before deciding what to do next.</summary>
    public int UpcomingSessionCount { get; set; }
    public int AttendedSessionCount { get; set; }
    public int MissedSessionCount { get; set; }
    public bool HasPendingPayment { get; set; }
    public bool HasDraftSubscription { get; set; }

    /// <summary>Adds one note. Notes are append-only: there is no edit and no
    /// delete, so the record of what the centre believed, and when, stays
    /// honest. A note that turns out to be wrong is answered with another
    /// note. Nothing here touches money, levels, packages or attendance.</summary>
    public async Task<IActionResult> OnPostAddNoteAsync(long id)
    {
        ModelState.Clear();
        if (!TryValidateModel(NewNote, nameof(NewNote)))
        {
            return await LoadPageAsync(id);
        }

        var student = await _db.Students.FirstOrDefaultAsync(s => s.Id == id);
        if (student is null)
        {
            return NotFound();
        }

        var actingUserId = long.Parse(_userManager.GetUserId(User)!);
        var author = await _userManager.GetUserAsync(User);
        _db.StudentNotes.Add(new StudentNote(id, NewNote.Category, NewNote.Text, actingUserId,
            author?.Email ?? string.Empty, _clock.GetCurrentInstant()));
        await _db.SaveChangesAsync(HttpContext.RequestAborted);

        return RedirectToPage(new { id, tab = "notes" });
    }

    public async Task<IActionResult> OnGetAsync(long id) => await LoadPageAsync(id);

    private async Task<IActionResult> LoadPageAsync(long id)
    {
        Student = await _db.Students.FirstOrDefaultAsync(s => s.Id == id);
        if (Student is null)
        {
            return NotFound();
        }

        CountryName = await _db.Countries.Where(c => c.Id == Student.CountryId).Select(c => c.NameEn).FirstOrDefaultAsync();

        var levelCodes = await _db.Levels.ToDictionaryAsync(l => l.Id, l => l.Code);
        var courseNames = await _db.Courses.ToDictionaryAsync(c => c.Id, c => c.NameEn);

        var guardianships = await _db.Guardianships.Where(g => g.StudentId == id).ToListAsync();
        var guardianNames = await _db.Guardians
            .Where(g => guardianships.Select(x => x.GuardianId).Contains(g.Id))
            .ToDictionaryAsync(g => g.Id, g => g.FullName);
        Guardians = guardianships.Select(g => new GuardianRow(g.GuardianId,
            guardianNames.GetValueOrDefault(g.GuardianId, $"#{g.GuardianId}"), g.Relationship, g.IsPrimary, g.CanPay)).ToList();

        var levels = await _db.StudentLevels.Where(l => l.StudentId == id).OrderByDescending(l => l.EffectiveFromUtc).ToListAsync();
        LevelHistory = levels.Select(l => new LevelHistoryRow(levelCodes.GetValueOrDefault(l.LevelId, "?"), l.Source,
            l.IsCurrent, l.EffectiveFromUtc, l.Reason)).ToList();

        var subs = await _db.Subscriptions.Where(s => s.StudentId == id).OrderByDescending(s => s.Id).ToListAsync();
        var subRows = new List<SubscriptionRow>();
        foreach (var sub in subs)
        {
            var balance = await _balances.GetSubscriptionBalanceAsync(sub.Id, HttpContext.RequestAborted);
            subRows.Add(new SubscriptionRow(sub.Id, courseNames.GetValueOrDefault(sub.CourseId, "?"),
                levelCodes.GetValueOrDefault(sub.LevelId, "?"), sub.Price.Amount, sub.Price.Currency, sub.Status,
                sub.Origin, balance, sub.ExpiresOn, sub.MinutesTotal, sub.SessionsCount));
        }
        Subscriptions = subRows;

        CurrentLevelCode = LevelHistory.FirstOrDefault(l => l.IsCurrent)?.LevelCode;
        RemainingMinutesOnActivePackages = subRows
            .Where(s => s.Status == SubscriptionStatus.Active)
            .Sum(s => s.BalanceMinutes);
        PurchasedMinutesOnActivePackages = subRows
            .Where(s => s.Status == SubscriptionStatus.Active)
            .Sum(s => s.MinutesTotal);
        PrimaryGuardianName = Guardians.FirstOrDefault(g => g.IsPrimary)?.FullName
                              ?? Guardians.FirstOrDefault()?.FullName;

        var payments = await _db.Payments.Where(p => p.StudentId == id).OrderByDescending(p => p.Id).ToListAsync();
        Payments = payments.Select(p => new PaymentRow(p.Id, p.Amount.Amount, p.Amount.Currency, p.Method, p.Status,
            p.ReferenceCode, p.CreatedAtUtc)).ToList();

        // Sessions and attendance — the two things a profile was missing and an
        // admin asks for first ("did they actually show up?"). Read-only joins over
        // tables that already exist; nothing here writes or derives a new rule.
        var enrollments = await _db.SessionEnrollments.Where(e => e.StudentId == id).ToListAsync();
        var enrolledSessionIds = enrollments.Select(e => e.SessionId).Distinct().ToList();
        var sessionsById = await _db.ClassSessions
            .Where(cs => enrolledSessionIds.Contains(cs.Id))
            .ToDictionaryAsync(cs => cs.Id);
        var teacherNames = await _db.Teachers
            .Where(t => sessionsById.Values.Select(cs => cs.TeacherId).Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.FullName);
        var attendanceBySession = await _db.AttendanceRecords
            .Where(a => a.StudentId == id && enrolledSessionIds.Contains(a.SessionId))
            .ToDictionaryAsync(a => a.SessionId, a => a.IsPresent);

        Sessions = enrollments
            .Where(e => sessionsById.ContainsKey(e.SessionId))
            .Select(e =>
            {
                var session = sessionsById[e.SessionId];
                return new SessionRow(session.Id, session.StartsAtUtc, session.ScheduleTimeZone,
                    levelCodes.GetValueOrDefault(session.LevelId, "?"),
                    teacherNames.GetValueOrDefault(session.TeacherId, string.Empty),
                    session.Status, e.State, e.CompensatesForSessionId is not null,
                    attendanceBySession.TryGetValue(session.Id, out var present) ? present : (bool?)null);
            })
            .OrderByDescending(r => r.StartsAtUtc)
            .ToList();

        var nowUtc = NodaTime.SystemClock.Instance.GetCurrentInstant();
        UpcomingSessionCount = Sessions.Count(r => r.StartsAtUtc > nowUtc
                                                   && r.SessionStatus != ClassSessionStatus.Cancelled
                                                   && r.EnrollmentState == EnrollmentState.Active);
        AttendedSessionCount = Sessions.Count(r => r.WasPresent == true);
        MissedSessionCount = Sessions.Count(r => r.WasPresent == false);
        HasPendingPayment = Payments.Any(p => p.Status == PaymentStatus.Pending);
        HasDraftSubscription = Subscriptions.Any(sub => sub.Status == SubscriptionStatus.Draft);

        NextSession = Sessions
            .Where(r => r.StartsAtUtc > nowUtc
                        && r.SessionStatus != ClassSessionStatus.Cancelled
                        && r.EnrollmentState == EnrollmentState.Active)
            .OrderBy(r => r.StartsAtUtc)
            .FirstOrDefault();

        // What was billed, what actually arrived, and what is still in the air —
        // per currency, never summed across them (D-53). "Billed"/"Paid" cover
        // only Draft/Active packages, via MoneyStanding — see its own remarks
        // for why a payment counts only when it is actually recorded against
        // one of those specific subscriptions, never every confirmed payment
        // ever made in that currency (a closed, fully-paid, since-Expired
        // package must not make a NEW small unpaid one look already paid for).
        var moneyByCurrency = MoneyStanding.ComputeByCurrency(subs, payments);
        var pendingByCurrency = payments
            .Where(pay => pay.Status == PaymentStatus.Pending)
            .GroupBy(pay => pay.Amount.Currency)
            .ToDictionary(g => g.Key, g => g.Sum(pay => pay.Amount.Amount));

        MoneyLines = moneyByCurrency.Keys
            .Union(pendingByCurrency.Keys)
            .OrderBy(currency => currency)
            .Select(currency =>
            {
                var money = moneyByCurrency.GetValueOrDefault(currency);
                return new MoneyLine(currency, money.Billed, money.Paid, money.Outstanding,
                    pendingByCurrency.GetValueOrDefault(currency));
            })
            .ToList();

        var compensations = await _db.CompensationRequests
            .Where(c => c.StudentId == id)
            .OrderByDescending(c => c.Id)
            .ToListAsync();
        var compensationSessionIds = compensations.Select(c => c.OriginalSessionId)
            .Concat(compensations.Where(c => c.ReplacementSessionId is not null).Select(c => c.ReplacementSessionId!.Value))
            .Distinct().ToList();
        var compensationSessions = await _db.ClassSessions
            .Where(cs => compensationSessionIds.Contains(cs.Id))
            .ToDictionaryAsync(cs => cs.Id);
        Compensations = compensations.Select(c =>
        {
            compensationSessions.TryGetValue(c.OriginalSessionId, out var original);
            ClassSession? replacement = null;
            if (c.ReplacementSessionId is not null)
            {
                compensationSessions.TryGetValue(c.ReplacementSessionId.Value, out replacement);
            }
            return new CompensationRow(c.Id, c.Status, c.Reason, c.RequestedAtUtc,
                original?.StartsAtUtc, original?.ScheduleTimeZone,
                replacement?.StartsAtUtc, replacement?.ScheduleTimeZone, c.RejectionReason);
        }).ToList();

        // Everything a person actually typed about this student, newest first.
        var notes = new List<NoteRow>();
        foreach (var level in levels.Where(l => !string.IsNullOrWhiteSpace(l.Reason)))
        {
            notes.Add(new NoteRow(level.EffectiveFromUtc, "LevelChange", level.Reason!));
        }
        foreach (var sub in subs)
        {
            if (!string.IsNullOrWhiteSpace(sub.CreatedReason))
            {
                notes.Add(new NoteRow(sub.StartsOn.AtMidnight().InUtc().ToInstant(), "PackageCreated", sub.CreatedReason!));
            }
            if (!string.IsNullOrWhiteSpace(sub.ExtendedReason))
            {
                notes.Add(new NoteRow(sub.StartsOn.AtMidnight().InUtc().ToInstant(), "PackageExtended", sub.ExtendedReason!));
            }
        }
        foreach (var c in compensations)
        {
            if (!string.IsNullOrWhiteSpace(c.Reason))
            {
                notes.Add(new NoteRow(c.RequestedAtUtc, "CompensationAsked", c.Reason!));
            }
            if (!string.IsNullOrWhiteSpace(c.RejectionReason))
            {
                notes.Add(new NoteRow(c.ResolvedAtUtc ?? c.RequestedAtUtc, "CompensationRefused", c.RejectionReason!));
            }
        }
        foreach (var pay in payments.Where(pay => !string.IsNullOrWhiteSpace(pay.RejectionReason)))
        {
            notes.Add(new NoteRow(pay.CreatedAtUtc, "PaymentRejected", pay.RejectionReason!));
        }
        Notes = notes.OrderByDescending(n => n.AtUtc).ToList();

        WrittenNotes = (await _db.StudentNotes.AsNoTracking()
                .Where(note => note.StudentId == id)
                .OrderByDescending(note => note.CreatedAtUtc).ThenByDescending(note => note.Id)
                .ToListAsync())
            .Select(note => new WrittenNoteRow(note.Id, note.Category, note.Text, note.AuthorName, note.CreatedAtUtc))
            .ToList();

        var certificates = await _db.Certificates.Where(c => c.StudentId == id).OrderByDescending(c => c.Id).ToListAsync();
        Certificates = certificates.Select(c => new CertificateRow(c.CertificateNumber,
            levelCodes.GetValueOrDefault(c.LevelId, "?"), c.Status, c.IssuedAtUtc)).ToList();

        return Page();
    }
}
