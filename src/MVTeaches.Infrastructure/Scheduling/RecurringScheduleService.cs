using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Scheduling;
using MVTeaches.Domain.Integrations;
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
        // Owner clarification (2026-08-29): every MVTeaches session is an
        // online session — a teacher with no usable Zoom/Google Meet
        // connection must never be assigned one. A free Google account is
        // enough; this only blocks a teacher with NEITHER provider connected.
        var teacherReady = await _db.TeacherMeetingConnections.AnyAsync(
            c => c.TeacherId == teacherId && c.Status == ProviderConnectionStatus.Connected, cancellationToken);
        if (!teacherReady)
        {
            throw new ArgumentException(
                "This teacher is not ready for online sessions — connect a Zoom or Google Meet account first " +
                "(Teacher portal → Connections; a free Google account is sufficient).", nameof(teacherId));
        }

        // Owner decision 2026-08-30 rule 5: "A teacher must not publish a
        // session for an unauthorized level." Absence of a grant is denial —
        // there is no implicit default level for a teacher.
        // Owner decision 2026-09-04: the grant is per (course, level), so the
        // check is too. Matching on level alone let a teacher hired for English
        // publish the same level in Spanish or Quran — which is precisely the
        // hole the course column was added to close, and leaving this query on
        // level alone would have left it open with a schema that only looked
        // fixed.
        var levelAllowed = await _db.TeacherLevelAssignments.AnyAsync(
            a => a.TeacherId == teacherId && a.CourseId == courseId && a.LevelId == levelId, cancellationToken);
        if (!levelAllowed)
        {
            throw new ArgumentException(
                "This teacher is not authorized to teach this level of this course. An admin must grant it first " +
                "(Admin portal → Teachers → Levels).", nameof(levelId));
        }

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
