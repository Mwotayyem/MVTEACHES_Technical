using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using MVTeaches.Application.Integrations;
using MVTeaches.Application.Payroll;
using MVTeaches.Domain.Delivery;
using MVTeaches.Domain.Integrations;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using MVTeaches.Web.Resources;
using NodaTime;

namespace MVTeaches.Web.Pages.Teacher;

/// <summary>
/// §18.1 step [1] — the teacher-facing half of the payroll cycle, which had
/// no UI at all until now (only the admin-side verify/reject on
/// /Admin/Payroll existed). A teacher sees their own sessions and declares
/// delivery on the ones that have actually finished; everything else
/// (rate lookup, separation of duties, the scheduled-duration-not-declared-
/// minutes rule) is already enforced by IPayrollService/SessionDelivery.
/// </summary>
[Authorize(Roles = RoleNames.Teacher)]
public class MySessionsModel : PageModel
{
    private readonly MvTeachesDbContext _db;
    private readonly IPayrollService _payroll;
    private readonly IMeetingProvisioningService _meetings;
    private readonly ITeacherMeetingConnectionService _connections;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IClock _clock;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public MySessionsModel(MvTeachesDbContext db, IPayrollService payroll, IMeetingProvisioningService meetings,
        ITeacherMeetingConnectionService connections, UserManager<ApplicationUser> userManager, IClock clock,
        IStringLocalizer<SharedResource> localizer)
    {
        _db = db;
        _payroll = payroll;
        _meetings = meetings;
        _connections = connections;
        _userManager = userManager;
        _clock = clock;
        _localizer = localizer;
    }

    /// <param name="CapabilityWarning">Owner decision 2026-08-30: shown before
    /// the teacher presses Start, never as a reason to hide the Start button.</param>
    public record SessionRow(long SessionId, Instant StartsAtUtc, string ScheduleTimeZone, int DurationMinutes,
        string CourseName, string LevelCode, ClassSessionStatus Status, DeliveryState? DeliveryState, bool CanDeclare,
        bool CanStart, MeetingProvisioningStatus? MeetingStatus, VideoProviderType? MeetingProvider,
        string? CapabilityWarning);

    public IReadOnlyList<SessionRow> Sessions { get; set; } = Array.Empty<SessionRow>();

    /// <summary>Owner clarification (2026-08-29): a teacher with neither a
    /// usable Zoom nor a Google connection is "Not ready for online
    /// sessions" — surfaced here as well as on the Connections page, since
    /// this is where they notice the Start button missing.</summary>
    public bool NotReadyForOnlineSessions { get; set; }

    /// <summary>True only when this Teacher-role account has no linked Teacher
    /// row yet — an admin data-entry gap, not something this page can fix
    /// itself (creating a Teacher record is an admin action per §9.1/D-28).</summary>
    public bool NoTeacherProfileLinked { get; set; }

    public string? StatusMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync() => await LoadAsync();

    /// <summary>
    /// Owner clarification (2026-08-29): "The assigned authenticated teacher
    /// receives a Start session action... For Zoom, treat start_url as a
    /// short-lived host-only secret. Do not persist it as a long-lived
    /// plaintext value. Retrieve a current value only when the assigned
    /// teacher explicitly starts the session, then redirect without
    /// rendering or logging it."
    ///
    /// The meeting is provisioned lazily here if it doesn't exist yet, and
    /// IMeetingProvisioningService re-checks that this teacher really is the
    /// session's assigned teacher itself — this page's own check below is
    /// the friendly path, not the guarantee.
    /// </summary>
    public async Task<IActionResult> OnPostStartAsync(long sessionId)
    {
        var userId = long.Parse(_userManager.GetUserId(User)!);
        var teacher = await _db.Teachers.FirstOrDefaultAsync(t => t.UserId == userId);
        var session = teacher is null ? null : await _db.ClassSessions.FirstOrDefaultAsync(s => s.Id == sessionId);

        // "not found" and "not yours" share one message so this page never
        // confirms another teacher's session id exists.
        if (teacher is null || session is null || session.TeacherId != teacher.Id)
        {
            ErrorMessage = _localizer["Session not found."];
            await LoadAsync();
            return Page();
        }

        var provision = await _meetings.GetOrProvisionReadyMeetingAsync(sessionId, HttpContext.RequestAborted);
        if (provision.Outcome != ProvisionMeetingOutcome.Ready)
        {
            ErrorMessage = provision.Outcome switch
            {
                ProvisionMeetingOutcome.NoProviderConnection =>
                    _localizer["You have no connected video account yet — connect Zoom or a free Google account on the Connections page first."].Value,
                ProvisionMeetingOutcome.ProviderDisconnected =>
                    _localizer["The account this session's meeting belongs to is no longer connected — reconnect it on the Connections page."].Value,
                ProvisionMeetingOutcome.StillProvisioning =>
                    _localizer["Your meeting is still being prepared — try again in a moment."].Value,
                // provision.Detail is computed by the service itself, not a
                // literal here — left as-is; localizing it would need the
                // service to return a resx key rather than free text.
                ProvisionMeetingOutcome.SessionNotProvisionable => provision.Detail,
                _ => _localizer["The video provider could not create this meeting. Try again shortly, or ask an admin to check the connection."].Value,
            };
            await LoadAsync();
            return Page();
        }

        var startUrl = await _meetings.GetHostStartUrlAsync(sessionId, teacher.Id, HttpContext.RequestAborted);
        if (startUrl is null)
        {
            ErrorMessage = _localizer["Could not obtain a host link for this meeting right now — try again shortly."];
            await LoadAsync();
            return Page();
        }

        // Redirect straight out — never rendered into the page, never logged.
        return Redirect(startUrl);
    }

    public async Task<IActionResult> OnPostDeclareAsync(long sessionId, int declaredMinutes, string? note)
    {
        var userId = long.Parse(_userManager.GetUserId(User)!);
        var teacher = await _db.Teachers.FirstOrDefaultAsync(t => t.UserId == userId);
        var session = teacher is null ? null : await _db.ClassSessions.FirstOrDefaultAsync(s => s.Id == sessionId);

        // This page's whole contract is "a teacher declares THEIR OWN session"
        // (§18.1 step [1]) — IPayrollService.DeclareAsync itself has no notion
        // of caller identity beyond the declaredByUserId it stamps, so without
        // this check any authenticated Teacher-role account could declare
        // delivery on a session belonging to a completely different teacher.
        // "not found" and "not yours" get the same generic message so this
        // page never confirms or denies another teacher's session id exists.
        if (teacher is null || session is null || session.TeacherId != teacher.Id)
        {
            ErrorMessage = _localizer["Session not found."];
            await LoadAsync();
            return Page();
        }

        // The "already happened" rule shown client-side by CanDeclare (below)
        // must also hold server-side — otherwise it's just a UI suggestion.
        if (session.EndsAtUtc > _clock.GetCurrentInstant())
        {
            ErrorMessage = _localizer["This session hasn't ended yet — nothing to declare."];
            await LoadAsync();
            return Page();
        }

        var result = await _payroll.DeclareAsync(sessionId, userId, declaredMinutes, note, HttpContext.RequestAborted);

        StatusMessage = result.Outcome == DeclareDeliveryOutcome.Declared ? _localizer["Delivery declared — awaiting admin verification."].Value : null;
        ErrorMessage = result.Outcome switch
        {
            DeclareDeliveryOutcome.SessionNotFound => _localizer["Session not found."].Value,
            DeclareDeliveryOutcome.SessionNotDelivered => _localizer["This session was marked not-delivered — nothing to declare."].Value,
            DeclareDeliveryOutcome.AlreadyDeclared => _localizer["This session's delivery was already declared."].Value,
            _ => null,
        };

        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        var userId = long.Parse(_userManager.GetUserId(User)!);
        var teacher = await _db.Teachers.FirstOrDefaultAsync(t => t.UserId == userId);
        if (teacher is null)
        {
            NoTeacherProfileLinked = true;
            Sessions = Array.Empty<SessionRow>();
            return;
        }

        var now = _clock.GetCurrentInstant();
        var windowStart = now.Minus(Duration.FromDays(30));
        var windowEnd = now.Plus(Duration.FromDays(7));

        var sessions = await _db.ClassSessions
            .Where(s => s.TeacherId == teacher.Id && s.StartsAtUtc >= windowStart && s.StartsAtUtc <= windowEnd)
            .OrderByDescending(s => s.StartsAtUtc)
            .ToListAsync();

        var sessionIds = sessions.Select(s => s.Id).ToList();
        var deliveries = await _db.SessionDeliveries
            .Where(d => sessionIds.Contains(d.SessionId))
            .ToDictionaryAsync(d => d.SessionId, d => (DeliveryState?)d.State);

        var meetings = await _db.ProvisionedMeetings
            .Where(m => sessionIds.Contains(m.SessionId) && m.IsActive)
            .ToDictionaryAsync(m => m.SessionId, m => m);

        NotReadyForOnlineSessions = !await _connections.IsReadyForOnlineSessionsAsync(teacher.Id, HttpContext.RequestAborted);

        var courseNames = await _db.Courses.ToDictionaryAsync(c => c.Id, c => c.NameEn);
        var levelCodes = await _db.Levels.ToDictionaryAsync(l => l.Id, l => l.Code);

        var rows = new List<SessionRow>(sessions.Count);
        foreach (var s in sessions)
        {
            var deliveryState = deliveries.GetValueOrDefault(s.Id); // null = no delivery row exists yet
            var hasEnded = s.EndsAtUtc <= now;
            var canDeclare = hasEnded && s.Status != ClassSessionStatus.NotDelivered &&
                deliveryState is null or DeliveryState.Pending;
            var meeting = meetings.GetValueOrDefault(s.Id);
            // Offered from 15 minutes before the start until the session ends —
            // the teacher needs to be in the room before the students are.
            var canStart = s.Status == ClassSessionStatus.Scheduled && !hasEnded
                && now >= s.StartsAtUtc.Minus(Duration.FromMinutes(15));

            // Only worth computing for a session that has not already finished —
            // a past session's plan limit is no longer actionable.
            var warning = hasEnded || s.Status != ClassSessionStatus.Scheduled
                ? null
                : await _meetings.GetCapabilityWarningAsync(s.Id, HttpContext.RequestAborted);

            rows.Add(new SessionRow(s.Id, s.StartsAtUtc, s.ScheduleTimeZone, s.DurationMinutes,
                courseNames.GetValueOrDefault(s.CourseId, "?"), levelCodes.GetValueOrDefault(s.LevelId, "?"),
                s.Status, deliveryState, canDeclare, canStart, meeting?.Status, meeting?.Provider, warning));
        }

        Sessions = rows;
    }
}
