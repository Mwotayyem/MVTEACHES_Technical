using Microsoft.AspNetCore.Authorization;
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

    public StudentDetailsModel(MvTeachesDbContext db, IEntitlementBalanceQuery balances)
    {
        _db = db;
        _balances = balances;
    }

    public record GuardianRow(long GuardianId, string FullName, GuardianRelationship Relationship, bool IsPrimary, bool CanPay);
    public record LevelHistoryRow(string LevelCode, LevelAssignmentSource Source, bool IsCurrent, NodaTime.Instant EffectiveFromUtc, string? Reason);
    public record SubscriptionRow(long Id, string CourseName, string LevelCode, decimal Price, string Currency,
        SubscriptionStatus Status, SubscriptionOrigin Origin, int BalanceMinutes, NodaTime.LocalDate ExpiresOn);
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

    /// <summary>Header counts an admin reads before deciding what to do next.</summary>
    public int UpcomingSessionCount { get; set; }
    public int AttendedSessionCount { get; set; }
    public int MissedSessionCount { get; set; }
    public bool HasPendingPayment { get; set; }
    public bool HasDraftSubscription { get; set; }

    public async Task<IActionResult> OnGetAsync(long id)
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
                sub.Origin, balance, sub.ExpiresOn));
        }
        Subscriptions = subRows;

        CurrentLevelCode = LevelHistory.FirstOrDefault(l => l.IsCurrent)?.LevelCode;
        RemainingMinutesOnActivePackages = subRows
            .Where(s => s.Status == SubscriptionStatus.Active)
            .Sum(s => s.BalanceMinutes);
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

        var certificates = await _db.Certificates.Where(c => c.StudentId == id).OrderByDescending(c => c.Id).ToListAsync();
        Certificates = certificates.Select(c => new CertificateRow(c.CertificateNumber,
            levelCodes.GetValueOrDefault(c.LevelId, "?"), c.Status, c.IssuedAtUtc)).ToList();

        return Page();
    }
}
