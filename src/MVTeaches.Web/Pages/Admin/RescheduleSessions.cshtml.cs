using System.ComponentModel.DataAnnotations;
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
/// Owner clarification (2026-08-27), replacing the earlier standalone
/// makeup-credit design entirely — there are exactly two cases:
///
/// 1. The student never pressed Join on the original session (nothing
///    consumed, balance untouched already) — "Reschedule an unattended
///    lesson" below just moves that specific enrollment to a new session.
///
/// 2. The student DID press Join (consumption stands, untouched forever)
///    and then had a legitimate problem outside their control (§17.4, line
///    1018 — reserved for the admin's own case-by-case judgment) —
///    "Approve a replacement lesson" below links one specific new session
///    to the original so the student's later Join on it costs nothing
///    extra. This is NOT a spendable credit — it is tied to exactly one
///    real replacement session, usable exactly once, same as any
///    enrollment.
/// </summary>
[Authorize(Roles = RoleNames.Admin + "," + RoleNames.SystemAdmin)]
public class RescheduleSessionsModel : PageModel
{
    private readonly MvTeachesDbContext _db;
    private readonly IEnrollmentService _enrollments;
    private readonly IClock _clock;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public RescheduleSessionsModel(MvTeachesDbContext db, IEnrollmentService enrollments, IClock clock,
        UserManager<ApplicationUser> userManager, IStringLocalizer<SharedResource> localizer)
    {
        _db = db;
        _enrollments = enrollments;
        _clock = clock;
        _userManager = userManager;
        _localizer = localizer;
    }

    /// <summary><paramref name="EnrolledStudentIds"/> lets the page narrow the
    /// "original session" list to the sessions the picked student is actually
    /// enrolled in — the same list, filtered client-side, never a different
    /// query and never a rule about what may be chosen.</summary>
    /// <summary><paramref name="LevelId"/>/<paramref name="LevelCode"/> let the
    /// make-up form offer only replacement lessons at the SAME level as the one
    /// that failed — the rule the server already enforces
    /// (ApproveReplacementOutcome.ReplacementSessionLevelMismatch), applied to
    /// the list instead of only to the rejection afterwards.</summary>
    public record SessionOption(long Id, string Label, string EnrolledStudentIds, bool HasStarted,
        int LevelId, string LevelCode, string When);

    /// <summary>The student, plus the person who would actually be told about
    /// a move. Nothing is sent from this screen - the guardian's name is here
    /// so the admin can see who the message is for and copy it.</summary>
    public record StudentOption(long Id, string FullName, string? GuardianName);

    public IReadOnlyList<StudentOption> Students { get; set; } = Array.Empty<StudentOption>();
    public IReadOnlyList<SessionOption> Sessions { get; set; } = Array.Empty<SessionOption>();

    /// <summary>Sessions that have already started — the only ones that can be
    /// the ORIGINAL of a reschedule or a make-up. Offering a future session
    /// there was the single biggest source of confusion on this screen: the
    /// server rejected it afterwards, having let the admin choose it first.</summary>
    public IReadOnlyList<SessionOption> PastSessions => Sessions.Where(s => s.HasStarted).ToList();

    /// <summary>Sessions that have not started yet — the only ones that can be
    /// the REPLACEMENT. The server enforces this too (ApproveReplacementOutcome
    /// .ReplacementSessionNotInFuture); this stops it being offered at all.</summary>
    public IReadOnlyList<SessionOption> FutureSessions => Sessions.Where(s => !s.HasStarted).ToList();

    [BindProperty]
    public RescheduleInput Reschedule { get; set; } = new();

    [BindProperty]
    public ApproveInput Approve { get; set; } = new();

    /// <summary>Set when the admin arrived from a student's file ("move this
    /// lesson"), so this screen opens on that student and that lesson instead
    /// of asking them to find both again. Display only - the server still
    /// validates and decides exactly as before.</summary>
    [BindProperty(SupportsGet = true, Name = "studentId")]
    public long? FocusStudentId { get; set; }

    [BindProperty(SupportsGet = true, Name = "sessionId")]
    public long? FocusSessionId { get; set; }

    public string? FocusStudentName { get; set; }
    public string? FocusGuardianName { get; set; }

    public string? StatusMessage { get; set; }
    public string? ErrorMessage { get; set; }

    // Nullable so [Required] actually fires on an untouched picker: on a
    // non-nullable long the empty post binds to 0, ModelState.Clear() drops the
    // binding error, and the service is called with session id 0.
    public class RescheduleInput
    {
        [Required(ErrorMessage = "Choose a student.")] public long? StudentId { get; set; }
        [Required(ErrorMessage = "Choose the original session.")] public long? OriginalSessionId { get; set; }
        [Required(ErrorMessage = "Choose the replacement session.")] public long? ReplacementSessionId { get; set; }
    }

    public class ApproveInput
    {
        [Required(ErrorMessage = "Choose a student.")] public long? StudentId { get; set; }
        [Required(ErrorMessage = "Choose the original session.")] public long? OriginalSessionId { get; set; }
        [Required(ErrorMessage = "Choose the replacement session.")] public long? ReplacementSessionId { get; set; }
    }

    public async Task OnGetAsync()
    {
        await LoadAsync();

        if (FocusStudentId is null)
        {
            return;
        }

        var focus = Students.FirstOrDefault(student => student.Id == FocusStudentId.Value);
        FocusStudentName = focus?.FullName;
        FocusGuardianName = focus?.GuardianName;

        // Pre-fill BOTH forms' student picker: which of the two applies
        // depends on whether the student joined, and only the admin knows
        // that. Pre-filling the lesson too, but only where it is eligible.
        Reschedule.StudentId ??= FocusStudentId;
        Approve.StudentId ??= FocusStudentId;
        if (FocusSessionId is not null && PastSessions.Any(session => session.Id == FocusSessionId.Value))
        {
            Reschedule.OriginalSessionId ??= FocusSessionId;
            Approve.OriginalSessionId ??= FocusSessionId;
        }
    }

    public async Task<IActionResult> OnPostRescheduleAsync()
    {
        ModelState.Clear();
        if (!TryValidateModel(Reschedule, nameof(Reschedule)))
        {
            await LoadAsync();
            return Page();
        }

        var actingUserId = long.Parse(_userManager.GetUserId(User)!);
        var result = await _enrollments.RescheduleUnattendedEnrollmentAsync(
            Reschedule.OriginalSessionId!.Value, Reschedule.ReplacementSessionId!.Value, Reschedule.StudentId!.Value,
            actingUserId, HttpContext.RequestAborted);

        if (result.Outcome == RescheduleOutcome.Rescheduled)
        {
            StatusMessage = _localizer["Rescheduled — the student's unattended lesson was moved to the new session. Balance untouched."].Value;
        }
        else
        {
            ErrorMessage = result.Outcome switch
            {
                RescheduleOutcome.OriginalEnrollmentNotFound => _localizer["No active enrollment found for that student on the original session."].Value,
                RescheduleOutcome.OriginalSessionAlreadyConsumed => _localizer["That session was already attended (Joined) — use \"Approve a replacement lesson\" below instead."].Value,
                RescheduleOutcome.ReplacementSessionNotFound => _localizer["Replacement session not found."].Value,
                RescheduleOutcome.ReplacementSessionIsTheSameSession => _localizer["The replacement must be a different session."].Value,
                RescheduleOutcome.ReplacementSessionFull => _localizer["The replacement session is full."].Value,
                RescheduleOutcome.NoApplicableAgeGroup => _localizer["No age group covers this student's current age."].Value,
                _ => _localizer["Could not reschedule."].Value,
            };
        }

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostApproveAsync()
    {
        ModelState.Clear();
        if (!TryValidateModel(Approve, nameof(Approve)))
        {
            await LoadAsync();
            return Page();
        }

        var actingUserId = long.Parse(_userManager.GetUserId(User)!);
        var result = await _enrollments.ApproveReplacementLessonAsync(
            Approve.OriginalSessionId!.Value, Approve.ReplacementSessionId!.Value, Approve.StudentId!.Value,
            actingUserId, HttpContext.RequestAborted);

        if (result.Outcome == ApproveReplacementOutcome.Approved)
        {
            StatusMessage = _localizer["Replacement lesson approved — the student's next Join on it will not deduct their balance again."].Value;
        }
        else
        {
            ErrorMessage = result.Outcome switch
            {
                ApproveReplacementOutcome.OriginalNotYetConsumed => _localizer["That session was never attended (no Join recorded) — use \"Reschedule an unattended lesson\" above instead."].Value,
                ApproveReplacementOutcome.OriginalSessionNotFound => _localizer["Original session not found."].Value,
                ApproveReplacementOutcome.ReplacementSessionNotFound => _localizer["Replacement session not found."].Value,
                ApproveReplacementOutcome.ReplacementSessionIsTheSameSession => _localizer["The replacement must be a different session."].Value,
                ApproveReplacementOutcome.ReplacementSessionFull => _localizer["The replacement session is full."].Value,
                ApproveReplacementOutcome.AlreadyEnrolledInReplacementSession => _localizer["The student already has an active enrollment on that replacement session."].Value,
                ApproveReplacementOutcome.NoApplicableAgeGroup => _localizer["No age group covers this student's current age."].Value,
                ApproveReplacementOutcome.ReplacementSessionLevelMismatch => _localizer["The replacement session is a different level than the original."].Value,
                ApproveReplacementOutcome.ReplacementSessionNotInFuture => _localizer["The replacement must be a session that hasn't started yet."].Value,
                _ => _localizer["Could not approve the replacement."].Value,
            };
        }

        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        var studentEntities = await _db.Students.OrderBy(s => s.FullName).Take(200).ToListAsync();
        var studentIds = studentEntities.Select(s => s.Id).ToList();
        var guardianships = await _db.Guardianships
            .Where(g => studentIds.Contains(g.StudentId))
            .ToListAsync();
        var guardianNames = await _db.Guardians
            .Where(g => guardianships.Select(x => x.GuardianId).Contains(g.Id))
            .ToDictionaryAsync(g => g.Id, g => g.FullName);
        var guardianByStudent = guardianships
            .GroupBy(g => g.StudentId)
            .ToDictionary(
                g => g.Key,
                g => guardianNames.GetValueOrDefault(
                    (g.FirstOrDefault(x => x.IsPrimary) ?? g.First()).GuardianId));
        Students = studentEntities
            .Select(s => new StudentOption(s.Id, s.FullName, guardianByStudent.GetValueOrDefault(s.Id)))
            .ToList();

        var now = _clock.GetCurrentInstant();
        var window = now.Minus(Duration.FromDays(30));
        var sessions = await _db.ClassSessions
            .Where(s => s.StartsAtUtc >= window)
            .OrderByDescending(s => s.StartsAtUtc)
            .Take(200)
            .ToListAsync();

        var sessionIds = sessions.Select(s => s.Id).ToList();
        var levelCodes = await _db.Levels.ToDictionaryAsync(l => l.Id, l => l.Code);
        var teacherNames = await _db.Teachers.ToDictionaryAsync(t => t.Id, t => t.FullName);
        var enrolledBySession = (await _db.SessionEnrollments
                .Where(e => sessionIds.Contains(e.SessionId) && e.State == EnrollmentState.Active)
                .Select(e => new { e.SessionId, e.StudentId })
                .ToListAsync())
            .GroupBy(e => e.SessionId)
            .ToDictionary(g => g.Key, g => string.Join(",", g.Select(e => e.StudentId).Distinct()));

        Sessions = sessions.Select(s => new SessionOption(
            s.Id,
            string.Join(" · ", new[]
            {
                _localizer.SessionOption(s.StartsAtUtc, s.ScheduleTimeZone),
                levelCodes.GetValueOrDefault(s.LevelId, "?"),
                teacherNames.GetValueOrDefault(s.TeacherId, string.Empty),
                _localizer["ClassSessionStatus." + s.Status].Value,
            }.Where(part => !string.IsNullOrWhiteSpace(part))),
            enrolledBySession.GetValueOrDefault(s.Id, string.Empty),
            s.StartsAtUtc <= now,
            s.LevelId,
            levelCodes.GetValueOrDefault(s.LevelId, "?"),
            _localizer.SessionMoment(s.StartsAtUtc, s.ScheduleTimeZone))).ToList();
    }
}
