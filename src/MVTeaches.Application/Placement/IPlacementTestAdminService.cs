using MVTeaches.Domain.Placement;

namespace MVTeaches.Application.Placement;

public record CreateDraftVersionResult(long TestVersionId);
public record AddQuestionChoice(string Text, bool IsCorrect);
public record AddQuestionResult(long QuestionId);
public record AddScoreRangeResult(long ScoreRangeId);

public enum PublishOutcome
{
    Published,
    VersionNotFound,
    AlreadyPublished,
    ValidationFailed,
}

/// <summary>Every reason a version was refused, together (never just the
/// first one hit) — the admin fixing this by hand needs the whole list, not
/// one error at a time. See IPlacementTestAdminService.PublishAsync.</summary>
public record PublishResult(PublishOutcome Outcome, IReadOnlyList<string> ValidationErrors);

public enum ActivateOutcome
{
    Activated,
    VersionNotFound,
    NotPublished,
}

public enum RetakeDecisionOutcome
{
    Decided,
    RequestNotFound,
    AlreadyDecided,
}

/// <summary>
/// Owner decision 2026-08-30, reversing D-48. Admin/SystemAdmin-only
/// management of placement test versions: "Do not invent academic questions,
/// answers, or scoring thresholds" — this service supplies no content of its
/// own; every question, choice, and score range is admin-entered. "A test
/// cannot be published if its scoring ranges are missing, overlapping,
/// invalid, or do not cover the possible score" is PublishAsync's own
/// validation, run fully server-side, never left to the caller.
/// </summary>
public interface IPlacementTestAdminService
{
    Task<CreateDraftVersionResult> CreateDraftVersionAsync(string title, long courseId, long createdByUserId,
        CancellationToken cancellationToken);

    /// <summary>Throws if the version is not Draft (PlacementTestVersion.EnsureEditable) —
    /// a published version's question bank is frozen. Exactly one of
    /// <paramref name="choices"/> must have IsCorrect true, or this throws.</summary>
    Task<AddQuestionResult> AddQuestionAsync(long testVersionId, string text, int points,
        IReadOnlyList<AddQuestionChoice> choices, int sortOrder, CancellationToken cancellationToken);

    Task RemoveQuestionAsync(long questionId, CancellationToken cancellationToken);

    Task<AddScoreRangeResult> AddScoreRangeAsync(long testVersionId, int minScore, int maxScore, int levelId, CancellationToken cancellationToken);

    Task RemoveScoreRangeAsync(long scoreRangeId, CancellationToken cancellationToken);

    /// <summary>Validates, in full, before writing anything: at least one
    /// question exists; every question has exactly one correct choice; every
    /// score range's LevelId exists; the full set of score ranges partitions
    /// [0, totalPossiblePoints] with no gaps and no overlaps. Freezes the
    /// version permanently on success — see PlacementTestVersion's remarks.</summary>
    Task<PublishResult> PublishAsync(long testVersionId, long publishedByUserId, CancellationToken cancellationToken);

    /// <summary>Deactivates whichever OTHER version is currently active (there
    /// is always at most one) in the same operation — new attempts are always
    /// created against whichever version this call leaves active.</summary>
    Task<ActivateOutcome> ActivateAsync(long testVersionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<PlacementTestVersion>> ListVersionsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<PlacementQuestion>> GetQuestionsAsync(long testVersionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<PlacementAnswerChoice>> GetChoicesAsync(long questionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<PlacementScoreRange>> GetScoreRangesAsync(long testVersionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<PlacementRetakeRequest>> ListPendingRetakeRequestsAsync(CancellationToken cancellationToken);

    /// <summary>Rule 3: "A retake requires explicit Admin approval... Retake
    /// approval and manual override must be audit-logged" — the caller
    /// (Infrastructure) writes the audit entry; this only performs the
    /// domain transition.</summary>
    Task<RetakeDecisionOutcome> ApproveRetakeAsync(long retakeRequestId, long decidedByUserId, string? reason, CancellationToken cancellationToken);

    Task<RetakeDecisionOutcome> RejectRetakeAsync(long retakeRequestId, long decidedByUserId, string reason, CancellationToken cancellationToken);
}
