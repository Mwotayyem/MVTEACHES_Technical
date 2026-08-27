using NodaTime;

namespace MVTeaches.Application.Scheduling;

public record CreateRecurringScheduleResult(long RecurringScheduleId);

/// <summary>
/// §15.2 (D-23) — the piece that was missing entirely: there was no way to
/// create a RecurringSchedule anywhere in the application. Every downstream
/// feature already built this session and in prior sessions (schedule
/// generation, attendance/Join, payroll declare/verify, certificate
/// progress) depends on a ClassSession existing, and a ClassSession is only
/// ever materialized by IScheduleGenerationService FROM a RecurringSchedule
/// row — so without this, nothing else in the system could ever be
/// exercised against real, admin-created data.
/// </summary>
public interface IRecurringScheduleService
{
    Task<CreateRecurringScheduleResult> CreateAsync(int countryId, long courseId, int levelId, int ageGroupId,
        long teacherId, IReadOnlyList<IsoDayOfWeek> daysOfWeek, LocalTime startLocal, int durationMinutes,
        string timeZoneId, LocalDate startsOn, int capacity, long createdByUserId, CancellationToken cancellationToken);

    /// <summary>§15.3: affects future generation only — never rewrites past or
    /// already-delivered sessions.</summary>
    Task PauseAsync(long recurringScheduleId, CancellationToken cancellationToken);

    Task ResumeAsync(long recurringScheduleId, CancellationToken cancellationToken);

    Task EndAsync(long recurringScheduleId, LocalDate endsOn, CancellationToken cancellationToken);
}
