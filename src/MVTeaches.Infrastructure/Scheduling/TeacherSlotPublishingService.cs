using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Scheduling;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Integrations;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;
using Npgsql;

namespace MVTeaches.Infrastructure.Scheduling;

/// <inheritdoc cref="ITeacherSlotPublishingService"/>
public class TeacherSlotPublishingService : ITeacherSlotPublishingService
{
    private const string PostgresExclusionViolationSqlState = "23P01";

    private readonly MvTeachesDbContext _db;
    private readonly IClock _clock;

    public TeacherSlotPublishingService(MvTeachesDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<PublishSlotResult> PublishSlotAsync(long teacherId, long actingUserId, int countryId,
        long courseId, int levelId, int ageGroupId, Instant startsAtUtc, int durationMinutes,
        string scheduleTimeZone, string localStartText, SessionType sessionType, CancellationToken cancellationToken)
    {
        // Owner decision 2026-08-30 rule 7: "Teachers cannot create slots for
        // another teacher" — re-checked here, never trusted from teacherId
        // arriving in the request alone (the same pattern every other
        // student/teacher-scoped service in this codebase uses).
        var isThisTeacherThemself = await _db.Teachers.AnyAsync(t => t.Id == teacherId && t.UserId == actingUserId, cancellationToken);
        if (!isThisTeacherThemself)
        {
            return new PublishSlotResult(PublishSlotOutcome.Unauthorized);
        }

        // Owner clarification 2026-08-29: a teacher with no usable Zoom/Google
        // connection is "not ready for online sessions" and cannot be
        // assigned any — the same gate RecurringScheduleService.CreateAsync enforces.
        var isReady = await _db.TeacherMeetingConnections.AnyAsync(
            c => c.TeacherId == teacherId && c.Status == ProviderConnectionStatus.Connected, cancellationToken);
        if (!isReady)
        {
            return new PublishSlotResult(PublishSlotOutcome.TeacherNotReadyForOnlineSessions);
        }

        // Owner decision 2026-08-30 rule 5: "A teacher must not publish a
        // session for an unauthorized level."
        // Owner decision 2026-09-04: the grant is per (course, level), so the
        // check is too. Matching on level alone let a teacher hired for English
        // publish the same level in Spanish or Quran — which is precisely the
        // hole the course column was added to close, and leaving this query on
        // level alone would have left it open with a schema that only looked
        // fixed.
        var isAuthorizedForLevel = await _db.TeacherLevelAssignments.AnyAsync(
            a => a.TeacherId == teacherId && a.CourseId == courseId && a.LevelId == levelId, cancellationToken);
        if (!isAuthorizedForLevel)
        {
            return new PublishSlotResult(PublishSlotOutcome.NotAuthorizedForLevel);
        }

        var endsAtUtc = startsAtUtc.Plus(Duration.FromMinutes(durationMinutes));
        var session = new ClassSession(countryId, recurringScheduleId: null, courseId, levelId, ageGroupId,
            teacherId, startsAtUtc, endsAtUtc, scheduleTimeZone, localStartText, sessionType,
            _clock.GetCurrentInstant());
        _db.ClassSessions.Add(session);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresExclusionViolationSqlState })
        {
            // Owner decision 2026-08-30 rule 7: "Prevent overlapping active
            // slots for the same teacher" — no_teacher_overlap (§14.2) is the
            // real, physical-impossibility guard; this only translates its
            // violation into a friendly outcome instead of a raw exception.
            _db.ChangeTracker.Clear();
            return new PublishSlotResult(PublishSlotOutcome.Overlapping);
        }

        return new PublishSlotResult(PublishSlotOutcome.Published, session.Id);
    }
}
