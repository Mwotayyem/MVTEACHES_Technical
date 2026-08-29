namespace MVTeaches.Domain.Placement;

/// <summary>
/// One single-choice question belonging to a <see cref="PlacementTestVersion"/>.
/// Never editable once the parent version is Published — see that class's
/// remarks. The correct answer lives on <see cref="PlacementAnswerChoice"/>,
/// never here and never sent to the browser before submission
/// (IPlacementAttemptService.StartAttemptAsync strips it).
/// </summary>
public class PlacementQuestion
{
    public long Id { get; private set; }

    public long TestVersionId { get; private set; }
    public string Text { get; private set; } = string.Empty;

    /// <summary>Must be positive — a zero/negative-point question would let a
    /// score range's total possible score be gamed or become ambiguous.</summary>
    public int Points { get; private set; }

    public int SortOrder { get; private set; }

    private PlacementQuestion() { }

    public PlacementQuestion(long testVersionId, string text, int points, int sortOrder)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("A question needs text.", nameof(text));
        }

        if (points <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(points), "A question must be worth a positive number of points.");
        }

        TestVersionId = testVersionId;
        Text = text;
        Points = points;
        SortOrder = sortOrder;
    }
}
