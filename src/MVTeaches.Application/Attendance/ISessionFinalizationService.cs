namespace MVTeaches.Application.Attendance;

public record SessionFinalizationSummary(int SessionsFinalized, int StudentsMarkedNoShow, int NoShowsWithNoConsumableSubscription);

/// <summary>
/// Owner correction (student self-service booking model, 2026-08-28):
/// D-83's original rule — "a student who never presses Join is never
/// debited, no exceptions" — is superseded for the self-booking case. A
/// student now chooses their own specific session; if it ends and they
/// never joined, this service finalizes that enrollment as a no-show and
/// consumes the scheduled duration exactly once, the same way a real Join
/// would have. A centre-cancelled or administratively rescheduled session
/// is NEVER finalized this way — it never reaches Scheduled+ended in a way
/// this service acts on, because only sessions still in
/// <c>ClassSessionStatus.Scheduled</c> are considered at all.
///
/// Runs as a frequent Hangfire recurring job (see Program.cs) rather than a
/// nightly one — a no-show should resolve promptly after the session ends,
/// not the next morning. Idempotent and safe to run concurrently with a
/// genuine late Join: both write to the same (SessionId, StudentId)-unique
/// AttendanceRecord row, so whichever wins the database race is the only
/// one that proceeds to write a ledger entry — the loser here is a
/// deliberate, silent no-op, exactly like JoinAttendanceService's own race
/// handling.
/// </summary>
public interface ISessionFinalizationService
{
    Task<SessionFinalizationSummary> FinalizeEndedSessionsAsync(CancellationToken cancellationToken);
}
