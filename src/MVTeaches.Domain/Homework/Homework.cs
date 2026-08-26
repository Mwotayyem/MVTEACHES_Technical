using NodaTime;

namespace MVTeaches.Domain.Homework;

public enum HomeworkKind
{
    Material,
    Assignment,
}

/// <summary>Technical Study §26.1 (D-31): bidirectional — teacher uploads, student submits.</summary>
public class Homework
{
    public long Id { get; private set; }
    public long SessionId { get; private set; }
    public long TeacherId { get; private set; }

    public string Title { get; private set; } = string.Empty;
    public string? Instructions { get; private set; }
    public HomeworkKind Kind { get; private set; }
    public Instant? DueAtUtc { get; private set; }
    public Instant CreatedAtUtc { get; private set; }

    private Homework() { }

    public Homework(long sessionId, long teacherId, string title, string? instructions, HomeworkKind kind,
        Instant? dueAtUtc, Instant createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("A title is required.", nameof(title));
        }

        SessionId = sessionId;
        TeacherId = teacherId;
        Title = title;
        Instructions = instructions;
        Kind = kind;
        DueAtUtc = dueAtUtc;
        CreatedAtUtc = createdAtUtc;
    }
}

/// <summary>
/// UNIQUE(HomeworkId, StudentId) means exactly one submission in MVP (Q-16 left
/// resubmission/late-submission policy open) — see the study's §26.3 note.
/// </summary>
public class HomeworkSubmission
{
    public long Id { get; private set; }
    public long HomeworkId { get; private set; }
    public long StudentId { get; private set; }
    public long FileId { get; private set; }

    public Instant SubmittedAtUtc { get; private set; }

    /// <summary>May be the guardian (D-01), not the student.</summary>
    public long SubmittedByUserId { get; private set; }

    public decimal? Grade { get; private set; }
    public string? Feedback { get; private set; }
    public long? GradedByTeacherId { get; private set; }
    public Instant? GradedAtUtc { get; private set; }

    private HomeworkSubmission() { }

    public HomeworkSubmission(long homeworkId, long studentId, long fileId, long submittedByUserId, Instant submittedAtUtc)
    {
        HomeworkId = homeworkId;
        StudentId = studentId;
        FileId = fileId;
        SubmittedByUserId = submittedByUserId;
        SubmittedAtUtc = submittedAtUtc;
    }

    public void RecordGrade(decimal grade, string? feedback, long gradedByTeacherId, Instant nowUtc)
    {
        Grade = grade;
        Feedback = feedback;
        GradedByTeacherId = gradedByTeacherId;
        GradedAtUtc = nowUtc;
    }
}
