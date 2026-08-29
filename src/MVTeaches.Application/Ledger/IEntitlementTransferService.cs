using MVTeaches.Domain.Catalog;

namespace MVTeaches.Application.Ledger;

/// <summary>One (Course, Level, SessionType) bucket that still carries a
/// positive balance for a level the student is no longer assigned to.</summary>
public record LockedEntitlementBalance(long CourseId, int LevelId, SessionType SessionType, int RemainingMinutes);

public enum TransferMinutesOutcome
{
    Transferred,
    StudentNotFound,
    NoCurrentLevelAssigned,

    /// <summary>The "from" level IS the student's current level — there is
    /// nothing locked to transfer; this bucket is already usable as-is.</summary>
    FromLevelIsCurrentLevel,

    InvalidMinutes,

    /// <summary>The old-level bucket's actual, freshly-recomputed balance
    /// (after the row lock) is less than the amount requested.</summary>
    InsufficientLockedBalance,

    /// <summary>Owner decision 2026-08-30 rule 5: the transfer moves minutes
    /// INTO an existing eligible package/entitlement at the student's current
    /// level — it never fabricates one. The admin must first grant or have
    /// the student purchase a current-level package/entitlement before a
    /// transfer can land anywhere.</summary>
    NoEligibleDestinationSubscription,
}

public record TransferMinutesResult(TransferMinutesOutcome Outcome, int MinutesTransferred = 0);

/// <summary>
/// Owner decision 2026-08-30 rule 5: "Never silently convert an existing
/// package when a student's level changes; the old entitlement stays tied to
/// its original level/type. If the level changes with minutes remaining,
/// prevent use for the new level, show clear status to admin/student, and
/// allow only an authorized admin to transfer remaining minutes to an
/// eligible new-level package/entitlement with a required reason. The
/// transfer must preserve an immutable audit/ledger trail (never edit/delete
/// historical ledger entries), must not create or lose minutes, and must be
/// transactionally/concurrency-safe."
///
/// Nothing in the booking/consumption path needs to change to get the
/// "prevent use for the new level" half of this rule: EntitlementLedgerEntry
/// already denormalizes LevelId onto every entry (exactly like CourseId), and
/// every balance/consumption query (StudentBookingService,
/// JoinAttendanceService.FindConsumableSubscriptionAsync) already scopes
/// strictly by the SESSION's own LevelId — a bucket recorded against a
/// superseded level was already structurally invisible to a session booked
/// at the new level before this service existed. This service only adds the
/// missing "show it, and let an admin move it" half.
/// </summary>
public interface IEntitlementTransferService
{
    /// <summary>Every (Course, Level, SessionType) bucket with a positive
    /// balance that does NOT match the student's current assigned level —
    /// i.e. minutes that exist but are currently unusable by any session the
    /// student can book or join today.</summary>
    Task<IReadOnlyList<LockedEntitlementBalance>> GetLockedBalancesAsync(long studentId, CancellationToken cancellationToken);

    /// <summary>Moves exactly <paramref name="minutes"/> from the
    /// (<paramref name="courseId"/>, <paramref name="fromLevelId"/>,
    /// <paramref name="sessionType"/>) bucket into the student's CURRENT
    /// level's matching bucket — the only level considered "eligible" — by
    /// writing one negative and one positive append-only AdminAdjustment
    /// ledger entry (net zero) plus one AuditLogEntry, inside a single
    /// transaction serialized on this student's own row (the same
    /// row-lock-then-recompute pattern StudentBookingService already uses),
    /// so a concurrent transfer or a concurrent Join cannot race past a
    /// balance this call already committed to moving.</summary>
    Task<TransferMinutesResult> TransferMinutesAsync(long studentId, long courseId, int fromLevelId,
        SessionType sessionType, int minutes, long performedByAdminUserId, string reason, CancellationToken cancellationToken);
}
