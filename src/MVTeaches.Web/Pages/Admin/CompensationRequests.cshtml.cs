using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using MVTeaches.Application.Scheduling;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using MVTeaches.Web.Display;
using MVTeaches.Web.Resources;
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
    private readonly IStringLocalizer<SharedResource> _localizer;

    public CompensationRequestsModel(MvTeachesDbContext db, ICompensationRequestService compensation,
        UserManager<ApplicationUser> userManager, IStringLocalizer<SharedResource> localizer)
    {
        _db = db;
        _compensation = compensation;
        _userManager = userManager;
        _localizer = localizer;
    }

    public record PendingRequestRow(long RequestId, long StudentId, string StudentName, string LevelCode,
        long OriginalSessionId, Instant OriginalSessionStartsAtUtc, string OriginalSessionTimeZone, string? Reason,
        Instant RequestedAtUtc, IReadOnlyList<SessionOption> CandidateReplacementSessions);

    public record SessionOption(long Id, string Label);

    public record ResolvedRequestRow(long RequestId, long StudentId, string StudentName, CompensationRequestStatus Status,
        Instant? ReplacementStartsAtUtc, string? ReplacementTimeZone, string? RejectionReason);

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
            ? _localizer["Replacement approved — the student has been notified."].Value
            : null;
        ErrorMessage = result.Outcome switch
        {
            ResolveCompensationRequestOutcome.RequestNotFound => _localizer["Request not found."].Value,
            ResolveCompensationRequestOutcome.RequestNotPending => _localizer["This request was already resolved."].Value,
            ResolveCompensationRequestOutcome.ReplacementSessionNotFound => _localizer["Replacement session not found."].Value,
            ResolveCompensationRequestOutcome.ReplacementSessionIsTheSameSession => _localizer["The replacement must be a different session."].Value,
            ResolveCompensationRequestOutcome.ReplacementSessionFull => _localizer["The replacement session is full."].Value,
            ResolveCompensationRequestOutcome.AlreadyEnrolledInReplacementSession => _localizer["The student already has an active enrollment on that replacement session."].Value,
            ResolveCompensationRequestOutcome.NoApplicableAgeGroup => _localizer["No age group covers this student's current age."].Value,
            ResolveCompensationRequestOutcome.ReplacementSessionLevelMismatch => _localizer["The replacement session is a different level than the student's."].Value,
            ResolveCompensationRequestOutcome.ReplacementSessionNotInFuture => _localizer["The replacement must be a session that hasn't started yet."].Value,
            _ => _localizer["Could not approve the request."].Value,
        };

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostRejectAsync(long requestId)
    {
        var rejectedByUserId = long.Parse(_userManager.GetUserId(User)!);
        var reason = string.IsNullOrWhiteSpace(RejectReason) ? _localizer["No reason given"].Value : RejectReason;

        var result = await _compensation.RejectAsync(requestId, reason, rejectedByUserId, HttpContext.RequestAborted);
        StatusMessage = result.Outcome == ResolveCompensationRequestOutcome.Rejected ? _localizer["Request rejected."].Value : null;
        ErrorMessage = result.Outcome switch
        {
            ResolveCompensationRequestOutcome.RequestNotFound => _localizer["Request not found."].Value,
            ResolveCompensationRequestOutcome.RequestNotPending => _localizer["This request was already resolved."].Value,
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
                .Select(s => new SessionOption(s.Id,
                    $"{_localizer.SessionOption(s.StartsAtUtc, s.ScheduleTimeZone)} — {_localizer["{0} seats left", s.Capacity - s.SeatsTaken].Value}"))
                .ToList();
        }

        PendingRequests = pending.Select(r =>
        {
            var original = originalSessions.GetValueOrDefault(r.OriginalSessionId);
            var levelId = original?.LevelId ?? 0;
            return new PendingRequestRow(r.Id, r.StudentId, students.GetValueOrDefault(r.StudentId, string.Empty),
                levelCodes.GetValueOrDefault(levelId, "?"), r.OriginalSessionId, original?.StartsAtUtc ?? default,
                original?.ScheduleTimeZone ?? string.Empty,
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
        var replacements = await _db.ClassSessions.Where(s => replacementIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => new { s.StartsAtUtc, s.ScheduleTimeZone });

        RecentlyResolved = resolved.Select(r =>
        {
            var replacement = r.ReplacementSessionId.HasValue
                ? replacements.GetValueOrDefault(r.ReplacementSessionId.Value)
                : null;
            return new ResolvedRequestRow(
                r.Id, r.StudentId, resolvedStudents.GetValueOrDefault(r.StudentId, string.Empty), r.Status,
                replacement?.StartsAtUtc, replacement?.ScheduleTimeZone, r.RejectionReason);
        }).ToList();
    }
}
