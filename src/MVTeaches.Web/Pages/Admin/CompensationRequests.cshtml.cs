using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Scheduling;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;

namespace MVTeaches.Web.Pages.Admin;

/// <summary>
/// Owner correction (student self-service booking, 2026-08-28): the admin's
/// review queue for STUDENT-submitted replacement requests — distinct from
/// /Admin/RescheduleSessions's "Approve a replacement lesson" form, which is
/// the admin acting directly with no request to review. Both ultimately call
/// the same IEnrollmentService.ApproveReplacementLessonAsync underneath
/// (via ICompensationRequestService here), so the granting rules — same
/// level, future session, atomic seat claim, exactly-once free Join — are
/// identical either way.
/// </summary>
[Authorize(Roles = RoleNames.Admin + "," + RoleNames.SystemAdmin)]
public class CompensationRequestsModel : PageModel
{
    private readonly MvTeachesDbContext _db;
    private readonly ICompensationRequestService _compensation;
    private readonly UserManager<ApplicationUser> _userManager;

    public CompensationRequestsModel(MvTeachesDbContext db, ICompensationRequestService compensation, UserManager<ApplicationUser> userManager)
    {
        _db = db;
        _compensation = compensation;
        _userManager = userManager;
    }

    public record PendingRequestRow(long RequestId, long StudentId, string StudentName, string LevelCode,
        long OriginalSessionId, Instant OriginalSessionStartsAtUtc, string? Reason, Instant RequestedAtUtc,
        IReadOnlyList<SessionOption> CandidateReplacementSessions);

    public record SessionOption(long Id, string Label);

    public record ResolvedRequestRow(long RequestId, string StudentName, CompensationRequestStatus Status,
        Instant? ReplacementStartsAtUtc, string? RejectionReason);

    public IReadOnlyList<PendingRequestRow> PendingRequests { get; set; } = Array.Empty<PendingRequestRow>();
    public IReadOnlyList<ResolvedRequestRow> RecentlyResolved { get; set; } = Array.Empty<ResolvedRequestRow>();

    [BindProperty]
    public string? RejectReason { get; set; }

    public string? StatusMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostApproveAsync(long requestId, long replacementSessionId)
    {
        var approvedByUserId = long.Parse(_userManager.GetUserId(User)!);
        var result = await _compensation.ApproveAsync(requestId, replacementSessionId, approvedByUserId, HttpContext.RequestAborted);

        StatusMessage = result.Outcome == ResolveCompensationRequestOutcome.Approved
            ? "Replacement approved — the student has been notified."
            : null;
        ErrorMessage = result.Outcome switch
        {
            ResolveCompensationRequestOutcome.RequestNotFound => "Request not found.",
            ResolveCompensationRequestOutcome.RequestNotPending => "This request was already resolved.",
            ResolveCompensationRequestOutcome.ReplacementSessionNotFound => "Replacement session not found.",
            ResolveCompensationRequestOutcome.ReplacementSessionIsTheSameSession => "The replacement must be a different session.",
            ResolveCompensationRequestOutcome.ReplacementSessionFull => "The replacement session is full.",
            ResolveCompensationRequestOutcome.AlreadyEnrolledInReplacementSession => "The student already has an active enrollment on that replacement session.",
            ResolveCompensationRequestOutcome.NoApplicableAgeGroup => "No age group covers this student's current age.",
            ResolveCompensationRequestOutcome.ReplacementSessionLevelMismatch => "The replacement session is a different level than the student's.",
            ResolveCompensationRequestOutcome.ReplacementSessionNotInFuture => "The replacement must be a session that hasn't started yet.",
            _ => "Could not approve the request.",
        };

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostRejectAsync(long requestId)
    {
        var rejectedByUserId = long.Parse(_userManager.GetUserId(User)!);
        var reason = string.IsNullOrWhiteSpace(RejectReason) ? "No reason given" : RejectReason;

        var result = await _compensation.RejectAsync(requestId, reason, rejectedByUserId, HttpContext.RequestAborted);
        StatusMessage = result.Outcome == ResolveCompensationRequestOutcome.Rejected ? "Request rejected." : null;
        ErrorMessage = result.Outcome switch
        {
            ResolveCompensationRequestOutcome.RequestNotFound => "Request not found.",
            ResolveCompensationRequestOutcome.RequestNotPending => "This request was already resolved.",
            _ => null,
        };

        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        var windowEnd = now.Plus(Duration.FromDays(30));

        var pending = await _db.CompensationRequests
            .Where(r => r.Status == CompensationRequestStatus.Pending)
            .OrderBy(r => r.RequestedAtUtc)
            .ToListAsync();

        var studentIds = pending.Select(r => r.StudentId).Distinct().ToList();
        var students = await _db.Students.Where(s => studentIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.FullName);

        var originalSessionIds = pending.Select(r => r.OriginalSessionId).ToList();
        var originalSessions = await _db.ClassSessions.Where(s => originalSessionIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id);
        var levelCodes = await _db.Levels.ToDictionaryAsync(l => l.Id, l => l.Code);

        // One candidate-replacement-session list per distinct level among the
        // pending requests, shared across requests of the same level.
        var levelsNeeded = originalSessions.Values.Select(s => s.LevelId).Distinct().ToList();
        var candidatesByLevel = new Dictionary<int, List<SessionOption>>();
        foreach (var levelId in levelsNeeded)
        {
            var sessions = await _db.ClassSessions
                .Where(s => s.LevelId == levelId && s.Status == ClassSessionStatus.Scheduled
                            && s.StartsAtUtc > now && s.StartsAtUtc <= windowEnd && s.SeatsTaken < s.Capacity)
                .OrderBy(s => s.StartsAtUtc)
                .Take(50)
                .ToListAsync();
            candidatesByLevel[levelId] = sessions
                .Select(s => new SessionOption(s.Id, $"#{s.Id} — {s.StartsAtUtc} ({s.Capacity - s.SeatsTaken} seats left)"))
                .ToList();
        }

        PendingRequests = pending.Select(r =>
        {
            var original = originalSessions.GetValueOrDefault(r.OriginalSessionId);
            var levelId = original?.LevelId ?? 0;
            return new PendingRequestRow(r.Id, r.StudentId, students.GetValueOrDefault(r.StudentId, $"#{r.StudentId}"),
                levelCodes.GetValueOrDefault(levelId, "?"), r.OriginalSessionId, original?.StartsAtUtc ?? default,
                r.Reason, r.RequestedAtUtc, candidatesByLevel.GetValueOrDefault(levelId, new List<SessionOption>()));
        }).ToList();

        var resolved = await _db.CompensationRequests
            .Where(r => r.Status != CompensationRequestStatus.Pending)
            .OrderByDescending(r => r.ResolvedAtUtc)
            .Take(30)
            .ToListAsync();
        var resolvedStudentIds = resolved.Select(r => r.StudentId).Distinct().ToList();
        var resolvedStudents = await _db.Students.Where(s => resolvedStudentIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.FullName);
        var replacementIds = resolved.Where(r => r.ReplacementSessionId.HasValue).Select(r => r.ReplacementSessionId!.Value).ToList();
        var replacementStarts = await _db.ClassSessions.Where(s => replacementIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.StartsAtUtc);

        RecentlyResolved = resolved.Select(r => new ResolvedRequestRow(
            r.Id, resolvedStudents.GetValueOrDefault(r.StudentId, $"#{r.StudentId}"), r.Status,
            r.ReplacementSessionId.HasValue ? replacementStarts.GetValueOrDefault(r.ReplacementSessionId.Value) : null,
            r.RejectionReason)).ToList();
    }
}
