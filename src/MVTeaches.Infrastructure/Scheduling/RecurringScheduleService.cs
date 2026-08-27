using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Scheduling;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;

namespace MVTeaches.Infrastructure.Scheduling;

/// <inheritdoc cref="IRecurringScheduleService"/>
public class RecurringScheduleService : IRecurringScheduleService
{
    private readonly MvTeachesDbContext _db;

    public RecurringScheduleService(MvTeachesDbContext db) => _db = db;

    public async Task<CreateRecurringScheduleResult> CreateAsync(int countryId, long courseId, int levelId,
        int ageGroupId, long teacherId, IReadOnlyList<IsoDayOfWeek> daysOfWeek, LocalTime startLocal,
        int durationMinutes, string timeZoneId, LocalDate startsOn, int capacity, long createdByUserId,
        CancellationToken cancellationToken)
    {
        var schedule = new RecurringSchedule(countryId, courseId, levelId, ageGroupId, teacherId, daysOfWeek,
            startLocal, durationMinutes, timeZoneId, startsOn, capacity, createdByUserId);
        _db.RecurringSchedules.Add(schedule);
        await _db.SaveChangesAsync(cancellationToken);
        return new CreateRecurringScheduleResult(schedule.Id);
    }

    public async Task PauseAsync(long recurringScheduleId, CancellationToken cancellationToken)
    {
        var schedule = await GetAsync(recurringScheduleId, cancellationToken);
        schedule.Pause();
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task ResumeAsync(long recurringScheduleId, CancellationToken cancellationToken)
    {
        var schedule = await GetAsync(recurringScheduleId, cancellationToken);
        schedule.Resume();
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task EndAsync(long recurringScheduleId, LocalDate endsOn, CancellationToken cancellationToken)
    {
        var schedule = await GetAsync(recurringScheduleId, cancellationToken);
        schedule.End(endsOn);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<RecurringSchedule> GetAsync(long id, CancellationToken ct) =>
        await _db.RecurringSchedules.FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new InvalidOperationException("Recurring schedule not found.");
}
