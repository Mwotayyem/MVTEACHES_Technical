using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using MVTeaches.Application.Placement;
using MVTeaches.Domain.People;
using MVTeaches.Domain.Placement;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using MVTeaches.Web.Resources;
using NodaTime;

namespace MVTeaches.Web.Pages;

/// <summary>
/// Owner decision 2026-08-30, reversing D-48, rules 1 and 3's runtime UI: the
/// free placement test a student must complete before purchasing any package,
/// and the admin-approved retake cycle. Shared by both Student and Guardian
/// accounts (a guardian acts on behalf of one specific, explicitly chosen
/// child) rather than duplicated per role, since every write here is a thin
/// pass-through to IPlacementAttemptService, and that service — not this
/// page — is what actually enforces "acting user must be the student
/// themself or one of their active guardians" (the same IDOR guard
/// JoinAttendanceService and SubscriptionService.PurchaseFromPlanAsync use).
/// A guardian's studentId always arrives as an ordinary form/query value here,
/// exactly like Guardian/MyChildren.cshtml.cs's own OnPostJoinAsync — the
/// service call underneath is the actual authority, not this page.
/// </summary>
[Authorize(Roles = RoleNames.Student + "," + RoleNames.Guardian)]
public class PlacementTestModel : PageModel
{
    private readonly MvTeachesDbContext _db;
    private readonly IPlacementAttemptService _attempts;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public PlacementTestModel(MvTeachesDbContext db, IPlacementAttemptService attempts, UserManager<ApplicationUser> userManager,
        IStringLocalizer<SharedResource> localizer)
    {
        _db = db;
        _attempts = attempts;
        _userManager = userManager;
        _localizer = localizer;
    }

    public record ChildOption(long StudentId, string FullName);
    public record LatestResult(int Score, string LevelCode, Instant CompletedAtUtc);

    public bool NoProfileLinked { get; set; }
    public bool IsGuardian { get; set; }
    public IReadOnlyList<ChildOption> Children { get; set; } = Array.Empty<ChildOption>();
    public long? SelectedStudentId { get; set; }
    public string? SelectedStudentName { get; set; }

    public PlacementEligibilityStatus? Eligibility { get; set; }
    public LatestResult? MostRecentResult { get; set; }

    public long? QuizAttemptId { get; set; }
    public IReadOnlyList<PlacementQuestionForAttempt>? QuizQuestions { get; set; }

    public int? JustScoredPoints { get; set; }
    public string? JustAssignedLevelCode { get; set; }

    public string? StatusMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public class AnswerInput
    {
        public long QuestionId { get; set; }
        public long SelectedChoiceId { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(long? studentId)
    {
        await LoadAsync(studentId);
        return Page();
    }

    /// <summary>Also used to RESUME an already-in-progress attempt —
    /// IPlacementAttemptService.StartAttemptAsync is itself idempotent for a
    /// student with an existing in-progress attempt (it returns that same
    /// attempt's questions again rather than starting a second one), so one
    /// button/handler covers both "take the test" and "resume after a
    /// refresh" without this page needing its own resume logic.</summary>
    public async Task<IActionResult> OnPostStartAsync(long studentId)
    {
        var actingUserId = long.Parse(_userManager.GetUserId(User)!);
        var result = await _attempts.StartAttemptAsync(studentId, actingUserId, HttpContext.RequestAborted);

        if (result.Outcome == StartAttemptOutcome.Started)
        {
            QuizAttemptId = result.AttemptId;
            QuizQuestions = result.Questions;
        }
        else
        {
            ErrorMessage = result.Outcome switch
            {
                StartAttemptOutcome.Unauthorized => _localizer["Not authorized for this student."],
                StartAttemptOutcome.NoActiveTestVersion => _localizer["No placement test is currently published — please contact the centre."],
                StartAttemptOutcome.NotEligible => _localizer["You are not eligible to start a new attempt right now."],
                _ => _localizer["Could not start the test."],
            };
        }

        await LoadAsync(studentId, skipEligibility: result.Outcome == StartAttemptOutcome.Started);
        return Page();
    }

    public async Task<IActionResult> OnPostSubmitAsync(long studentId, long attemptId, List<AnswerInput> answers)
    {
        var actingUserId = long.Parse(_userManager.GetUserId(User)!);
        var answerMap = answers.ToDictionary(a => a.QuestionId, a => a.SelectedChoiceId);
        var result = await _attempts.SubmitAttemptAsync(attemptId, studentId, actingUserId, answerMap, HttpContext.RequestAborted);

        if (result.Outcome == SubmitAttemptOutcome.Scored)
        {
            JustScoredPoints = result.Score;
            var level = await _db.Levels.FirstOrDefaultAsync(l => l.Id == result.AssignedLevelId);
            JustAssignedLevelCode = level?.Code;
            StatusMessage = _localizer["Your placement test has been scored."];
        }
        else
        {
            ErrorMessage = result.Outcome switch
            {
                SubmitAttemptOutcome.Unauthorized => _localizer["Not authorized for this student."],
                SubmitAttemptOutcome.AttemptNotFound => _localizer["Attempt not found."],
                SubmitAttemptOutcome.AlreadyCompleted => _localizer["This attempt was already submitted."],
                SubmitAttemptOutcome.MissingAnswers => _localizer["Please answer every question before submitting."],
                SubmitAttemptOutcome.InvalidChoiceForQuestion => _localizer["One of the submitted answers was invalid."],
                _ => _localizer["Could not submit the test."],
            };
        }

        await LoadAsync(studentId);
        return Page();
    }

    public async Task<IActionResult> OnPostRequestRetakeAsync(long studentId)
    {
        var actingUserId = long.Parse(_userManager.GetUserId(User)!);
        var result = await _attempts.RequestRetakeAsync(studentId, actingUserId, HttpContext.RequestAborted);

        StatusMessage = result.Outcome == RequestRetakeOutcome.Requested
            ? _localizer["Retake requested — an admin will review it."].Value
            : null;
        ErrorMessage = result.Outcome switch
        {
            RequestRetakeOutcome.Unauthorized => _localizer["Not authorized for this student."].Value,
            RequestRetakeOutcome.NoCompletedAttemptYet => _localizer["There is no completed attempt to retake yet."].Value,
            RequestRetakeOutcome.AlreadyPendingOrApproved => _localizer["A retake request already exists for this student."].Value,
            _ => null,
        };

        await LoadAsync(studentId);
        return Page();
    }

    private async Task LoadAsync(long? studentId, bool skipEligibility = false)
    {
        var userId = long.Parse(_userManager.GetUserId(User)!);
        IsGuardian = User.IsInRole(RoleNames.Guardian);

        if (IsGuardian)
        {
            var guardian = await _db.Guardians.FirstOrDefaultAsync(g => g.UserId == userId);
            if (guardian is null)
            {
                NoProfileLinked = true;
                return;
            }

            Children = await _db.Guardianships
                .Where(g => g.GuardianId == guardian.Id)
                .Join(_db.Students, g => g.StudentId, s => s.Id, (g, s) => new ChildOption(s.Id, s.FullName))
                .ToListAsync();

            if (studentId is null)
            {
                return; // show the child picker only
            }

            SelectedStudentId = studentId;
            SelectedStudentName = Children.FirstOrDefault(c => c.StudentId == studentId)?.FullName;
        }
        else
        {
            var student = await _db.Students.FirstOrDefaultAsync(s => s.UserId == userId);
            if (student is null)
            {
                NoProfileLinked = true;
                return;
            }

            SelectedStudentId = student.Id;
            SelectedStudentName = student.FullName;
        }

        if (SelectedStudentId is null || skipEligibility)
        {
            return;
        }

        var eligibility = await _attempts.GetEligibilityAsync(SelectedStudentId.Value, userId, HttpContext.RequestAborted);
        Eligibility = eligibility.Status;

        if (eligibility.Status == PlacementEligibilityStatus.Unauthorized)
        {
            SelectedStudentId = null; // never display or act on data for a child that isn't actually theirs
            return;
        }

        if (eligibility.Status == PlacementEligibilityStatus.AlreadyCompletedNoRetakeApproved)
        {
            var latest = await _db.PlacementAttempts
                .Where(a => a.StudentId == SelectedStudentId.Value && a.Status == PlacementAttemptStatus.Completed)
                .OrderByDescending(a => a.CompletedAtUtc)
                .FirstOrDefaultAsync();
            if (latest is not null)
            {
                var level = await _db.Levels.FirstOrDefaultAsync(l => l.Id == latest.AssignedLevelId);
                MostRecentResult = new LatestResult(latest.Score!.Value, level?.Code ?? "?", latest.CompletedAtUtc!.Value);
            }
        }
    }
}
