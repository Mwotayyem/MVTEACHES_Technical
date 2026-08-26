using NodaTime;

namespace MVTeaches.Domain.Placement;

public enum PlacementInterviewStatus
{
    Scheduled,
    Completed,
    Cancelled,
    NoShow,
}

/// <summary>
/// Technical Study §10 (D-48, replacing the cancelled exam model; D-84: always
/// free, retake allowed with admin approval). Called `placement_interviews` in
/// the entity map (renamed from `placement_attempts` — "attempt" implies an
/// exam that no longer exists). The teacher meets the student over Zoom and
/// assigns a level by professional judgment; there is no question bank, no
/// scoring engine, and none should ever be added here.
/// </summary>
public class PlacementInterview
{
    public long Id { get; private set; }

    public long StudentId { get; private set; }
    public long? InterviewerTeacherId { get; private set; }

    public Instant ScheduledAtUtc { get; private set; }
    public PlacementInterviewStatus Status { get; private set; } = PlacementInterviewStatus.Scheduled;

    public int? AssignedLevelId { get; private set; }
    public string? Notes { get; private set; }

    private PlacementInterview() { }

    public PlacementInterview(long studentId, Instant scheduledAtUtc, long? interviewerTeacherId = null)
    {
        StudentId = studentId;
        ScheduledAtUtc = scheduledAtUtc;
        InterviewerTeacherId = interviewerTeacherId;
    }

    public void Complete(long interviewerTeacherId, int assignedLevelId, string? notes)
    {
        if (Status != PlacementInterviewStatus.Scheduled)
        {
            throw new InvalidOperationException($"Cannot complete an interview in state {Status}.");
        }

        InterviewerTeacherId = interviewerTeacherId;
        AssignedLevelId = assignedLevelId;
        Notes = notes;
        Status = PlacementInterviewStatus.Completed;
    }

    public void Cancel() => Status = PlacementInterviewStatus.Cancelled;

    public void MarkNoShow() => Status = PlacementInterviewStatus.NoShow;
}
