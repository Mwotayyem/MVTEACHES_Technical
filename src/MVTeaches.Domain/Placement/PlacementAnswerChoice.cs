namespace MVTeaches.Domain.Placement;

/// <summary>
/// One answer option for a <see cref="PlacementQuestion"/>. Exactly one choice
/// per question may have <see cref="IsCorrect"/> = true — enforced by
/// IPlacementTestAdminService's publish validation (§ "cannot publish if...
/// missing/invalid"), not by a database constraint, since the check spans
/// multiple rows. <see cref="IsCorrect"/> is never serialized to the browser
/// before submission (see PlacementQuestionOption in the Application layer,
/// which deliberately omits it) — only the scoring service, running
/// server-side, ever reads it against a submitted answer.
/// </summary>
public class PlacementAnswerChoice
{
    public long Id { get; private set; }

    public long QuestionId { get; private set; }
    public string Text { get; private set; } = string.Empty;
    public bool IsCorrect { get; private set; }
    public int SortOrder { get; private set; }

    private PlacementAnswerChoice() { }

    public PlacementAnswerChoice(long questionId, string text, bool isCorrect, int sortOrder)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("An answer choice needs text.", nameof(text));
        }

        QuestionId = questionId;
        Text = text;
        IsCorrect = isCorrect;
        SortOrder = sortOrder;
    }
}
