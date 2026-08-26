using NodaTime;

namespace MVTeaches.Domain.Scheduling;

public enum RecurringScheduleStatus
{
    Active,
    Paused,
    Ended,
}

/// <summary>
/// Technical Study §15.2 (D-23). The student is assigned to a fixed weekly
/// schedule — there is no free-form booking calendar (see §15.1: this
/// deliberately drops the interactive slot-picker and the last-seat race
/// condition it would otherwise create).
/// </summary>
public class RecurringSchedule
{
    public long Id { get; private set; }

    public int CountryId { get; private set; }
    public long CourseId { get; private set; }
    public int LevelId { get; private set; }
    public int AgeGroupId { get; private set; }
    public long TeacherId { get; private set; }

    /// <summary>ISO day-of-week values, e.g. {1,3} = Monday+Wednesday.</summary>
    public IReadOnlyList<IsoDayOfWeek> DaysOfWeek { get; private set; } = Array.Empty<IsoDayOfWeek>();

    public LocalTime StartLocal { get; private set; }
    public int DurationMinutes { get; private set; } = 60;
    public string TimeZoneId { get; private set; } = string.Empty;

    public LocalDate StartsOn { get; private set; }
    public LocalDate? EndsOn { get; private set; }

    public int Capacity { get; private set; } = 4;
    public RecurringScheduleStatus Status { get; private set; } = RecurringScheduleStatus.Active;
    public long CreatedByUserId { get; private set; }

    private RecurringSchedule() { }

    public RecurringSchedule(int countryId, long courseId, int levelId, int ageGroupId, long teacherId,
        IReadOnlyList<IsoDayOfWeek> daysOfWeek, LocalTime startLocal, int durationMinutes, string timeZoneId,
        LocalDate startsOn, int capacity, long createdByUserId)
    {
        if (daysOfWeek.Count is < 1 or > 7)
        {
            throw new ArgumentException("days_of_week must contain between 1 and 7 entries.", nameof(daysOfWeek));
        }

        CountryId = countryId;
        CourseId = courseId;
        LevelId = levelId;
        AgeGroupId = ageGroupId;
        TeacherId = teacherId;
        DaysOfWeek = daysOfWeek;
        StartLocal = startLocal;
        DurationMinutes = durationMinutes;
        TimeZoneId = timeZoneId;
        StartsOn = startsOn;
        Capacity = capacity;
        CreatedByUserId = createdByUserId;
    }

    /// <summary>§15.3: changing a schedule affects FUTURE generation only — it
    /// must never rewrite past or already-delivered sessions.</summary>
    public void Pause() => Status = RecurringScheduleStatus.Paused;

    public void Resume() => Status = RecurringScheduleStatus.Active;

    public void End(LocalDate endsOn) => (Status, EndsOn) = (RecurringScheduleStatus.Ended, endsOn);
}
