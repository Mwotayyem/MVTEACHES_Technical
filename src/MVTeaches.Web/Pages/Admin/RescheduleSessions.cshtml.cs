using System.ComponentModel.DataAnnotations;
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

    public RescheduleSessionsModel(MvTeachesDbContext db, IEnrollmentService enrollments, IClock clock,
        UserManager<ApplicationUser> userManager)
    {
        _db = db;
        _enrollments = enrollments;
        _clock = clock;
        _userManager = userManager;
    }

    public record SessionOption(long Id, string Label);

    public IReadOnlyList<MVTeaches.Domain.People.Student> Students { get; set; } = Array.Empty<MVTeaches.Domain.People.Student>();
    public IReadOnlyList<SessionOption> Sessions { get; set; } = Array.Empty<SessionOption>();

    [BindProperty]
    public RescheduleInput Reschedule { get; set; } = new();

    [BindProperty]
    public ApproveInput Approve { get; set; } = new();

    public string? StatusMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public class RescheduleInput
    {
        [Required] public long StudentId { get; set; }
        [Required] public long OriginalSessionId { get; set; }
        [Required] public long ReplacementSessionId { get; set; }
    }

    public class ApproveInput
    {
        [Required] public long StudentId { get; set; }
        [Required] public long OriginalSessionId { get; set; }
        [Required] public long ReplacementSessionId { get; set; }
    }

    public async Task OnGetAsync() => await LoadAsync();

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
            Reschedule.OriginalSessionId, Reschedule.ReplacementSessionId, Reschedule.StudentId, actingUserId, HttpContext.RequestAborted);

        if (result.Outcome == RescheduleOutcome.Rescheduled)
        {
            StatusMessage = "Rescheduled — the student's unattended lesson was moved to the new session. Balance untouched.";
        }
        else
        {
            ErrorMessage = result.Outcome switch
            {
                RescheduleOutcome.OriginalEnrollmentNotFound => "No active enrollment found for that student on the original session.",
                RescheduleOutcome.OriginalSessionAlreadyConsumed => "That session was already attended (Joined) — use \"Approve a replacement lesson\" below instead.",
                RescheduleOutcome.ReplacementSessionNotFound => "Replacement session not found.",
                RescheduleOutcome.ReplacementSessionIsTheSameSession => "The replacement must be a different session.",
                RescheduleOutcome.ReplacementSessionFull => "The replacement session is full.",
                RescheduleOutcome.NoApplicableAgeGroup => "No age group covers this student's current age.",
                _ => "Could not reschedule.",
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
            Approve.OriginalSessionId, Approve.ReplacementSessionId, Approve.StudentId, actingUserId, HttpContext.RequestAborted);

        if (result.Outcome == ApproveReplacementOutcome.Approved)
        {
            StatusMessage = "Replacement lesson approved — the student's next Join on it will not deduct their balance again.";
        }
        else
        {
            ErrorMessage = result.Outcome switch
            {
                ApproveReplacementOutcome.OriginalNotYetConsumed => "That session was never attended (no Join recorded) — use \"Reschedule an unattended lesson\" above instead.",
                ApproveReplacementOutcome.OriginalSessionNotFound => "Original session not found.",
                ApproveReplacementOutcome.ReplacementSessionNotFound => "Replacement session not found.",
                ApproveReplacementOutcome.ReplacementSessionIsTheSameSession => "The replacement must be a different session.",
                ApproveReplacementOutcome.ReplacementSessionFull => "The replacement session is full.",
                ApproveReplacementOutcome.AlreadyEnrolledInReplacementSession => "The student already has an active enrollment on that replacement session.",
                ApproveReplacementOutcome.NoApplicableAgeGroup => "No age group covers this student's current age.",
                ApproveReplacementOutcome.ReplacementSessionLevelMismatch => "The replacement session is a different level than the original.",
                ApproveReplacementOutcome.ReplacementSessionNotInFuture => "The replacement must be a session that hasn't started yet.",
                _ => "Could not approve the replacement.",
            };
        }

        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        Students = await _db.Students.OrderByDescending(s => s.Id).Take(200).ToListAsync();

        var now = _clock.GetCurrentInstant();
        var window = now.Minus(Duration.FromDays(30));
        var sessions = await _db.ClassSessions
            .Where(s => s.StartsAtUtc >= window)
            .OrderByDescending(s => s.StartsAtUtc)
            .Take(200)
            .ToListAsync();
        Sessions = sessions.Select(s => new SessionOption(s.Id, $"#{s.Id} — {s.StartsAtUtc} ({s.Status})")).ToList();
    }
}
