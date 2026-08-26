namespace MVTeaches.Application.Scheduling;

/// <summary>
/// Technical Study §15.3 — materializes concrete <c>ClassSession</c> rows from
/// every Active <c>RecurringSchedule</c>, out to the admin-configured horizon.
/// Runs nightly via Hangfire and can also be triggered manually by an admin;
/// both call sites use this exact same method, per the study's own table
/// ("مهمة Hangfire ليلية + تشغيل يدوي من الأدمن") — there is no separate
/// "manual generation" code path to drift out of sync with the scheduled one.
/// </summary>
public interface IScheduleGenerationService
{
    Task<ScheduleGenerationSummary> GenerateAsync(CancellationToken cancellationToken);
}

/// <summary>A plain count, not a report — §15.3 requires conflicts to be
/// recorded (see ScheduleGenerationException), not narrated back to the caller.</summary>
public record ScheduleGenerationSummary(int SessionsCreated, int ConflictsRecorded, int SchedulesProcessed);
