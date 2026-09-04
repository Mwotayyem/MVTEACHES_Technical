using NodaTime;

namespace MVTeaches.Domain.Placement;

public enum PlacementTestStatus
{
    Draft,
    Published,
}

/// <summary>
/// Owner decision 2026-08-30, reversing D-48 ("no student-submitted placement
/// exam") by explicit owner confirmation after the conflict was raised. See
/// MVTEACHES_Owner_Answers_R3.md for the full record — D-48's original text is
/// left in place there, marked superseded, not deleted.
///
/// A version's questions/choices/score-ranges are freely editable ONLY while
/// Draft. Publishing freezes it permanently (no method on this class ever
/// moves a Published version back to Draft) — a later change means creating a
/// new draft version, never rewriting a version students may have already
/// been scored against. Exactly one version may be <see cref="IsActive"/> at
/// a time (ux_placement_test_active in Infrastructure), and only a Published
/// version can be active — new attempts are always created against whichever
/// one that is.
/// </summary>
public class PlacementTestVersion
{
    public long Id { get; private set; }

    public string Title { get; private set; } = string.Empty;

    /// <summary>Owner decision 2026-09-04: which course this test places students
    /// into. A level now belongs to a course (see StudentLevel), so a test that
    /// did not know its own course could only ever write levels into whichever
    /// course happened to be assumed — which is exactly the confusion the
    /// multi-course work exists to end. Backfilled for existing versions to the
    /// centre's original course, the only one that existed when they were
    /// written.</summary>
    public long CourseId { get; private set; }

    public PlacementTestStatus Status { get; private set; } = PlacementTestStatus.Draft;
    public bool IsActive { get; private set; }

    public long CreatedByUserId { get; private set; }
    public Instant CreatedAtUtc { get; private set; }
    public long? PublishedByUserId { get; private set; }
    public Instant? PublishedAtUtc { get; private set; }

    private PlacementTestVersion() { }

    public PlacementTestVersion(string title, long courseId, long createdByUserId, Instant createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("A test version needs a title.", nameof(title));
        }

        Title = title;
        CourseId = courseId;
        CreatedByUserId = createdByUserId;
        CreatedAtUtc = createdAtUtc;
    }

    public void EnsureEditable()
    {
        if (Status != PlacementTestStatus.Draft)
        {
            throw new InvalidOperationException("A published placement test version can never be edited — create a new draft version instead.");
        }
    }

    /// <summary>The caller (IPlacementTestAdminService) must have already run
    /// the full validation this class's own remarks describe — this method
    /// only performs the state transition, not the validation itself, since
    /// the validation needs to inspect the version's questions/choices/ranges,
    /// which live in separate tables this entity does not hold references to.</summary>
    public void Publish(long publishedByUserId, Instant nowUtc)
    {
        EnsureEditable();
        Status = PlacementTestStatus.Published;
        PublishedByUserId = publishedByUserId;
        PublishedAtUtc = nowUtc;
    }

    public void Activate()
    {
        if (Status != PlacementTestStatus.Published)
        {
            throw new InvalidOperationException("Only a published test version can be activated.");
        }

        IsActive = true;
    }

    public void Deactivate() => IsActive = false;
}
