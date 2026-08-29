using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.People;
using MVTeaches.Application.Placement;
using MVTeaches.Domain.Audit;
using MVTeaches.Domain.Placement;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;
using Npgsql;

namespace MVTeaches.Infrastructure.Placement;

/// <inheritdoc cref="IPlacementAttemptService"/>
public class PlacementAttemptService : IPlacementAttemptService
{
    private const string PostgresUniqueViolationSqlState = "23505";

    private readonly MvTeachesDbContext _db;
    private readonly IStudentAdmissionService _admissions;
    private readonly IClock _clock;

    public PlacementAttemptService(MvTeachesDbContext db, IStudentAdmissionService admissions, IClock clock)
    {
        _db = db;
        _admissions = admissions;
        _clock = clock;
    }

    /// <summary>The same IDOR guard JoinAttendanceService.IsAuthorizedToJoinAsync
    /// and SubscriptionService.PurchaseFromPlanAsync use — never trusted from
    /// studentId arriving in the request alone.</summary>
    private async Task<bool> IsAuthorizedAsync(long studentId, long actingUserId, CancellationToken ct)
    {
        var isTheStudentThemself = await _db.Students.AnyAsync(s => s.Id == studentId && s.UserId == actingUserId, ct);
        if (isTheStudentThemself)
        {
            return true;
        }

        return await _db.Guardianships
            .Join(_db.Guardians, gs => gs.GuardianId, g => g.Id, (gs, g) => new { gs.StudentId, g.UserId })
            .AnyAsync(x => x.StudentId == studentId && x.UserId == actingUserId, ct);
    }

    private async Task<(PlacementAttempt? InProgress, PlacementRetakeRequest? Pending, PlacementRetakeRequest? ApprovedUnconsumed, bool HasAnyCompleted)>
        LoadStateAsync(long studentId, CancellationToken ct)
    {
        var inProgress = await _db.PlacementAttempts.FirstOrDefaultAsync(
            a => a.StudentId == studentId && a.Status == PlacementAttemptStatus.InProgress, ct);
        var pending = await _db.PlacementRetakeRequests.FirstOrDefaultAsync(
            r => r.StudentId == studentId && r.Status == PlacementRetakeStatus.Pending, ct);
        var approvedUnconsumed = await _db.PlacementRetakeRequests.FirstOrDefaultAsync(
            r => r.StudentId == studentId && r.Status == PlacementRetakeStatus.Approved && r.ConsumedByAttemptId == null, ct);
        var hasAnyCompleted = await _db.PlacementAttempts.AnyAsync(
            a => a.StudentId == studentId && a.Status == PlacementAttemptStatus.Completed, ct);

        return (inProgress, pending, approvedUnconsumed, hasAnyCompleted);
    }

    public async Task<PlacementEligibilityResult> GetEligibilityAsync(long studentId, long actingUserId, CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync(studentId, actingUserId, cancellationToken))
        {
            return new PlacementEligibilityResult(PlacementEligibilityStatus.Unauthorized, null, null, null);
        }

        var currentLevelId = await _db.StudentLevels
            .Where(l => l.StudentId == studentId && l.IsCurrent)
            .Select(l => (int?)l.LevelId)
            .FirstOrDefaultAsync(cancellationToken);

        var (inProgress, pending, approvedUnconsumed, hasAnyCompleted) = await LoadStateAsync(studentId, cancellationToken);

        if (inProgress is not null)
        {
            return new PlacementEligibilityResult(PlacementEligibilityStatus.AttemptInProgress, currentLevelId, inProgress.Id, null);
        }

        if (pending is not null)
        {
            return new PlacementEligibilityResult(PlacementEligibilityStatus.RetakePending, currentLevelId, null, pending.Id);
        }

        if (approvedUnconsumed is not null)
        {
            return new PlacementEligibilityResult(PlacementEligibilityStatus.RetakeApprovedReadyToStart, currentLevelId, null, approvedUnconsumed.Id);
        }

        if (hasAnyCompleted)
        {
            return new PlacementEligibilityResult(PlacementEligibilityStatus.AlreadyCompletedNoRetakeApproved, currentLevelId, null, null);
        }

        return new PlacementEligibilityResult(PlacementEligibilityStatus.EligibleFirstAttempt, currentLevelId, null, null);
    }

    public async Task<StartAttemptResult> StartAttemptAsync(long studentId, long actingUserId, CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync(studentId, actingUserId, cancellationToken))
        {
            return new StartAttemptResult(StartAttemptOutcome.Unauthorized);
        }

        var activeVersion = await _db.PlacementTestVersions.FirstOrDefaultAsync(v => v.IsActive, cancellationToken);
        if (activeVersion is null)
        {
            return new StartAttemptResult(StartAttemptOutcome.NoActiveTestVersion);
        }

        var (inProgress, pending, approvedUnconsumed, hasAnyCompleted) = await LoadStateAsync(studentId, cancellationToken);

        // Idempotent resume: a refreshed browser must not spawn a second
        // in-progress attempt for the same student.
        if (inProgress is not null)
        {
            return new StartAttemptResult(StartAttemptOutcome.Started, inProgress.Id,
                await BuildQuestionListAsync(inProgress.TestVersionId, cancellationToken));
        }

        long? consumingRetakeRequestId = null;
        if (hasAnyCompleted)
        {
            // Rule 3: "A student cannot repeatedly retake the test to obtain a
            // preferred level. A retake requires explicit Admin approval."
            if (approvedUnconsumed is null)
            {
                return new StartAttemptResult(StartAttemptOutcome.NotEligible);
            }

            consumingRetakeRequestId = approvedUnconsumed.Id;
        }
        // else: no completed attempt yet — the free first attempt, regardless
        // of whether pending/approved retake rows happen to exist for some
        // other reason; hasAnyCompleted being false is what makes this free.

        var now = _clock.GetCurrentInstant();
        var attempt = new PlacementAttempt(studentId, activeVersion.Id, consumingRetakeRequestId, actingUserId, now);
        _db.PlacementAttempts.Add(attempt);

        try
        {
            await _db.SaveChangesAsync(cancellationToken); // attempt needs its Id before MarkConsumed below
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresUniqueViolationSqlState })
        {
            // Lost a genuine race against a concurrent Start call for this same
            // student — ux_placement_attempt_in_progress is the real guard
            // behind the pre-check above. The winner's in-progress attempt is
            // what both callers should now see, exactly like the loser of a
            // concurrent booking/join race is redirected to the winner's outcome.
            _db.ChangeTracker.Clear();
            var winner = await _db.PlacementAttempts.FirstAsync(
                a => a.StudentId == studentId && a.Status == PlacementAttemptStatus.InProgress, cancellationToken);
            return new StartAttemptResult(StartAttemptOutcome.Started, winner.Id,
                await BuildQuestionListAsync(winner.TestVersionId, cancellationToken));
        }

        if (consumingRetakeRequestId is not null)
        {
            approvedUnconsumed!.MarkConsumed(attempt.Id);
            await _db.SaveChangesAsync(cancellationToken);
        }

        return new StartAttemptResult(StartAttemptOutcome.Started, attempt.Id,
            await BuildQuestionListAsync(activeVersion.Id, cancellationToken));
    }

    /// <summary>Never includes which choice is correct — see the interface's
    /// own remarks. This is exactly what reaches the browser.</summary>
    private async Task<IReadOnlyList<PlacementQuestionForAttempt>> BuildQuestionListAsync(long testVersionId, CancellationToken ct)
    {
        var questions = await _db.PlacementQuestions
            .Where(q => q.TestVersionId == testVersionId)
            .OrderBy(q => q.SortOrder)
            .ToListAsync(ct);
        var questionIds = questions.Select(q => q.Id).ToList();
        var choicesByQuestion = (await _db.PlacementAnswerChoices
                .Where(c => questionIds.Contains(c.QuestionId))
                .OrderBy(c => c.SortOrder)
                .ToListAsync(ct))
            .GroupBy(c => c.QuestionId)
            .ToDictionary(g => g.Key, g => g.Select(c => new PlacementQuestionOption(c.Id, c.Text)).ToList());

        return questions.Select(q => new PlacementQuestionForAttempt(
            q.Id, q.Text, choicesByQuestion.GetValueOrDefault(q.Id, new List<PlacementQuestionOption>())))
            .ToList();
    }

    public async Task<SubmitAttemptResult> SubmitAttemptAsync(long attemptId, long studentId, long actingUserId,
        IReadOnlyDictionary<long, long> answers, CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync(studentId, actingUserId, cancellationToken))
        {
            return new SubmitAttemptResult(SubmitAttemptOutcome.Unauthorized);
        }

        var attempt = await _db.PlacementAttempts.FirstOrDefaultAsync(a => a.Id == attemptId && a.StudentId == studentId, cancellationToken);
        if (attempt is null)
        {
            return new SubmitAttemptResult(SubmitAttemptOutcome.AttemptNotFound);
        }

        if (attempt.Status != PlacementAttemptStatus.InProgress)
        {
            return new SubmitAttemptResult(SubmitAttemptOutcome.AlreadyCompleted);
        }

        var questions = await _db.PlacementQuestions.Where(q => q.TestVersionId == attempt.TestVersionId).ToListAsync(cancellationToken);
        if (questions.Any(q => !answers.ContainsKey(q.Id)))
        {
            return new SubmitAttemptResult(SubmitAttemptOutcome.MissingAnswers);
        }

        var questionIds = questions.Select(q => q.Id).ToList();
        var choicesByQuestion = (await _db.PlacementAnswerChoices
                .Where(c => questionIds.Contains(c.QuestionId))
                .ToListAsync(cancellationToken))
            .GroupBy(c => c.QuestionId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Validate EVERY submitted choice actually belongs to its own
        // question before scoring anything — never trust a client-supplied
        // (questionId, choiceId) pairing on faith.
        foreach (var question in questions)
        {
            var chosenId = answers[question.Id];
            if (!choicesByQuestion[question.Id].Any(c => c.Id == chosenId))
            {
                return new SubmitAttemptResult(SubmitAttemptOutcome.InvalidChoiceForQuestion);
            }
        }

        var now = _clock.GetCurrentInstant();
        var score = 0;
        foreach (var question in questions)
        {
            var chosen = choicesByQuestion[question.Id].First(c => c.Id == answers[question.Id]);
            var pointsAwarded = chosen.IsCorrect ? question.Points : 0;
            score += pointsAwarded;
            _db.PlacementAttemptAnswers.Add(new PlacementAttemptAnswer(attempt.Id, question.Id, chosen.Id, chosen.IsCorrect, pointsAwarded));
        }

        var range = await _db.PlacementScoreRanges
            .Where(r => r.TestVersionId == attempt.TestVersionId && r.MinScore <= score && r.MaxScore >= score)
            .FirstOrDefaultAsync(cancellationToken);
        if (range is null)
        {
            // Publish validation guarantees full [0, totalPoints] coverage, so
            // this can only mean the version's ranges were tampered with
            // outside this service — fail loudly rather than silently
            // assigning an arbitrary level.
            throw new InvalidOperationException(
                $"No score range on test version {attempt.TestVersionId} covers score {score} — the published version's ranges no longer cover the full possible score.");
        }

        attempt.Complete(score, range.LevelId, now);

        // Owner decision 2026-08-30: the level is assigned AUTOMATICALLY by the
        // scoring engine — AssignedByRole.System, not Admin/Teacher, since no
        // human judgment call is in this loop. AssignedByUserId still records
        // who triggered the submission (student or guardian) for traceability.
        var previousCurrent = await _db.StudentLevels.Where(l => l.StudentId == studentId && l.IsCurrent).ToListAsync(cancellationToken);
        foreach (var previous in previousCurrent)
        {
            previous.Supersede();
        }
        _db.StudentLevels.Add(new Domain.Placement.StudentLevel(studentId, range.LevelId, actingUserId,
            Domain.Placement.AssignedByRole.System, LevelAssignmentSource.PlacementTest, null, null, now));

        var student = await _db.Students.FirstAsync(s => s.Id == studentId, cancellationToken);
        if (student.Status == Domain.People.StudentStatus.PendingLevel)
        {
            student.MarkLevelAssigned();
        }

        await _db.SaveChangesAsync(cancellationToken);
        return new SubmitAttemptResult(SubmitAttemptOutcome.Scored, score, range.LevelId);
    }

    public async Task<RequestRetakeResult> RequestRetakeAsync(long studentId, long actingUserId, CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync(studentId, actingUserId, cancellationToken))
        {
            return new RequestRetakeResult(RequestRetakeOutcome.Unauthorized);
        }

        var (inProgress, pending, approvedUnconsumed, hasAnyCompleted) = await LoadStateAsync(studentId, cancellationToken);
        if (!hasAnyCompleted)
        {
            return new RequestRetakeResult(RequestRetakeOutcome.NoCompletedAttemptYet);
        }

        if (pending is not null || approvedUnconsumed is not null || inProgress is not null)
        {
            return new RequestRetakeResult(RequestRetakeOutcome.AlreadyPendingOrApproved);
        }

        var request = new PlacementRetakeRequest(studentId, actingUserId, _clock.GetCurrentInstant());
        _db.PlacementRetakeRequests.Add(request);
        await _db.SaveChangesAsync(cancellationToken);
        return new RequestRetakeResult(RequestRetakeOutcome.Requested, request.Id);
    }

    public async Task<OverrideLevelOutcome> OverrideLevelAsync(long studentId, int newLevelId, long adminUserId, string reason, CancellationToken cancellationToken)
    {
        if (!await _db.Students.AnyAsync(s => s.Id == studentId, cancellationToken))
        {
            return OverrideLevelOutcome.StudentNotFound;
        }

        if (!await _db.Levels.AnyAsync(l => l.Id == newLevelId, cancellationToken))
        {
            return OverrideLevelOutcome.LevelNotFound;
        }

        // Reuses the exact same supersede-then-insert-AdminOverride path
        // Admin/Students already uses (IStudentAdmissionService.AssignLevelAsync),
        // whose own Domain constructor already makes the reason mandatory —
        // rather than re-implementing it here.
        await _admissions.AssignLevelAsync(studentId, newLevelId, adminUserId, reason, cancellationToken);

        _db.AuditLogEntries.Add(new AuditLogEntry("Student", studentId.ToString(), "LevelOverridden",
            adminUserId, reason, beforeJson: null, afterJson: null, _clock.GetCurrentInstant()));
        await _db.SaveChangesAsync(cancellationToken);

        return OverrideLevelOutcome.Overridden;
    }
}
