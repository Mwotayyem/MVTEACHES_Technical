using MVTeaches.Domain.Catalog;
using NodaTime;

namespace MVTeaches.Application.Scheduling;

public enum PublishSlotOutcome
{
    Published,
    Unauthorized,
    TeacherNotReadyForOnlineSessions,
    NotAuthorizedForLevel,
    Overlapping,
}

public record PublishSlotResult(PublishSlotOutcome Outcome, long? SessionId = null);

/// <summary>
/// Owner decision 2026-08-30 rule 7: "The teacher creates and manages
/// available session slots within their permitted levels." Distinct from
/// RecurringScheduleService (an ADMIN-only weekly recurring roster) — this is
/// a single, one-off slot a teacher publishes directly, matching "the teacher
/// chooses: an authorized level, date and start time, scheduled duration,
/// Group or Private." Capacity is never a parameter here — see
/// ClassSession.CapacityFor, applied automatically from the chosen session type.
/// </summary>
public interface ITeacherSlotPublishingService
{
    /// <summary><paramref name="actingUserId"/> must be this exact teacher's
    /// own login — a teacher can never publish a slot "as" another teacher,
    /// re-checked here rather than trusted from teacherId arriving in the
    /// request alone. Refuses a level the teacher has no
    /// TeacherLevelAssignment grant for (rule 5), a teacher with no usable
    /// video connection (the pre-existing 2026-08-29 readiness gate
    /// RecurringScheduleService already enforces), and a time range that
    /// overlaps another of this teacher's own active sessions
    /// (no_teacher_overlap, the same physical-impossibility EXCLUDE
    /// constraint every other scheduling path already relies on).</summary>
    Task<PublishSlotResult> PublishSlotAsync(long teacherId, long actingUserId, int countryId, long courseId,
        int levelId, int ageGroupId, Instant startsAtUtc, int durationMinutes, string scheduleTimeZone,
        string localStartText, SessionType sessionType, CancellationToken cancellationToken);
}
