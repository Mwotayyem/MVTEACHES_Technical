using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Placement;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Placement;
using MVTeaches.Infrastructure.Persistence;
using MVTeaches.Infrastructure.Placement;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace MVTeaches.Tests.Placement;

/// <summary>
/// Owner decision 2026-08-30, reversing D-48 ("no student-submitted placement
/// exam") by explicit owner confirmation — see MVTEACHES_Owner_Answers_R3.md.
/// "Do not invent academic questions, answers, or scoring thresholds": every
/// question/choice/range in these tests is deliberately trivial placeholder
/// content, never real curriculum. This covers the admin-authoring half;
/// PlacementAttemptServiceTests covers the student-facing scoring half.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class PlacementTestAdminServiceTests
{
    private readonly TestDatabaseFixture _fixture;
    private static long _idSeed = 71_000_000;

    public PlacementTestAdminServiceTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static long NextId() => Interlocked.Increment(ref _idSeed);

    private static IPlacementTestAdminService CreateService(MvTeachesDbContext db) =>
        new PlacementTestAdminService(db, new FakeClock(SystemClock.Instance.GetCurrentInstant()),
            TestLocalization.For<MVTeaches.Infrastructure.Resources.InfrastructureResource>());

    private static async Task<(int LevelA, int LevelB)> SeedTwoLevelsAsync(MvTeachesDbContext db)
    {
        var levelA = (int)NextId();
        var levelB = (int)NextId();
        db.Levels.Add(new Level(levelA, "L" + levelA, "مستوى", "Level A", levelA));
        db.Levels.Add(new Level(levelB, "L" + levelB, "مستوى", "Level B", levelB));
        await db.SaveChangesAsync();
        return (levelA, levelB);
    }

    /// <summary>A minimal, always-publishable version: two 5-point questions
    /// (10 total) and two score ranges [0,4]->LevelA, [5,10]->LevelB.</summary>
    private static async Task<long> BuildPublishableDraftAsync(IPlacementTestAdminService service, int levelA, int levelB)
    {
        var version = await service.CreateDraftVersionAsync("Test v1", NextId(), CancellationToken.None);
        await service.AddQuestionAsync(version.TestVersionId, "1+1=?", 5,
            new[] { new AddQuestionChoice("2", true), new AddQuestionChoice("3", false) }, 0, CancellationToken.None);
        await service.AddQuestionAsync(version.TestVersionId, "2+2=?", 5,
            new[] { new AddQuestionChoice("4", true), new AddQuestionChoice("5", false) }, 1, CancellationToken.None);
        await service.AddScoreRangeAsync(version.TestVersionId, 0, 4, levelA, CancellationToken.None);
        await service.AddScoreRangeAsync(version.TestVersionId, 5, 10, levelB, CancellationToken.None);
        return version.TestVersionId;
    }

    [Fact]
    public async Task A_fully_valid_draft_publishes_successfully()
    {
        await using var db = _fixture.CreateContext();
        var (levelA, levelB) = await SeedTwoLevelsAsync(db);
        var service = CreateService(db);
        var versionId = await BuildPublishableDraftAsync(service, levelA, levelB);

        var result = await service.PublishAsync(versionId, NextId(), CancellationToken.None);

        Assert.Equal(PublishOutcome.Published, result.Outcome);
        Assert.Empty(result.ValidationErrors);
        var version = await db.PlacementTestVersions.FirstAsync(v => v.Id == versionId);
        Assert.Equal(PlacementTestStatus.Published, version.Status);
    }

    [Fact]
    public async Task Publishing_with_no_questions_is_refused()
    {
        await using var db = _fixture.CreateContext();
        var (levelA, _) = await SeedTwoLevelsAsync(db);
        var service = CreateService(db);
        var version = await service.CreateDraftVersionAsync("Empty", NextId(), CancellationToken.None);
        await service.AddScoreRangeAsync(version.TestVersionId, 0, 10, levelA, CancellationToken.None);

        var result = await service.PublishAsync(version.TestVersionId, NextId(), CancellationToken.None);

        Assert.Equal(PublishOutcome.ValidationFailed, result.Outcome);
        Assert.Contains(result.ValidationErrors, e => e.Contains("At least one question"));
    }

    [Fact]
    public async Task A_question_with_no_correct_choice_blocks_publishing()
    {
        await using var db = _fixture.CreateContext();
        var (levelA, _) = await SeedTwoLevelsAsync(db);
        var service = CreateService(db);
        var version = await service.CreateDraftVersionAsync("Bad question", NextId(), CancellationToken.None);

        // AddQuestionAsync itself throws for zero-correct-choices — this proves
        // the guard exists at the point of adding, not only at publish time.
        await Assert.ThrowsAsync<ArgumentException>(() => service.AddQuestionAsync(version.TestVersionId, "Q", 5,
            new[] { new AddQuestionChoice("a", false), new AddQuestionChoice("b", false) }, 0, CancellationToken.None));
    }

    [Fact]
    public async Task A_gap_between_score_ranges_blocks_publishing()
    {
        await using var db = _fixture.CreateContext();
        var (levelA, levelB) = await SeedTwoLevelsAsync(db);
        var service = CreateService(db);
        var version = await service.CreateDraftVersionAsync("Gap", NextId(), CancellationToken.None);
        await service.AddQuestionAsync(version.TestVersionId, "Q", 10,
            new[] { new AddQuestionChoice("a", true), new AddQuestionChoice("b", false) }, 0, CancellationToken.None);
        await service.AddScoreRangeAsync(version.TestVersionId, 0, 3, levelA, CancellationToken.None);
        await service.AddScoreRangeAsync(version.TestVersionId, 5, 10, levelB, CancellationToken.None); // gap at 4

        var result = await service.PublishAsync(version.TestVersionId, NextId(), CancellationToken.None);

        Assert.Equal(PublishOutcome.ValidationFailed, result.Outcome);
        Assert.Contains(result.ValidationErrors, e => e.Contains("gap or overlap"));
    }

    [Fact]
    public async Task An_overlap_between_score_ranges_blocks_publishing()
    {
        await using var db = _fixture.CreateContext();
        var (levelA, levelB) = await SeedTwoLevelsAsync(db);
        var service = CreateService(db);
        var version = await service.CreateDraftVersionAsync("Overlap", NextId(), CancellationToken.None);
        await service.AddQuestionAsync(version.TestVersionId, "Q", 10,
            new[] { new AddQuestionChoice("a", true), new AddQuestionChoice("b", false) }, 0, CancellationToken.None);
        await service.AddScoreRangeAsync(version.TestVersionId, 0, 6, levelA, CancellationToken.None);
        await service.AddScoreRangeAsync(version.TestVersionId, 5, 10, levelB, CancellationToken.None); // overlaps at 5-6

        var result = await service.PublishAsync(version.TestVersionId, NextId(), CancellationToken.None);

        Assert.Equal(PublishOutcome.ValidationFailed, result.Outcome);
        Assert.Contains(result.ValidationErrors, e => e.Contains("gap or overlap"));
    }

    [Fact]
    public async Task Score_ranges_not_starting_at_zero_or_not_reaching_the_total_are_refused()
    {
        await using var db = _fixture.CreateContext();
        var (levelA, _) = await SeedTwoLevelsAsync(db);
        var service = CreateService(db);
        var version = await service.CreateDraftVersionAsync("Incomplete coverage", NextId(), CancellationToken.None);
        await service.AddQuestionAsync(version.TestVersionId, "Q", 10,
            new[] { new AddQuestionChoice("a", true), new AddQuestionChoice("b", false) }, 0, CancellationToken.None);
        await service.AddScoreRangeAsync(version.TestVersionId, 2, 8, levelA, CancellationToken.None); // misses 0,1 and 9,10

        var result = await service.PublishAsync(version.TestVersionId, NextId(), CancellationToken.None);

        Assert.Equal(PublishOutcome.ValidationFailed, result.Outcome);
        Assert.Contains(result.ValidationErrors, e => e.Contains("must start at 0"));
        Assert.Contains(result.ValidationErrors, e => e.Contains("total possible score"));
    }

    [Fact]
    public async Task A_published_version_can_never_be_edited_again()
    {
        await using var db = _fixture.CreateContext();
        var (levelA, levelB) = await SeedTwoLevelsAsync(db);
        var service = CreateService(db);
        var versionId = await BuildPublishableDraftAsync(service, levelA, levelB);
        await service.PublishAsync(versionId, NextId(), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddQuestionAsync(versionId, "New Q", 5,
            new[] { new AddQuestionChoice("a", true), new AddQuestionChoice("b", false) }, 2, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddScoreRangeAsync(versionId, 11, 20, levelA, CancellationToken.None));

        var secondPublish = await service.PublishAsync(versionId, NextId(), CancellationToken.None);
        Assert.Equal(PublishOutcome.AlreadyPublished, secondPublish.Outcome);
    }

    [Fact]
    public async Task Activating_a_version_deactivates_the_previously_active_one()
    {
        await using var db = _fixture.CreateContext();
        var (levelA, levelB) = await SeedTwoLevelsAsync(db);
        var service = CreateService(db);
        var v1 = await BuildPublishableDraftAsync(service, levelA, levelB);
        await service.PublishAsync(v1, NextId(), CancellationToken.None);
        await service.ActivateAsync(v1, CancellationToken.None);

        var v2 = await BuildPublishableDraftAsync(service, levelA, levelB);
        await service.PublishAsync(v2, NextId(), CancellationToken.None);
        var activateResult = await service.ActivateAsync(v2, CancellationToken.None);

        Assert.Equal(ActivateOutcome.Activated, activateResult);
        await using var verify = _fixture.CreateContext();
        Assert.False((await verify.PlacementTestVersions.FirstAsync(v => v.Id == v1)).IsActive);
        Assert.True((await verify.PlacementTestVersions.FirstAsync(v => v.Id == v2)).IsActive);
        Assert.Equal(1, await verify.PlacementTestVersions.CountAsync(v => v.IsActive));
    }

    [Fact]
    public async Task A_draft_or_unpublished_version_cannot_be_activated()
    {
        await using var db = _fixture.CreateContext();
        var service = CreateService(db);
        var draft = await service.CreateDraftVersionAsync("Still draft", NextId(), CancellationToken.None);

        var result = await service.ActivateAsync(draft.TestVersionId, CancellationToken.None);

        Assert.Equal(ActivateOutcome.NotPublished, result);
    }

    [Fact]
    public async Task Approving_a_retake_request_is_audit_logged()
    {
        await using var db = _fixture.CreateContext();
        var studentId = NextId();
        var request = new PlacementRetakeRequest(studentId, NextId(), SystemClock.Instance.GetCurrentInstant());
        db.PlacementRetakeRequests.Add(request);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var adminId = NextId();
        var outcome = await service.ApproveRetakeAsync(request.Id, adminId, "goodwill", CancellationToken.None);

        Assert.Equal(RetakeDecisionOutcome.Decided, outcome);
        await using var verify = _fixture.CreateContext();
        Assert.Equal(PlacementRetakeStatus.Approved, (await verify.PlacementRetakeRequests.FirstAsync(r => r.Id == request.Id)).Status);
        var audit = await verify.AuditLogEntries.SingleAsync(a => a.EntityType == "PlacementRetakeRequest" && a.EntityId == request.Id.ToString());
        Assert.Equal("RetakeApproved", audit.Action);
        Assert.Equal(adminId, audit.PerformedByUserId);
    }

    [Fact]
    public async Task Rejecting_a_retake_request_requires_a_reason_and_is_audit_logged()
    {
        await using var db = _fixture.CreateContext();
        var studentId = NextId();
        var request = new PlacementRetakeRequest(studentId, NextId(), SystemClock.Instance.GetCurrentInstant());
        db.PlacementRetakeRequests.Add(request);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var outcome = await service.RejectRetakeAsync(request.Id, NextId(), "no valid reason given", CancellationToken.None);

        Assert.Equal(RetakeDecisionOutcome.Decided, outcome);
        await using var verify = _fixture.CreateContext();
        Assert.Equal(PlacementRetakeStatus.Rejected, (await verify.PlacementRetakeRequests.FirstAsync(r => r.Id == request.Id)).Status);
        Assert.True(await verify.AuditLogEntries.AnyAsync(a => a.EntityType == "PlacementRetakeRequest" && a.Action == "RetakeRejected"));
    }

    [Fact]
    public async Task A_retake_request_already_decided_cannot_be_decided_again()
    {
        await using var db = _fixture.CreateContext();
        var request = new PlacementRetakeRequest(NextId(), NextId(), SystemClock.Instance.GetCurrentInstant());
        db.PlacementRetakeRequests.Add(request);
        await db.SaveChangesAsync();
        var service = CreateService(db);
        await service.ApproveRetakeAsync(request.Id, NextId(), null, CancellationToken.None);

        var second = await service.RejectRetakeAsync(request.Id, NextId(), "too late", CancellationToken.None);

        Assert.Equal(RetakeDecisionOutcome.AlreadyDecided, second);
    }
}
