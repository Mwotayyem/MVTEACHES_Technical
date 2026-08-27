using NodaTime;

namespace MVTeaches.Application.Ledger;

/// <summary>One pending (not-yet-expired) makeup grant, for the admin's D-66
/// "awaiting makeup" queue — sorted by nearest expiry so nothing lapses forgotten.</summary>
public record PendingMakeUpCredit(long LedgerEntryId, long StudentId, string StudentFullName,
    long CourseId, int LevelId, int Minutes, LocalDate ExpiresOn, Instant GrantedAtUtc);

/// <summary>
/// Technical Study D-19/D-20/D-63/D-66 — the ONE case where a makeup is a
/// standalone ledger credit rather than simply re-enrolling the student into a
/// replacement session (see ISessionCancellationService): a session was
/// cancelled with no replacement identified yet, or a student's entitlement was
/// consumed by a Join for a session that then failed for reasons outside their
/// control (§17.4, line 1018 — the one case the Technical Study itself says
/// requires the admin's own judgment, never an automatic system decision).
///
/// ⚠️ Known, deliberately un-resolved limitation (CONF-04 in the Technical
/// Study is explicitly marked "قرار تجاري لا تقني — يحتاج إقرار المالك", a
/// business decision reserved for the owner): a granted credit here is NOT
/// currently spendable through IJoinAttendanceService. That service's balance
/// check is scoped to a specific Subscription (`WHERE SubscriptionId ==
/// candidate.Id`), and — matching ForMakeUpGranted's own signature, which
/// intentionally takes no SubscriptionId — a makeup credit is not attached to
/// one. Until the owner resolves CONF-04 (does a makeup credit's validity
/// live independent of any subscription, or must it attach to one), this
/// service can only record the obligation accurately and surface it on the
/// admin's queue; it must not silently invent an attachment rule to make it
/// spendable. See docs/deployment/STATUS.md.
/// </summary>
public interface IMakeUpCreditService
{
    /// <summary>D-63: the admin sets the deadline per case — there is no
    /// system-wide default applied here (DefaultMakeUpExpiryDays is offered to
    /// the admin as a starting suggestion by the UI, never enforced server-side).</summary>
    Task GrantAsync(long studentId, long courseId, int levelId, int minutes, LocalDate expiresOn,
        long performedByUserId, CancellationToken cancellationToken);

    /// <summary>"Today" is anchored to Asia/Amman — the one timezone every
    /// seeded Country/schedule in this system treats as home, and a
    /// MakeUpGranted entry carries no timezone of its own to derive it from.</summary>
    Task<IReadOnlyList<PendingMakeUpCredit>> GetPendingQueueAsync(CancellationToken cancellationToken);

    /// <summary>The daily sweep (Hangfire): issues a MakeUpExpired entry for
    /// every MakeUpGranted entry whose ExpiresOn has passed (Asia/Amman "today"
    /// — see GetPendingQueueAsync) and that has not already been expired
    /// (idempotent — checked via ReversesEntryId, not a separate "resolved"
    /// flag). Returns how many were expired, for the job's own logging.</summary>
    Task<int> ExpireDueAsync(CancellationToken cancellationToken);
}
