using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Placement;
using MVTeaches.Domain.Audit;
using MVTeaches.Domain.Placement;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;

namespace MVTeaches.Infrastructure.Placement;

/// <inheritdoc cref="IPlacementTestAdminService"/>
public class PlacementTestAdminService : IPlacementTestAdminService
{
    private readonly MvTeachesDbContext _db;
    private readonly IClock _clock;

    public PlacementTestAdminService(MvTeachesDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<CreateDraftVersionResult> CreateDraftVersionAsync(string title, long createdByUserId, CancellationToken cancellationToken)
    {
        var version = new PlacementTestVersion(title, createdByUserId, _clock.GetCurrentInstant());
        _db.PlacementTestVersions.Add(version);
        await _db.SaveChangesAsync(cancellationToken);
        return new CreateDraftVersionResult(version.Id);
    }

    public async Task<AddQuestionResult> AddQuestionAsync(long testVersionId, string text, int points,
        IReadOnlyList<AddQuestionChoice> choices, int sortOrder, CancellationToken cancellationToken)
    {
        var version = await _db.PlacementTestVersions.FirstOrDefaultAsync(v => v.Id == testVersionId, cancellationToken)
            ?? throw new InvalidOperationException("Test version not found.");
        version.EnsureEditable();

        if (choices.Count(c => c.IsCorrect) != 1)
        {
            throw new ArgumentException("A question must have exactly one correct choice.", nameof(choices));
        }

        var question = new PlacementQuestion(testVersionId, text, points, sortOrder);
        _db.PlacementQuestions.Add(question);
        await _db.SaveChangesAsync(cancellationToken); // question needs its Id before the choices below

        for (var i = 0; i < choices.Count; i++)
        {
            _db.PlacementAnswerChoices.Add(new PlacementAnswerChoice(question.Id, choices[i].Text, choices[i].IsCorrect, i));
        }
        await _db.SaveChangesAsync(cancellationToken);

        return new AddQuestionResult(question.Id);
    }

    public async Task RemoveQuestionAsync(long questionId, CancellationToken cancellationToken)
    {
        var question = await _db.PlacementQuestions.FirstOrDefaultAsync(q => q.Id == questionId, cancellationToken);
        if (question is null)
        {
            return;
        }

        var version = await _db.PlacementTestVersions.FirstAsync(v => v.Id == question.TestVersionId, cancellationToken);
        version.EnsureEditable();

        var choices = await _db.PlacementAnswerChoices.Where(c => c.QuestionId == questionId).ToListAsync(cancellationToken);
        _db.PlacementAnswerChoices.RemoveRange(choices);
        _db.PlacementQuestions.Remove(question);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<AddScoreRangeResult> AddScoreRangeAsync(long testVersionId, int minScore, int maxScore, int levelId, CancellationToken cancellationToken)
    {
        var version = await _db.PlacementTestVersions.FirstOrDefaultAsync(v => v.Id == testVersionId, cancellationToken)
            ?? throw new InvalidOperationException("Test version not found.");
        version.EnsureEditable();

        if (!await _db.Levels.AnyAsync(l => l.Id == levelId, cancellationToken))
        {
            throw new InvalidOperationException("Level not found.");
        }

        var range = new PlacementScoreRange(testVersionId, minScore, maxScore, levelId);
        _db.PlacementScoreRanges.Add(range);
        await _db.SaveChangesAsync(cancellationToken);
        return new AddScoreRangeResult(range.Id);
    }

    public async Task RemoveScoreRangeAsync(long scoreRangeId, CancellationToken cancellationToken)
    {
        var range = await _db.PlacementScoreRanges.FirstOrDefaultAsync(r => r.Id == scoreRangeId, cancellationToken);
        if (range is null)
        {
            return;
        }

        var version = await _db.PlacementTestVersions.FirstAsync(v => v.Id == range.TestVersionId, cancellationToken);
        version.EnsureEditable();

        _db.PlacementScoreRanges.Remove(range);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<PublishResult> PublishAsync(long testVersionId, long publishedByUserId, CancellationToken cancellationToken)
    {
        var version = await _db.PlacementTestVersions.FirstOrDefaultAsync(v => v.Id == testVersionId, cancellationToken);
        if (version is null)
        {
            return new PublishResult(PublishOutcome.VersionNotFound, Array.Empty<string>());
        }

        if (version.Status != PlacementTestStatus.Draft)
        {
            return new PublishResult(PublishOutcome.AlreadyPublished, Array.Empty<string>());
        }

        var errors = new List<string>();

        var questions = await _db.PlacementQuestions.Where(q => q.TestVersionId == testVersionId).ToListAsync(cancellationToken);
        if (questions.Count == 0)
        {
            errors.Add("At least one question is required.");
        }

        var questionIds = questions.Select(q => q.Id).ToList();
        var choicesByQuestion = (await _db.PlacementAnswerChoices
                .Where(c => questionIds.Contains(c.QuestionId))
                .ToListAsync(cancellationToken))
            .GroupBy(c => c.QuestionId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var question in questions)
        {
            var choices = choicesByQuestion.GetValueOrDefault(question.Id, new List<PlacementAnswerChoice>());
            var correctCount = choices.Count(c => c.IsCorrect);
            if (choices.Count < 2)
            {
                errors.Add($"Question #{question.Id} needs at least two answer choices.");
            }
            if (correctCount != 1)
            {
                errors.Add($"Question #{question.Id} must have exactly one correct answer choice (found {correctCount}).");
            }
        }

        var totalPossiblePoints = questions.Sum(q => q.Points);

        var ranges = await _db.PlacementScoreRanges
            .Where(r => r.TestVersionId == testVersionId)
            .OrderBy(r => r.MinScore)
            .ToListAsync(cancellationToken);

        if (ranges.Count == 0)
        {
            errors.Add("At least one score range is required.");
        }
        else if (totalPossiblePoints > 0)
        {
            var levelIds = (await _db.Levels.Select(l => l.Id).ToListAsync(cancellationToken)).ToHashSet();
            foreach (var range in ranges)
            {
                if (!levelIds.Contains(range.LevelId))
                {
                    errors.Add($"Score range [{range.MinScore},{range.MaxScore}] points to a level that does not exist.");
                }
            }

            // Owner requirement: "cannot publish if scoring ranges are missing,
            // overlapping, invalid, or do not cover the possible score" — the
            // full partition [0, totalPossiblePoints] must be covered exactly
            // once, no gaps, no overlaps.
            if (ranges[0].MinScore != 0)
            {
                errors.Add($"Score ranges must start at 0 (the lowest range starts at {ranges[0].MinScore}).");
            }

            for (var i = 0; i < ranges.Count; i++)
            {
                if (i > 0 && ranges[i].MinScore != ranges[i - 1].MaxScore + 1)
                {
                    errors.Add($"Score ranges must have no gap or overlap between [{ranges[i - 1].MinScore},{ranges[i - 1].MaxScore}] and [{ranges[i].MinScore},{ranges[i].MaxScore}].");
                }
            }

            if (ranges[^1].MaxScore != totalPossiblePoints)
            {
                errors.Add($"Score ranges must cover up to the total possible score ({totalPossiblePoints}); the highest range currently ends at {ranges[^1].MaxScore}.");
            }
        }

        if (errors.Count > 0)
        {
            return new PublishResult(PublishOutcome.ValidationFailed, errors);
        }

        version.Publish(publishedByUserId, _clock.GetCurrentInstant());
        await _db.SaveChangesAsync(cancellationToken);
        return new PublishResult(PublishOutcome.Published, Array.Empty<string>());
    }

    public async Task<ActivateOutcome> ActivateAsync(long testVersionId, CancellationToken cancellationToken)
    {
        var version = await _db.PlacementTestVersions.FirstOrDefaultAsync(v => v.Id == testVersionId, cancellationToken);
        if (version is null)
        {
            return ActivateOutcome.VersionNotFound;
        }

        if (version.Status != PlacementTestStatus.Published)
        {
            return ActivateOutcome.NotPublished;
        }

        // Exactly one active version at a time — ux_placement_test_active
        // (a partial unique index on IsActive, non-deferred like every other
        // constraint in this codebase) is the real backstop. It is checked
        // per-statement, not per-transaction, so deactivating the old holder
        // and activating the new one must be two SEPARATE SaveChanges calls —
        // batching them together risks the activate UPDATE executing before
        // the deactivate one within the same round trip, which would violate
        // the index even though the end state is fine.
        var currentlyActive = await _db.PlacementTestVersions.Where(v => v.IsActive && v.Id != testVersionId).ToListAsync(cancellationToken);
        foreach (var other in currentlyActive)
        {
            other.Deactivate();
        }
        if (currentlyActive.Count > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }

        version.Activate();
        await _db.SaveChangesAsync(cancellationToken);
        return ActivateOutcome.Activated;
    }

    public async Task<IReadOnlyList<PlacementTestVersion>> ListVersionsAsync(CancellationToken cancellationToken) =>
        await _db.PlacementTestVersions.OrderByDescending(v => v.Id).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<PlacementQuestion>> GetQuestionsAsync(long testVersionId, CancellationToken cancellationToken) =>
        await _db.PlacementQuestions.Where(q => q.TestVersionId == testVersionId).OrderBy(q => q.SortOrder).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<PlacementAnswerChoice>> GetChoicesAsync(long questionId, CancellationToken cancellationToken) =>
        await _db.PlacementAnswerChoices.Where(c => c.QuestionId == questionId).OrderBy(c => c.SortOrder).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<PlacementScoreRange>> GetScoreRangesAsync(long testVersionId, CancellationToken cancellationToken) =>
        await _db.PlacementScoreRanges.Where(r => r.TestVersionId == testVersionId).OrderBy(r => r.MinScore).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<PlacementRetakeRequest>> ListPendingRetakeRequestsAsync(CancellationToken cancellationToken) =>
        await _db.PlacementRetakeRequests.Where(r => r.Status == PlacementRetakeStatus.Pending).OrderBy(r => r.RequestedAtUtc).ToListAsync(cancellationToken);

    public async Task<RetakeDecisionOutcome> ApproveRetakeAsync(long retakeRequestId, long decidedByUserId, string? reason, CancellationToken cancellationToken)
    {
        var request = await _db.PlacementRetakeRequests.FirstOrDefaultAsync(r => r.Id == retakeRequestId, cancellationToken);
        if (request is null)
        {
            return RetakeDecisionOutcome.RequestNotFound;
        }

        if (request.Status != PlacementRetakeStatus.Pending)
        {
            return RetakeDecisionOutcome.AlreadyDecided;
        }

        var now = _clock.GetCurrentInstant();
        request.Approve(decidedByUserId, reason, now);

        // Rule 3: "Retake approval and manual override must be audit-logged."
        _db.AuditLogEntries.Add(new AuditLogEntry("PlacementRetakeRequest", retakeRequestId.ToString(), "RetakeApproved",
            decidedByUserId, reason, beforeJson: null, afterJson: null, now));
        await _db.SaveChangesAsync(cancellationToken);
        return RetakeDecisionOutcome.Decided;
    }

    public async Task<RetakeDecisionOutcome> RejectRetakeAsync(long retakeRequestId, long decidedByUserId, string reason, CancellationToken cancellationToken)
    {
        var request = await _db.PlacementRetakeRequests.FirstOrDefaultAsync(r => r.Id == retakeRequestId, cancellationToken);
        if (request is null)
        {
            return RetakeDecisionOutcome.RequestNotFound;
        }

        if (request.Status != PlacementRetakeStatus.Pending)
        {
            return RetakeDecisionOutcome.AlreadyDecided;
        }

        var now = _clock.GetCurrentInstant();
        request.Reject(decidedByUserId, reason, now);

        _db.AuditLogEntries.Add(new AuditLogEntry("PlacementRetakeRequest", retakeRequestId.ToString(), "RetakeRejected",
            decidedByUserId, reason, beforeJson: null, afterJson: null, now));
        await _db.SaveChangesAsync(cancellationToken);
        return RetakeDecisionOutcome.Decided;
    }
}
