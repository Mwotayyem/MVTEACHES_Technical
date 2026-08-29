namespace MVTeaches.Domain.Placement;

/// <summary>
/// Maps an inclusive [MinScore, MaxScore] band of a <see cref="PlacementTestVersion"/>'s
/// total possible score to exactly one <see cref="MVTeaches.Domain.Catalog.Level"/>.
/// IPlacementTestAdminService's publish validation requires every version's
/// full set of ranges to partition [0, totalPossiblePoints] with no gaps and
/// no overlaps before it may ever be published — this class only stores one
/// row of that partition and does not itself see the sibling rows needed to
/// validate the whole set.
/// </summary>
public class PlacementScoreRange
{
    public long Id { get; private set; }

    public long TestVersionId { get; private set; }
    public int MinScore { get; private set; }
    public int MaxScore { get; private set; }
    public int LevelId { get; private set; }

    private PlacementScoreRange() { }

    public PlacementScoreRange(long testVersionId, int minScore, int maxScore, int levelId)
    {
        if (minScore < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minScore));
        }

        if (maxScore < minScore)
        {
            throw new ArgumentException("A score range's max must not be below its min.", nameof(maxScore));
        }

        TestVersionId = testVersionId;
        MinScore = minScore;
        MaxScore = maxScore;
        LevelId = levelId;
    }

    public bool Contains(int score) => score >= MinScore && score <= MaxScore;
}
