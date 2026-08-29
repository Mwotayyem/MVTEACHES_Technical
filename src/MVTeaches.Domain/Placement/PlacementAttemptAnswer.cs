namespace MVTeaches.Domain.Placement;

/// <summary>
/// One submitted answer within a <see cref="PlacementAttempt"/>. Snapshots
/// <see cref="IsCorrectSnapshot"/> and <see cref="PointsAwardedSnapshot"/> at
/// scoring time rather than re-deriving them later from the live
/// PlacementQuestion/PlacementAnswerChoice rows — a question's correct answer
/// is never edited after its version is published, but this snapshot exists
/// for the same historical-fidelity reason every other snapshot in this
/// codebase does (§9.2's golden rule): so this row's own meaning can never
/// silently change out from under a completed attempt.
/// </summary>
public class PlacementAttemptAnswer
{
    public long Id { get; private set; }

    public long AttemptId { get; private set; }
    public long QuestionId { get; private set; }
    public long SelectedAnswerChoiceId { get; private set; }

    public bool IsCorrectSnapshot { get; private set; }
    public int PointsAwardedSnapshot { get; private set; }

    private PlacementAttemptAnswer() { }

    public PlacementAttemptAnswer(long attemptId, long questionId, long selectedAnswerChoiceId,
        bool isCorrectSnapshot, int pointsAwardedSnapshot)
    {
        AttemptId = attemptId;
        QuestionId = questionId;
        SelectedAnswerChoiceId = selectedAnswerChoiceId;
        IsCorrectSnapshot = isCorrectSnapshot;
        PointsAwardedSnapshot = pointsAwardedSnapshot;
    }
}
