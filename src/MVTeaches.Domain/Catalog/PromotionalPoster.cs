using NodaTime;

namespace MVTeaches.Domain.Catalog;

/// <summary>
/// Owner decision 2026-09-01 (approved for a schema change): an offer poster
/// the centre publishes to students — an image, a title, and the details
/// underneath it — managed entirely from the admin screen.
///
/// Deliberately NOT a pricing plan and never a price: a poster advertises,
/// it does not sell. Nothing here is read by any purchase, subscription, or
/// payment path; a student still buys through <see cref="PricingPlan"/> and
/// its own eligibility rules, exactly as before. <see cref="LevelId"/> and
/// <see cref="PricingPlanId"/> are both optional, and neither grants
/// anything — they only let the admin say what a poster is about, and let
/// the student screen put it next to the right package.
///
/// One image per poster, replaced in place. The owner asked explicitly that
/// a re-upload replace the old file rather than pile up versions, so
/// <see cref="ReplaceImage"/> hands back the id of the file it displaced and
/// the caller deletes it — the poster row never accumulates an archive.
/// </summary>
public class PromotionalPoster
{
    public long Id { get; private set; }

    public string Title { get; private set; } = string.Empty;

    /// <summary>The text that appears under the image. Free-form; the admin
    /// writes whatever the offer needs to say.</summary>
    public string? Details { get; private set; }

    /// <summary>The uploaded image, as a <c>FileRecord</c> id. Null until an
    /// image is attached — a poster with only text still renders.</summary>
    public long? ImageFileId { get; private set; }

    /// <summary>Hidden posters stay in the admin list and disappear from the
    /// student's screen. Nothing is deleted to hide something.</summary>
    public bool IsActive { get; private set; }

    /// <summary>Lowest first. Two posters sharing a number fall back to the
    /// order they were created in.</summary>
    public int SortOrder { get; private set; }

    public int? LevelId { get; private set; }
    public long? PricingPlanId { get; private set; }

    public long CreatedByUserId { get; private set; }
    public Instant CreatedAtUtc { get; private set; }
    public Instant UpdatedAtUtc { get; private set; }

    private PromotionalPoster() { }

    public PromotionalPoster(string title, string? details, bool isActive, int sortOrder,
        int? levelId, long? pricingPlanId, long createdByUserId, Instant createdAtUtc)
    {
        Title = Require(title);
        Details = Trim(details);
        IsActive = isActive;
        SortOrder = sortOrder;
        LevelId = levelId;
        PricingPlanId = pricingPlanId;
        CreatedByUserId = createdByUserId;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public void Update(string title, string? details, bool isActive, int sortOrder,
        int? levelId, long? pricingPlanId, Instant updatedAtUtc)
    {
        Title = Require(title);
        Details = Trim(details);
        IsActive = isActive;
        SortOrder = sortOrder;
        LevelId = levelId;
        PricingPlanId = pricingPlanId;
        UpdatedAtUtc = updatedAtUtc;
    }

    /// <summary>Points the poster at a new image and returns the id of the one
    /// it replaced, so the caller can delete those bytes. Returns null when
    /// there was nothing to replace.</summary>
    public long? ReplaceImage(long newImageFileId, Instant updatedAtUtc)
    {
        var displaced = ImageFileId == newImageFileId ? null : ImageFileId;
        ImageFileId = newImageFileId;
        UpdatedAtUtc = updatedAtUtc;
        return displaced;
    }

    private static string Require(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A poster needs a title.", nameof(value));
        }

        return value.Trim();
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
