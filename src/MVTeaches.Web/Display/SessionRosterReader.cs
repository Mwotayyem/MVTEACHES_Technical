using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Ledger;
using MVTeaches.Domain.Payments;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Domain.Subscriptions;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;

namespace MVTeaches.Web.Display;

/// <summary>Who is actually in one session, with the facts an admin needs
/// before the lesson runs: are they paid up, is there balance left, what
/// package are they on, what level are they at.</summary>
public record SessionRosterStudent(
    long StudentId,
    string FullName,
    string? LevelCode,
    StudentLifecycleState State,
    string? PackageName,
    string? Currency,
    decimal Outstanding,
    int RemainingMinutes,
    string? Note);

public record SessionRoster(long SessionId, IReadOnlyList<SessionRosterStudent> Students);

/// <summary>
/// Reads the roster for a set of sessions in a fixed number of queries, so a
/// list of a dozen sessions does not turn into a hundred round-trips.
///
/// Read-only and display-only. It enrols nobody, changes no state, and takes
/// no decision: the state badge is <see cref="StudentLifecycle"/>'s display
/// classification, and whether a student may actually join is still
/// PaymentEligibilityService's call at Join time (D-38), untouched.
/// </summary>
public class SessionRosterReader
{
    private readonly MvTeachesDbContext _db;
    private readonly IEntitlementBalanceQuery _balances;
    private readonly IClock _clock;

    public SessionRosterReader(MvTeachesDbContext db, IEntitlementBalanceQuery balances, IClock clock)
    {
        _db = db;
        _balances = balances;
        _clock = clock;
    }

    public async Task<IReadOnlyDictionary<long, SessionRoster>> ReadAsync(
        IReadOnlyCollection<long> sessionIds, CancellationToken cancellationToken)
    {
        if (sessionIds.Count == 0)
        {
            return new Dictionary<long, SessionRoster>();
        }

        var ids = sessionIds.ToList();
        var enrollments = await _db.SessionEnrollments
            .Where(e => ids.Contains(e.SessionId) && e.State == EnrollmentState.Active)
            .ToListAsync(cancellationToken);

        var studentIds = enrollments.Select(e => e.StudentId).Distinct().ToList();
        if (studentIds.Count == 0)
        {
            return ids.ToDictionary(id => id, id => new SessionRoster(id, Array.Empty<SessionRosterStudent>()));
        }

        var students = await _db.Students.Where(s => studentIds.Contains(s.Id)).ToListAsync(cancellationToken);
        var levelCodes = await _db.Levels.ToDictionaryAsync(l => l.Id, l => l.Code, cancellationToken);
        var courseNames = await _db.Courses.ToDictionaryAsync(c => c.Id, c => c.NameEn, cancellationToken);
        var currentLevelByStudent = await _db.StudentLevels
            .Where(l => l.IsCurrent && studentIds.Contains(l.StudentId))
            .ToDictionaryAsync(l => l.StudentId, l => l.LevelId, cancellationToken);

        var subscriptions = await _db.Subscriptions
            .Where(sub => studentIds.Contains(sub.StudentId))
            .ToListAsync(cancellationToken);
        var balanceBySubscription = await _balances.GetSubscriptionBalancesAsync(
            subscriptions.Select(sub => sub.Id).ToList(), cancellationToken);
        var payments = await _db.Payments
            .Where(pay => studentIds.Contains(pay.StudentId))
            .ToListAsync(cancellationToken);
        var attendedStudentIds = (await _db.AttendanceRecords
                .Where(a => studentIds.Contains(a.StudentId) && a.IsPresent)
                .Select(a => a.StudentId).Distinct().ToListAsync(cancellationToken))
            .ToHashSet();

        var now = _clock.GetCurrentInstant();
        var upcomingByStudent = (await _db.SessionEnrollments
                .Where(e => studentIds.Contains(e.StudentId) && e.State == EnrollmentState.Active)
                .Join(_db.ClassSessions.Where(cs => cs.StartsAtUtc > now && cs.Status != ClassSessionStatus.Cancelled),
                    e => e.SessionId, cs => cs.Id, (e, cs) => new { e.StudentId, Session = cs })
                .ToListAsync(cancellationToken))
            .GroupBy(x => x.StudentId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Session).OrderBy(cs => cs.StartsAtUtc).ToList());

        var subscriptionsByStudent = subscriptions.GroupBy(sub => sub.StudentId)
            .ToDictionary(g => g.Key, g => g.ToList());
        var paymentsByStudent = payments.GroupBy(pay => pay.StudentId)
            .ToDictionary(g => g.Key, g => g.ToList());
        var studentsById = students.ToDictionary(s => s.Id);

        var rowByStudent = new Dictionary<long, SessionRosterStudent>();
        foreach (var student in students)
        {
            var subs = subscriptionsByStudent.GetValueOrDefault(student.Id, new List<Subscription>());
            var pays = paymentsByStudent.GetValueOrDefault(student.Id, new List<Payment>());
            var running = subs.FirstOrDefault(sub => sub.Status == SubscriptionStatus.Active);
            var upcoming = upcomingByStudent.GetValueOrDefault(student.Id, new List<ClassSession>());
            var nextLesson = upcoming.FirstOrDefault();

            var remainingMinutes = subs.Where(sub => sub.Status == SubscriptionStatus.Active)
                .Sum(sub => balanceBySubscription.GetValueOrDefault(sub.Id));

            // One currency only — the running package's. D-53 forbids folding
            // two currencies into one number, so a second one is left for the
            // profile rather than invented here. See MoneyStanding's own
            // remarks: "paid" only counts payments tied to THIS student's
            // Draft/Active subscriptions, never every confirmed payment ever
            // made in that currency (a closed, fully-paid, since-Expired
            // package must not make a new unpaid one look already settled).
            var (currency, money) = MoneyStanding.ComputePrimary(subs, pays);

            var state = StudentLifecycle.Classify(new StudentLifecycleFacts(
                student.Status,
                pays.Any(pay => pay.Status == PaymentStatus.Pending),
                subs.Any(sub => sub.Status == SubscriptionStatus.Draft),
                running is not null,
                subs.Count > 0,
                remainingMinutes,
                attendedStudentIds.Contains(student.Id),
                upcoming.Count,
                nextLesson?.DurationMinutes,
                running?.ExpiresOn,
                nextLesson is null ? null : nextLesson.StartsAtUtc.InUtc().Date));

            // The "note" is the most recent reason a person actually typed
            // about this student's package — never invented text.
            var note = subs.Select(sub => sub.ExtendedReason ?? sub.CreatedReason)
                .FirstOrDefault(reason => !string.IsNullOrWhiteSpace(reason));

            rowByStudent[student.Id] = new SessionRosterStudent(
                student.Id,
                student.FullName,
                currentLevelByStudent.TryGetValue(student.Id, out var levelId)
                    ? levelCodes.GetValueOrDefault(levelId)
                    : null,
                state,
                running is null ? null : $"{courseNames.GetValueOrDefault(running.CourseId, "?")} / {levelCodes.GetValueOrDefault(running.LevelId, "?")}",
                currency,
                money.Outstanding,
                remainingMinutes,
                note);
        }

        return ids.ToDictionary(
            id => id,
            id => new SessionRoster(id, enrollments
                .Where(e => e.SessionId == id && studentsById.ContainsKey(e.StudentId))
                .Select(e => rowByStudent[e.StudentId])
                .OrderBy(row => row.FullName, StringComparer.CurrentCulture)
                .ToList()));
    }
}
