using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using MVTeaches.Application.Attendance;
using MVTeaches.Application.Integrations;
using MVTeaches.Application.Scheduling;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using MVTeaches.Web.Resources;
using NodaTime;

namespace MVTeaches.Web.Pages.Student;

/// <summary>
/// Owner correction (student self-service booking, 2026-08-28), superseding
/// "Admin assigns the student's normal lesson dates": this page now covers
/// the whole self-service loop — browse sessions matching the student's own
/// level, book one, Join it, and (if a session ends up finalized as a
/// no-show) request a replacement — not just Join on an admin-assigned
/// enrollment. Every write handler resolves the acting student from the
/// authenticated account's own linked Student row; no handler accepts a
/// student id, and IStudentBookingService/ICompensationRequestService each
/// independently re-verify account ownership again themselves — the page
/// never becomes the only thing standing between an authenticated Student
/// account and someone else's data.
/// </summary>
[Authorize(Roles = RoleNames.Student)]
public class MySessionsModel : PageModel
{
    private readonly MvTeachesDbContext _db;
    private readonly IJoinAttendanceService _join;
    private readonly IStudentBookingService _booking;
    private readonly ICompensationRequestService _compensation;
    private readonly IMeetingProvisioningService _meetings;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IClock _clock;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public MySessionsModel(MvTeachesDbContext db, IJoinAttendanceService join, IStudentBookingService booking,
        ICompensationRequestService compensation, IMeetingProvisioningService meetings,
        UserManager<ApplicationUser> userManager, IClock clock, IStringLocalizer<SharedResource> localizer)
    {
        _db = db;
        _join = join;
        _booking = booking;
        _compensation = compensation;
        _meetings = meetings;
        _userManager = userManager;
        _clock = clock;
        _localizer = localizer;
    }

    public enum AttendanceState { NotYetResolved, Present, NoShow }

    public record SessionRow(long SessionId, Instant StartsAtUtc, string ScheduleTimeZone, string CourseName,
        string LevelCode, ClassSessionStatus SessionStatus, AttendanceState Attendance, bool CanJoin, bool CanRequestReplacement);

    public record AvailableSessionRow(long SessionId, Instant StartsAtUtc, string ScheduleTimeZone, string CourseName,
        string LevelCode, int SeatsRemaining, int Capacity);

    public record CompensationRequestRow(long RequestId, long OriginalSessionId, Instant OriginalSessionStartsAtUtc,
        string? Reason, CompensationRequestStatus Status, Instant? ReplacementStartsAtUtc, string? RejectionReason);

    public IReadOnlyList<SessionRow> MySessions { get; set; } = Array.Empty<SessionRow>();
    public IReadOnlyList<AvailableSessionRow> AvailableSessions { get; set; } = Array.Empty<AvailableSessionRow>();
    public IReadOnlyList<CompensationRequestRow> MyCompensationRequests { get; set; } = Array.Empty<CompensationRequestRow>();

    /// <summary>True only when this Student-role account has no linked Student
    /// row yet — an admin data-entry gap (see /Admin/Students), not something
    /// this page can fix itself.</summary>
    public bool NoStudentProfileLinked { get; set; }

    /// <summary>True when the student has a linked profile but no current
    /// level assignment (§10.3) yet — nothing to browse or book until an
    /// admin assigns one.</summary>
    public bool NoLevelAssigned { get; set; }

    [BindProperty]
    public string? CompensationReason { get; set; }

    public string? StatusMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostJoinAsync(long sessionId)
    {
        var actingUserId = long.Parse(_userManager.GetUserId(User)!);
        var student = await _db.Students.FirstOrDefaultAsync(s => s.UserId == actingUserId);
        if (student is null)
        {
            NoStudentProfileLinked = true;
            return Page();
        }

        // Attendance/consumption is decided here and ONLY here (D-83) — the
        // video provider is never a source of truth for either, and the
        // redirect below cannot produce a second debit because JoinAsync is
        // idempotent per (session, student).
        var result = await _join.JoinAsync(new JoinAttendanceRequest(sessionId, student.Id, actingUserId), HttpContext.RequestAborted);

        StatusMessage = result.Outcome switch
        {
            JoinOutcome.Recorded => _localizer["Attendance recorded — the session's full duration has been drawn from your package."].Value,
            JoinOutcome.AlreadyRecorded => _localizer["You're already marked present for this session."].Value,
            _ => null,
        };
        ErrorMessage = result.Outcome switch
        {
            JoinOutcome.Unauthorized => _localizer["You are not enrolled in that session."].Value,
            JoinOutcome.SessionNotFound => _localizer["Session not found."].Value,
            JoinOutcome.SessionNotYetJoinable => _localizer["This session hasn't started yet."].Value,
            JoinOutcome.InsufficientBalance => _localizer["No package has enough remaining balance to cover this session."].Value,
            JoinOutcome.AlreadyFinalizedAsNoShow => _localizer["This session already ended and was recorded as a no-show — use \"Request replacement\" below instead."].Value,
            _ => null,
        };

        if (result.IsPresent)
        {
            // Owner clarification (2026-08-29): "Students receive only
            // participant access through the existing authorized MVTeaches
            // Join workflow" — the participant link is fetched here, after
            // every authorization/enrollment/timing/entitlement check above
            // has already passed, and is redirected to rather than rendered
            // into any page, list, or HTML source.
            var provision = await _meetings.GetOrProvisionReadyMeetingAsync(sessionId, HttpContext.RequestAborted);
            if (provision.Outcome == ProvisionMeetingOutcome.Ready && provision.JoinUrl is not null)
            {
                return Redirect(provision.JoinUrl);
            }

            // Attendance still stands (correctly) — only the meeting link is
            // unavailable, which is never a reason to undo a recorded Join.
            ErrorMessage = provision.Outcome switch
            {
                ProvisionMeetingOutcome.StillProvisioning => _localizer["Your meeting link is still being prepared — press Join again in a moment. Your attendance is already recorded."].Value,
                ProvisionMeetingOutcome.SessionNotProvisionable => _localizer["This session is no longer running. Your attendance is already recorded — please contact the centre."].Value,
                ProvisionMeetingOutcome.NoProviderConnection or ProvisionMeetingOutcome.ProviderDisconnected =>
                    _localizer["Your teacher's video account isn't connected — please contact the centre. Your attendance is already recorded."].Value,
                _ => _localizer["The meeting link isn't available right now — please contact the centre. Your attendance is already recorded."].Value,
            };
        }

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostBookAsync(long sessionId)
    {
        var actingUserId = long.Parse(_userManager.GetUserId(User)!);
        var student = await _db.Students.FirstOrDefaultAsync(s => s.UserId == actingUserId);
        if (student is null)
        {
            NoStudentProfileLinked = true;
            return Page();
        }

        var result = await _booking.BookSessionAsync(student.Id, sessionId, actingUserId, HttpContext.RequestAborted);

        StatusMessage = result.Outcome == BookSessionOutcome.Booked ? _localizer["Session booked."].Value : null;
        ErrorMessage = result.Outcome switch
        {
            BookSessionOutcome.Unauthorized => _localizer["Session not found."].Value, // never confirm/deny another student's data
            BookSessionOutcome.SessionNotFound => _localizer["Session not found."].Value,
            BookSessionOutcome.NoCurrentLevelAssigned => _localizer["You don't have a level assigned yet — ask an admin."].Value,
            BookSessionOutcome.SessionLevelMismatch => _localizer["That session is a different level than yours."].Value,
            BookSessionOutcome.SessionNotBookable => _localizer["That session can no longer be booked."].Value,
            BookSessionOutcome.AlreadyBooked => _localizer["You're already booked into that session."].Value,
            BookSessionOutcome.SessionFull => _localizer["That session is full."].Value,
            BookSessionOutcome.PackageLimitExceeded => _localizer["Booking this session would exceed your remaining package balance."].Value,
            BookSessionOutcome.NoApplicableAgeGroup => _localizer["No age group covers your current age."].Value,
            _ => null,
        };

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostRequestReplacementAsync(long sessionId)
    {
        var actingUserId = long.Parse(_userManager.GetUserId(User)!);
        var student = await _db.Students.FirstOrDefaultAsync(s => s.UserId == actingUserId);
        if (student is null)
        {
            NoStudentProfileLinked = true;
            return Page();
        }

        var result = await _compensation.RequestReplacementAsync(student.Id, sessionId, CompensationReason, actingUserId, HttpContext.RequestAborted);

        StatusMessage = result.Outcome == SubmitCompensationRequestOutcome.Submitted
            ? _localizer["Replacement request submitted — an admin will review it."].Value
            : null;
        ErrorMessage = result.Outcome switch
        {
            SubmitCompensationRequestOutcome.Unauthorized => _localizer["Session not found."].Value,
            SubmitCompensationRequestOutcome.NotANoShow => _localizer["That session isn't recorded as a no-show."].Value,
            SubmitCompensationRequestOutcome.DuplicateRequest => _localizer["You already have a request for this session."].Value,
            _ => null,
        };

        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        var userId = long.Parse(_userManager.GetUserId(User)!);
        var student = await _db.Students.FirstOrDefaultAsync(s => s.UserId == userId);
        if (student is null)
        {
            NoStudentProfileLinked = true;
            return;
        }

        var now = _clock.GetCurrentInstant();
        var currentLevelId = await _db.StudentLevels
            .Where(l => l.StudentId == student.Id && l.IsCurrent)
            .Select(l => (int?)l.LevelId)
            .FirstOrDefaultAsync();
        NoLevelAssigned = currentLevelId is null;

        var courseNames = await _db.Courses.ToDictionaryAsync(c => c.Id, c => c.NameEn);
        var levelCodes = await _db.Levels.ToDictionaryAsync(l => l.Id, l => l.Code);

        await LoadMySessionsAsync(student.Id, now, courseNames, levelCodes);

        if (currentLevelId is not null)
        {
            await LoadAvailableSessionsAsync(student.Id, currentLevelId.Value, now, courseNames, levelCodes);
        }

        await LoadCompensationRequestsAsync(student.Id);
    }

    private async Task LoadMySessionsAsync(long studentId, Instant now,
        Dictionary<long, string> courseNames, Dictionary<int, string> levelCodes)
    {
        // Wide enough back-window that a recent no-show is still visible long
        // enough for the student to notice and request a replacement.
        var windowStart = now.Minus(Duration.FromDays(14));
        var windowEnd = now.Plus(Duration.FromDays(30));

        var enrollments = await _db.SessionEnrollments
            .Where(e => e.StudentId == studentId && e.State == EnrollmentState.Active)
            .ToListAsync();
        var sessionIds = enrollments.Select(e => e.SessionId).Distinct().ToList();

        var sessions = await _db.ClassSessions
            .Where(s => sessionIds.Contains(s.Id) && s.StartsAtUtc >= windowStart && s.StartsAtUtc <= windowEnd)
            .OrderBy(s => s.StartsAtUtc)
            .ToListAsync();

        var attendance = await _db.AttendanceRecords
            .Where(a => sessionIds.Contains(a.SessionId) && a.StudentId == studentId)
            .ToDictionaryAsync(a => a.SessionId, a => a.IsPresent);

        // Sessions with an open (non-Rejected) compensation request already —
        // no second "Request replacement" button for those.
        var openRequestSessionIds = (await _db.CompensationRequests
            .Where(r => r.StudentId == studentId && sessionIds.Contains(r.OriginalSessionId) && r.Status != CompensationRequestStatus.Rejected)
            .Select(r => r.OriginalSessionId)
            .ToListAsync()).ToHashSet();

        MySessions = sessions.Select(session =>
        {
            var attendanceState = attendance.TryGetValue(session.Id, out var isPresent)
                ? (isPresent ? AttendanceState.Present : AttendanceState.NoShow)
                : AttendanceState.NotYetResolved;
            var canJoin = attendanceState == AttendanceState.NotYetResolved && now >= session.StartsAtUtc
                          && session.Status == ClassSessionStatus.Scheduled;
            var canRequestReplacement = attendanceState == AttendanceState.NoShow && !openRequestSessionIds.Contains(session.Id);
            return new SessionRow(session.Id, session.StartsAtUtc, session.ScheduleTimeZone,
                courseNames.GetValueOrDefault(session.CourseId, "?"), levelCodes.GetValueOrDefault(session.LevelId, "?"),
                session.Status, attendanceState, canJoin, canRequestReplacement);
        }).ToList();
    }

    private async Task LoadAvailableSessionsAsync(long studentId, int currentLevelId, Instant now,
        Dictionary<long, string> courseNames, Dictionary<int, string> levelCodes)
    {
        var windowEnd = now.Plus(Duration.FromDays(30));

        var alreadyBookedSessionIds = (await _db.SessionEnrollments
            .Where(e => e.StudentId == studentId && e.State == EnrollmentState.Active)
            .Select(e => e.SessionId)
            .ToListAsync()).ToHashSet();

        var candidates = await _db.ClassSessions
            .Where(s => s.LevelId == currentLevelId && s.Status == ClassSessionStatus.Scheduled
                        && s.StartsAtUtc > now && s.StartsAtUtc <= windowEnd && s.SeatsTaken < s.Capacity)
            .OrderBy(s => s.StartsAtUtc)
            .Take(100)
            .ToListAsync();

        AvailableSessions = candidates
            .Where(s => !alreadyBookedSessionIds.Contains(s.Id))
            .Select(s => new AvailableSessionRow(s.Id, s.StartsAtUtc, s.ScheduleTimeZone,
                courseNames.GetValueOrDefault(s.CourseId, "?"), levelCodes.GetValueOrDefault(s.LevelId, "?"),
                s.Capacity - s.SeatsTaken, s.Capacity))
            .ToList();
    }

    private async Task LoadCompensationRequestsAsync(long studentId)
    {
        var requests = await _db.CompensationRequests
            .Where(r => r.StudentId == studentId)
            .OrderByDescending(r => r.Id)
            .ToListAsync();

        var sessionIds = requests.Select(r => r.OriginalSessionId)
            .Concat(requests.Where(r => r.ReplacementSessionId.HasValue).Select(r => r.ReplacementSessionId!.Value))
            .Distinct()
            .ToList();
        var sessionStarts = await _db.ClassSessions
            .Where(s => sessionIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.StartsAtUtc);

        MyCompensationRequests = requests.Select(r => new CompensationRequestRow(
            r.Id, r.OriginalSessionId, sessionStarts.GetValueOrDefault(r.OriginalSessionId), r.Reason, r.Status,
            r.ReplacementSessionId.HasValue ? sessionStarts.GetValueOrDefault(r.ReplacementSessionId.Value) : null,
            r.RejectionReason)).ToList();
    }
}
