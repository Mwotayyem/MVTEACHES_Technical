using MVTeaches.Domain.Placement;

namespace MVTeaches.Application.Placement;

public enum PlacementEligibilityStatus
{
    /// <summary>The acting user is neither the student nor one of their
    /// guardians — the same IDOR guard every other method here enforces.</summary>
    Unauthorized,

    /// <summary>No attempt yet — the free first attempt is available.</summary>
    EligibleFirstAttempt,

    /// <summary>An earlier attempt exists and no retake has been approved —
    /// rule 1's "until a placement result exists" no longer blocks purchase,
    /// but taking ANOTHER test needs RequestRetakeAsync + admin approval.</summary>
    AlreadyCompletedNoRetakeApproved,

    /// <summary>A retake request is awaiting an admin decision.</summary>
    RetakePending,

    /// <summary>An admin approved a retake and it has not been used yet —
    /// StartAttemptAsync will succeed and consume it.</summary>
    RetakeApprovedReadyToStart,

    /// <summary>An attempt was started but never submitted.</summary>
    AttemptInProgress,
}

/// <summary>One current placement, in one course. Owner decision 2026-09-04
/// (multi-course levels): a student is placed separately in every course they
/// study, so "the student's level" is a list and never a single value. A
/// student studying English at B2 and Spanish at A1 has two rows here, and
/// each one gates a different set of packages and sessions.</summary>
public record StudentCourseLevel(long CourseId, int LevelId);

/// <summary><see cref="CurrentLevels"/> is empty exactly when the student has
/// no placement anywhere — which is the condition that blocks buying a package,
/// not "has no level" in the abstract. Ordered by course id so two calls always
/// return the same order.</summary>
public record PlacementEligibilityResult(PlacementEligibilityStatus Status,
    IReadOnlyList<StudentCourseLevel> CurrentLevels,
    long? InProgressAttemptId, long? PendingOrApprovedRetakeRequestId);

public enum StartAttemptOutcome
{
    Started,
    Unauthorized,
    NoActiveTestVersion,
    NotEligible,
}

public record PlacementQuestionOption(long ChoiceId, string Text);

/// <summary>Never carries which option is correct — see StartAttemptAsync's
/// own remarks. This is exactly what a student's browser receives.</summary>
public record PlacementQuestionForAttempt(long QuestionId, string Text, IReadOnlyList<PlacementQuestionOption> Options);

public record StartAttemptResult(StartAttemptOutcome Outcome, long? AttemptId = null,
    IReadOnlyList<PlacementQuestionForAttempt>? Questions = null);

public enum SubmitAttemptOutcome
{
    Scored,
    Unauthorized,
    AttemptNotFound,
    AlreadyCompleted,
    MissingAnswers,
    InvalidChoiceForQuestion,
}

public record SubmitAttemptResult(SubmitAttemptOutcome Outcome, int? Score = null, int? AssignedLevelId = null);

public enum RequestRetakeOutcome
{
    Requested,
    Unauthorized,
    NoCompletedAttemptYet,
    AlreadyPendingOrApproved,
}

public record RequestRetakeResult(RequestRetakeOutcome Outcome, long? RetakeRequestId = null);

public enum OverrideLevelOutcome
{
    Overridden,
    StudentNotFound,
    LevelNotFound,

    /// <summary>Owner decision 2026-09-04 (multi-course levels): an override
    /// names the course it applies to, and that course must exist.</summary>
    CourseNotFound,
}

/// <summary>
/// Owner decision 2026-08-30, reversing D-48. Rules 1 and 3's runtime half:
/// eligibility, starting/submitting a scored attempt, and the admin-approved
/// retake cycle. Every method re-derives the student's own identity/ownership
/// server-side (self or an active guardian — the same IDOR guard
/// JoinAttendanceService and SubscriptionService.PurchaseFromPlanAsync use);
/// <paramref name="actingUserId" /> parameters below are never trusted from a
/// studentId arriving in the request alone.
/// </summary>
public interface IPlacementAttemptService
{
    Task<PlacementEligibilityResult> GetEligibilityAsync(long studentId, long actingUserId, CancellationToken cancellationToken);

    /// <summary>Scoring is entirely server-side. This returns the active
    /// version's questions with their answer choices, but NEVER which choice
    /// is correct — the client only ever sees choice ids and text, and the
    /// correct answer is compared only inside SubmitAttemptAsync, which runs
    /// after submission and is never sent to the browser beforehand.</summary>
    Task<StartAttemptResult> StartAttemptAsync(long studentId, long actingUserId, CancellationToken cancellationToken);

    /// <summary><paramref name="answers"/> maps QuestionId -> the chosen
    /// AnswerChoiceId. Every question belonging to the attempt's test version
    /// must be answered, and every choice must actually belong to its
    /// question — both checked server-side before any score is computed.</summary>
    Task<SubmitAttemptResult> SubmitAttemptAsync(long attemptId, long studentId, long actingUserId,
        IReadOnlyDictionary<long, long> answers, CancellationToken cancellationToken);

    Task<RequestRetakeResult> RequestRetakeAsync(long studentId, long actingUserId, CancellationToken cancellationToken);

    /// <summary>Rule 3: "Admin may override the assigned level only with a
    /// required reason." Writes a new StudentLevel row
    /// (Source = AdminOverride, which already enforces the mandatory reason in
    /// its own constructor) — never edits a PlacementAttempt's own historical
    /// Score/AssignedLevelId.</summary>
    Task<OverrideLevelOutcome> OverrideLevelAsync(long studentId, long courseId, int newLevelId, long adminUserId,
        string reason, CancellationToken cancellationToken);
}
