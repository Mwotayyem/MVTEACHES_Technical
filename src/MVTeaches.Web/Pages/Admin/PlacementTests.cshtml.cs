using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using MVTeaches.Application.Placement;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Placement;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using MVTeaches.Web.Resources;

namespace MVTeaches.Web.Pages.Admin;

/// <summary>
/// Owner decision 2026-08-30, reversing D-48, rule 2: "Do not invent
/// academic questions, answers, or scoring thresholds — build authorized
/// Admin/SystemAdmin management for test versions, questions/choices, correct
/// answers/points, score ranges mapped to levels, draft/published status,
/// activating one published version." This page supplies no content of its
/// own; every question, choice, and score range is admin-entered.
/// IPlacementTestAdminService.PublishAsync is the actual authority on
/// whether a version's ranges are valid — this page only surfaces its
/// (possibly multiple) validation errors.
/// </summary>
[Authorize(Roles = RoleNames.Admin + "," + RoleNames.SystemAdmin)]
public class PlacementTestsModel : PageModel
{
    private readonly MvTeachesDbContext _db;
    private readonly IPlacementTestAdminService _admin;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public PlacementTestsModel(MvTeachesDbContext db, IPlacementTestAdminService admin, UserManager<ApplicationUser> userManager,
        IStringLocalizer<SharedResource> localizer)
    {
        _db = db;
        _admin = admin;
        _userManager = userManager;
        _localizer = localizer;
    }

    public IReadOnlyList<PlacementTestVersion> Versions { get; set; } = Array.Empty<PlacementTestVersion>();
    public IReadOnlyList<Level> Levels { get; set; } = Array.Empty<Level>();
    public IReadOnlyList<PlacementRetakeRequest> PendingRetakeRequests { get; set; } = Array.Empty<PlacementRetakeRequest>();
    public IReadOnlyDictionary<long, string> StudentNamesById { get; set; } = new Dictionary<long, string>();

    /// <summary>Set only when a specific version is being edited — its
    /// questions/choices/score ranges, for the detail section below the list.</summary>
    public long? EditingVersionId { get; set; }
    public PlacementTestVersion? EditingVersion { get; set; }
    public IReadOnlyList<PlacementQuestion> EditingQuestions { get; set; } = Array.Empty<PlacementQuestion>();
    public IReadOnlyDictionary<long, IReadOnlyList<PlacementAnswerChoice>> ChoicesByQuestion { get; set; } =
        new Dictionary<long, IReadOnlyList<PlacementAnswerChoice>>();
    public IReadOnlyList<PlacementScoreRange> EditingScoreRanges { get; set; } = Array.Empty<PlacementScoreRange>();

    [BindProperty]
    public NewVersionInput NewVersion { get; set; } = new();

    [BindProperty]
    public NewQuestionInput NewQuestion { get; set; } = new();

    [BindProperty]
    public NewScoreRangeInput NewScoreRange { get; set; } = new();

    public string? StatusMessage { get; set; }
    public string? ErrorMessage { get; set; }
    public IReadOnlyList<string> ValidationErrors { get; set; } = Array.Empty<string>();

    public class NewVersionInput
    {
        [Required] public string Title { get; set; } = string.Empty;
    }

    public class NewQuestionInput
    {
        [Required] public long TestVersionId { get; set; }
        [Required] public string Text { get; set; } = string.Empty;
        [Required, Range(1, int.MaxValue)] public int Points { get; set; } = 1;
        [Required, Range(0, int.MaxValue)] public int SortOrder { get; set; }

        // A fixed 4-choice shape keeps the admin form simple — the domain
        // itself has no limit on choice count, this is just this page's UI.
        [Required] public string Choice1Text { get; set; } = string.Empty;
        [Required] public string Choice2Text { get; set; } = string.Empty;
        public string? Choice3Text { get; set; }
        public string? Choice4Text { get; set; }
        [Required, Range(1, 4)] public int CorrectChoiceNumber { get; set; } = 1;
    }

    public class NewScoreRangeInput
    {
        [Required] public long TestVersionId { get; set; }
        [Required] public int MinScore { get; set; }
        [Required] public int MaxScore { get; set; }
        [Required] public int LevelId { get; set; }
    }

    public async Task OnGetAsync(long? versionId) => await LoadAsync(versionId);

    public async Task<IActionResult> OnPostCreateVersionAsync()
    {
        ModelState.Clear();
        if (!TryValidateModel(NewVersion, nameof(NewVersion)))
        {
            await LoadAsync(null);
            return Page();
        }

        var actingUserId = GetCurrentUserId();
        var result = await _admin.CreateDraftVersionAsync(NewVersion.Title, actingUserId, HttpContext.RequestAborted);
        StatusMessage = _localizer["Draft version #{0} created.", result.TestVersionId].Value;

        await LoadAsync(result.TestVersionId);
        return Page();
    }

    public async Task<IActionResult> OnPostAddQuestionAsync()
    {
        ModelState.Clear();
        if (!TryValidateModel(NewQuestion, nameof(NewQuestion)))
        {
            await LoadAsync(NewQuestion.TestVersionId);
            return Page();
        }

        var choices = new List<AddQuestionChoice>
        {
            new(NewQuestion.Choice1Text, NewQuestion.CorrectChoiceNumber == 1),
            new(NewQuestion.Choice2Text, NewQuestion.CorrectChoiceNumber == 2),
        };
        if (!string.IsNullOrWhiteSpace(NewQuestion.Choice3Text))
        {
            choices.Add(new AddQuestionChoice(NewQuestion.Choice3Text, NewQuestion.CorrectChoiceNumber == 3));
        }
        if (!string.IsNullOrWhiteSpace(NewQuestion.Choice4Text))
        {
            choices.Add(new AddQuestionChoice(NewQuestion.Choice4Text, NewQuestion.CorrectChoiceNumber == 4));
        }

        try
        {
            await _admin.AddQuestionAsync(NewQuestion.TestVersionId, NewQuestion.Text, NewQuestion.Points,
                choices, NewQuestion.SortOrder, HttpContext.RequestAborted);
            StatusMessage = _localizer["Question added."].Value;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            ErrorMessage = ex.Message;
        }

        await LoadAsync(NewQuestion.TestVersionId);
        return Page();
    }

    public async Task<IActionResult> OnPostRemoveQuestionAsync(long questionId, long versionId)
    {
        await _admin.RemoveQuestionAsync(questionId, HttpContext.RequestAborted);
        StatusMessage = _localizer["Question removed."].Value;
        await LoadAsync(versionId);
        return Page();
    }

    public async Task<IActionResult> OnPostAddScoreRangeAsync()
    {
        ModelState.Clear();
        if (!TryValidateModel(NewScoreRange, nameof(NewScoreRange)))
        {
            await LoadAsync(NewScoreRange.TestVersionId);
            return Page();
        }

        try
        {
            await _admin.AddScoreRangeAsync(NewScoreRange.TestVersionId, NewScoreRange.MinScore,
                NewScoreRange.MaxScore, NewScoreRange.LevelId, HttpContext.RequestAborted);
            StatusMessage = _localizer["Score range added."].Value;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            ErrorMessage = ex.Message;
        }

        await LoadAsync(NewScoreRange.TestVersionId);
        return Page();
    }

    public async Task<IActionResult> OnPostRemoveScoreRangeAsync(long scoreRangeId, long versionId)
    {
        await _admin.RemoveScoreRangeAsync(scoreRangeId, HttpContext.RequestAborted);
        StatusMessage = _localizer["Score range removed."].Value;
        await LoadAsync(versionId);
        return Page();
    }

    public async Task<IActionResult> OnPostPublishAsync(long versionId)
    {
        var actingUserId = GetCurrentUserId();
        var result = await _admin.PublishAsync(versionId, actingUserId, HttpContext.RequestAborted);

        if (result.Outcome == PublishOutcome.Published)
        {
            StatusMessage = _localizer["Version published."].Value;
        }
        else
        {
            ValidationErrors = result.ValidationErrors;
            ErrorMessage = result.Outcome switch
            {
                PublishOutcome.VersionNotFound => _localizer["Version not found."].Value,
                PublishOutcome.AlreadyPublished => _localizer["This version is already published."].Value,
                PublishOutcome.ValidationFailed => _localizer["This version cannot be published yet — see the errors below."].Value,
                _ => _localizer["Could not publish this version."].Value,
            };
        }

        await LoadAsync(versionId);
        return Page();
    }

    public async Task<IActionResult> OnPostActivateAsync(long versionId)
    {
        var result = await _admin.ActivateAsync(versionId, HttpContext.RequestAborted);
        StatusMessage = result == ActivateOutcome.Activated ? _localizer["Version activated — new attempts will use it."].Value : null;
        ErrorMessage = result switch
        {
            ActivateOutcome.VersionNotFound => _localizer["Version not found."].Value,
            ActivateOutcome.NotPublished => _localizer["Only a published version can be activated."].Value,
            _ => null,
        };

        await LoadAsync(versionId);
        return Page();
    }

    public async Task<IActionResult> OnPostApproveRetakeAsync(long retakeRequestId, string? reason)
    {
        var actingUserId = GetCurrentUserId();
        var result = await _admin.ApproveRetakeAsync(retakeRequestId, actingUserId, reason, HttpContext.RequestAborted);
        StatusMessage = result == RetakeDecisionOutcome.Decided ? _localizer["Retake approved."].Value : null;
        ErrorMessage = result == RetakeDecisionOutcome.AlreadyDecided ? _localizer["This request was already decided."].Value : null;

        await LoadAsync(null);
        return Page();
    }

    public async Task<IActionResult> OnPostRejectRetakeAsync(long retakeRequestId, string reason)
    {
        var actingUserId = GetCurrentUserId();
        if (string.IsNullOrWhiteSpace(reason))
        {
            ErrorMessage = _localizer["A reason is required to reject a retake request."].Value;
            await LoadAsync(null);
            return Page();
        }

        var result = await _admin.RejectRetakeAsync(retakeRequestId, actingUserId, reason, HttpContext.RequestAborted);
        StatusMessage = result == RetakeDecisionOutcome.Decided ? _localizer["Retake rejected."].Value : null;
        ErrorMessage = result == RetakeDecisionOutcome.AlreadyDecided ? _localizer["This request was already decided."].Value : null;

        await LoadAsync(null);
        return Page();
    }

    private long GetCurrentUserId() => long.Parse(_userManager.GetUserId(User)!);

    private async Task LoadAsync(long? versionId)
    {
        Versions = (await _admin.ListVersionsAsync(HttpContext.RequestAborted)).OrderByDescending(v => v.Id).ToList();
        Levels = await _db.Levels.Where(l => l.IsActive).OrderBy(l => l.SortOrder).ToListAsync();

        PendingRetakeRequests = await _admin.ListPendingRetakeRequestsAsync(HttpContext.RequestAborted);
        var studentIds = PendingRetakeRequests.Select(r => r.StudentId).Distinct().ToList();
        StudentNamesById = await _db.Students
            .Where(s => studentIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.FullName);

        EditingVersionId = versionId;
        if (versionId is null)
        {
            return;
        }

        EditingVersion = Versions.FirstOrDefault(v => v.Id == versionId);
        if (EditingVersion is null)
        {
            return;
        }

        EditingQuestions = await _admin.GetQuestionsAsync(versionId.Value, HttpContext.RequestAborted);
        var choicesByQuestion = new Dictionary<long, IReadOnlyList<PlacementAnswerChoice>>();
        foreach (var q in EditingQuestions)
        {
            choicesByQuestion[q.Id] = await _admin.GetChoicesAsync(q.Id, HttpContext.RequestAborted);
        }
        ChoicesByQuestion = choicesByQuestion;

        EditingScoreRanges = await _admin.GetScoreRangesAsync(versionId.Value, HttpContext.RequestAborted);
    }
}
