using System.Globalization;
using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Scheduling;
using MVTeaches.Application.Settings;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Domain.Settings;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;
using Npgsql;

namespace MVTeaches.Infrastructure.Scheduling;

/// <summary>
/// Technical Study §15.3's المولّد. Materializes <see cref="ClassSession"/> rows
/// from every Active <see cref="RecurringSchedule"/>, one occurrence at a time,
/// out to the admin-configured horizon (<see cref="SettingKey.ScheduleGenerationHorizonWeeks"/>).
///
/// §15.3's crucial rule, restated as code: a schedule edit (Pause/Resume/End)
/// affects future generation ONLY — this service never touches an already-
/// generated ClassSession. It only ever reads schedules and inserts brand-new
/// session rows; it has no update/delete path onto ClassSession at all.
///
/// Idempotent by construction: every call — the nightly Hangfire run and a
/// manual admin "Trigger now" from the Hangfire dashboard alike — re-derives
/// the same occurrence dates and skips any that already have a ClassSession.
/// A teacher-overlap collision is never silently dropped (§15.3): it is
/// recorded as a <see cref="ScheduleGenerationException"/> for an admin to see.
/// </summary>
public class ScheduleGenerationService : IScheduleGenerationService
{
    private const string PostgresUniqueViolationSqlState = "23505";
    private const string PostgresExclusionViolationSqlState = "23P01";

    private readonly MvTeachesDbContext _db;
    private readonly ISettingsProvider _settings;
    private readonly IClock _clock;

    public ScheduleGenerationService(MvTeachesDbContext db, ISettingsProvider settings, IClock clock)
    {
        _db = db;
        _settings = settings;
        _clock = clock;
    }

    public async Task<ScheduleGenerationSummary> GenerateAsync(CancellationToken cancellationToken)
    {
        var horizonWeeks = await _settings.GetIntAsync(SettingKey.ScheduleGenerationHorizonWeeks, cancellationToken);
        var now = _clock.GetCurrentInstant();

        var schedules = await _db.RecurringSchedules
            .Where(s => s.Status == RecurringScheduleStatus.Active)
            .ToListAsync(cancellationToken);

        var created = 0;
        var conflicts = 0;

        foreach (var schedule in schedules)
        {
            var (schedCreated, schedConflicts) = await GenerateForScheduleAsync(schedule, horizonWeeks, now, cancellationToken);
            created += schedCreated;
            conflicts += schedConflicts;
        }

        return new ScheduleGenerationSummary(created, conflicts, schedules.Count);
    }

    private async Task<(int Created, int Conflicts)> GenerateForScheduleAsync(
        RecurringSchedule schedule, int horizonWeeks, Instant now, CancellationToken ct)
    {
        var zone = DateTimeZoneProviders.Tzdb[schedule.TimeZoneId];
        var today = now.InZone(zone).Date;

        var windowStart = schedule.StartsOn > today ? schedule.StartsOn : today;
        var windowEnd = today.PlusWeeks(horizonWeeks);
        if (schedule.EndsOn is { } endsOn && endsOn < windowEnd)
        {
            windowEnd = endsOn;
        }

        var created = 0;
        var conflicts = 0;

        for (var date = windowStart; date <= windowEnd; date = date.PlusDays(1))
        {
            if (!schedule.DaysOfWeek.Contains(date.DayOfWeek))
            {
                continue;
            }

            // §15.1's own note applies here too, one level up: this per-occurrence
            // read-then-write is the fast, common-case path. The database's
            // EXCLUDE constraint (caught below) is what actually makes a
            // collision impossible under concurrency, not this check.
            var startInstant = zone.AtLeniently(date.At(schedule.StartLocal)).ToInstant();
            var alreadyGenerated = await _db.ClassSessions.AnyAsync(
                s => s.RecurringScheduleId == schedule.Id && s.StartsAtUtc == startInstant, ct);
            if (alreadyGenerated)
            {
                continue;
            }

            var endInstant = startInstant.Plus(Duration.FromMinutes(schedule.DurationMinutes));

            var blockedByTimeOff = await _db.TeacherTimeOffs.AnyAsync(
                t => t.TeacherId == schedule.TeacherId && t.StartsAtUtc < endInstant && startInstant < t.EndsAtUtc, ct);
            if (blockedByTimeOff)
            {
                await RecordConflictIfNewAsync(schedule.Id, date, ScheduleConflictReason.TeacherTimeOff,
                    "Occurrence overlaps a recorded TeacherTimeOff window.", now, ct);
                conflicts++;
                continue;
            }

            // Recurring schedules generate Group sessions only — a fixed weekly
            // roster is what §15.2 describes; a Private/Placement session is a
            // one-off ClassSession created directly, never via this generator.
            // Owner decision 2026-08-30: capacity is derived from the session
            // type inside ClassSession — the schedule's own stored Capacity is
            // no longer consulted, so a legacy row cannot widen a group session.
            var session = new ClassSession(schedule.CountryId, schedule.Id, schedule.CourseId, schedule.LevelId,
                schedule.AgeGroupId, schedule.TeacherId, startInstant, endInstant, schedule.TimeZoneId,
                schedule.StartLocal.ToString("HH:mm", CultureInfo.InvariantCulture), SessionType.Group, now);
            _db.ClassSessions.Add(session);

            try
            {
                await _db.SaveChangesAsync(ct);
                created++;
            }
            catch (DbUpdateException ex) when (IsExclusionViolation(ex))
            {
                // §15.3: "إن رفض قيد EXCLUDE توليد حصة، تُسجَّل كـ Exception للأدمن
                // ولا تُتجاهل صامتة" — some other session already holds this
                // teacher for an overlapping window.
                _db.ChangeTracker.Clear();
                await RecordConflictIfNewAsync(schedule.Id, date, ScheduleConflictReason.TeacherOverlap,
                    "Occurrence rejected by the no_teacher_overlap database constraint.", now, ct);
                conflicts++;
            }
        }

        return (created, conflicts);
    }

    private async Task RecordConflictIfNewAsync(long recurringScheduleId, LocalDate occurrenceDate,
        ScheduleConflictReason reason, string detail, Instant now, CancellationToken ct)
    {
        // A rerun (nightly, or a manual "Trigger now") must not pile up a fresh
        // row every night for the same still-unresolved collision — the unique
        // index is the actual guarantee; this check just avoids the round trip
        // that would otherwise throw on every subsequent run.
        var exists = await _db.ScheduleGenerationExceptions.AnyAsync(
            e => e.RecurringScheduleId == recurringScheduleId && e.OccurrenceDate == occurrenceDate, ct);
        if (exists)
        {
            return;
        }

        _db.ScheduleGenerationExceptions.Add(
            new ScheduleGenerationException(recurringScheduleId, occurrenceDate, reason, detail, now));

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Lost a race against another concurrent run recording the exact
            // same conflict — it is already recorded, which is the goal.
            _db.ChangeTracker.Clear();
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresUniqueViolationSqlState };

    private static bool IsExclusionViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresExclusionViolationSqlState };
}
