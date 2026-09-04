using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using MVTeaches.Application.Scheduling;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using MVTeaches.Web.Display;
using MVTeaches.Web.Identity;
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
[Authorize(Policy = PermissionKeys.CompensationView)]
public class CompensationRequestsModel : PageModel
{
    private readonly MvTeachesDbContext _db;
    private readonly ICompensationRequestService _compensation;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IAuthorizationService _authorizationService;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public CompensationRequestsModel(MvTeachesDbContext db, ICompensationRequestService compensation,
        UserManager<ApplicationUser> userManager, IAuthorizationService authorizationService,
        IStringLocalizer<SharedResource> localizer)
    {
        _db = db;
        _compensation = compensation;
        _userManager = userManager;
        _authorizationService = authorizationService;
        _localizer = localizer;
    }

    public record PendingRequestRow(long RequestId, long StudentId, string StudentName, string LevelCode,
        long OriginalSessionId, Instant OriginalSessionStartsAtUtc, string OriginalSessionTimeZone, string? Reason,
        Instant RequestedAtUtc, IReadOnlyList<SessionOption> CandidateReplacementSessions);

    /// <summary>Owner decision 2026-09-04: <see cref="IsSuggested"/> marks the
    /// ONE option this page puts forward as the obvious replacement — the
    /// soonest matching session with a free seat. It is a suggestion in the
    /// literal sense: it is pre-selected in the dropdown and labelled, and an
    /// admin still has to press Approve, can pick any other option, or can
    /// reject outright. Nothing approves itself.</summary>
    public record SessionOption(long Id, string Label, bool IsSuggested = false);

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
        if (await this.RequirePermissionAsync(_authorizationService, PermissionKeys.CompensationManage) is { } deny)
        {
            return deny;
        }

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
            ResolveCompensationRequestOutcome.ReplacementSessionCourseMismatch => _localizer["The replacement must be the same course and the same lesson type as the lesson that was missed."].Value,
            ResolveCompensationRequestOutcome.ReplacementSessionNotInFuture => _localizer["The replacement must be a session that hasn't started yet."].Value,
            _ => _localizer["Could not approve the request."].Value,
        };

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostRejectAsync(long requestId)
    {
        if (await this.RequirePermissionAsync(_authorizationService, PermissionKeys.CompensationManage) is { } deny)
        {
            return deny;
        }

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

        // Owner decision 2026-09-04 (compensation UX). Two changes here, and
        // neither touches the compensation RULE itself: approving still books a
        // replacement without charging a second hour, and rejecting still
        // leaves the original deduction standing. This only changes which
        // sessions are offered and which one is put forward first.
        //
        // 1. The match is now on course + level + session type, not level
        //    alone. It used to be possible to offer a student a different
        //    course at the same level, or a Group session as the replacement
        //    for a Private one. Course and type are already columns on
        //    ClassSession, so this needed no schema change.
        // 2. The soonest match with a free seat is marked as the suggestion.
        //
        // The candidate key is therefore the ORIGINAL session's own
        // (course, level, type), shared by every pending request that arose
        // from an equivalent session.
        var candidateKeys = originalSessions.Values
            .Select(s => (s.CourseId, s.LevelId, s.SessionType))
            .Distinct()
            .ToList();
        var candidatesByKey = new Dictionary<(long CourseId, int LevelId, SessionType SessionType), List<SessionOption>>();
        foreach (var key in candidateKeys)
        {
            var sessions = await _db.ClassSessions
                .Where(s => s.CourseId == key.CourseId && s.LevelId == key.LevelId
                            && s.SessionType == key.SessionType
                            && s.Status == ClassSessionStatus.Scheduled
                            && s.StartsAtUtc > now && s.StartsAtUtc <= windowEnd && s.SeatsTaken < s.Capacity)
                .OrderBy(s => s.StartsAtUtc)
                .Take(50)
                .ToListAsync();
            candidatesByKey[key] = sessions
                .Select((s, index) => new SessionOption(s.Id,
                    $"{_localizer.SessionOption(s.StartsAtUtc, s.ScheduleTimeZone)} — {_localizer["{0} seats left", s.Capacity - s.SeatsTaken].Value}",
                    // Sorted by start time above, so the first row IS the
                    // soonest one with a seat free.
                    IsSuggested: index == 0))
                .ToList();
        }

        PendingRequests = pending.Select(r =>
        {
            var original = originalSessions.GetValueOrDefault(r.OriginalSessionId);
            var levelId = original?.LevelId ?? 0;
            var candidates = original is null
                ? new List<SessionOption>()
                : candidatesByKey.GetValueOrDefault((original.CourseId, original.LevelId, original.SessionType),
                    new List<SessionOption>());
            return new PendingRequestRow(r.Id, r.StudentId, students.GetValueOrDefault(r.StudentId, string.Empty),
                levelCodes.GetValueOrDefault(levelId, "?"), r.OriginalSessionId, original?.StartsAtUtc ?? default,
                original?.ScheduleTimeZone ?? string.Empty,
                r.Reason, r.RequestedAtUtc, candidates);
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
