using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using MVTeaches.Application.Integrations;
using MVTeaches.Domain.Integrations;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using MVTeaches.Web.Resources;

namespace MVTeaches.Web.Pages.Teacher;

/// <summary>
/// Owner clarification (2026-08-29): the teacher's own video-meeting
/// connections page — "Connect Zoom", "Connect Google Meet", per-provider
/// status, detected capability, default-provider selection, and disconnect.
///
/// The teacher is ALWAYS resolved server-side from the signed-in account's
/// own linked Teacher row; no handler on this page accepts a teacher id,
/// and ITeacherMeetingConnectionService independently re-verifies the
/// teacher binding on the OAuth callback as well.
/// </summary>
[Authorize(Roles = RoleNames.Teacher)]
public class ConnectionsModel : PageModel
{
    private readonly MvTeachesDbContext _db;
    private readonly ITeacherMeetingConnectionService _connections;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public ConnectionsModel(MvTeachesDbContext db, ITeacherMeetingConnectionService connections,
        UserManager<ApplicationUser> userManager, IStringLocalizer<SharedResource> localizer)
    {
        _db = db;
        _connections = connections;
        _userManager = userManager;
        _localizer = localizer;
    }

    public IReadOnlyList<ConnectionSummary> Connections { get; set; } = Array.Empty<ConnectionSummary>();

    public bool NoTeacherProfileLinked { get; set; }

    /// <summary>Owner clarification (2026-08-29): with neither provider
    /// connected the teacher is "Not ready for online sessions" and cannot
    /// be assigned any — the page then shows the free-Google-account
    /// instructions rather than only an error.</summary>
    public bool NotReadyForOnlineSessions => !NoTeacherProfileLinked
        && !Connections.Any(c => c.Status == ProviderConnectionStatus.Connected);

    public string? StatusMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync(string? status)
    {
        // Set by the OAuth callback endpoints on redirect back here — a
        // short opaque code, never any provider payload.
        (StatusMessage, ErrorMessage) = status switch
        {
            "connected" => (_localizer["Account connected."].Value, (string?)null),
            "state_invalid" => (null, _localizer["That authorization link was invalid, expired, or already used. Please start again."].Value),
            "mismatch" => (null, _localizer["That authorization did not belong to your account. Please start again."].Value),
            "not_configured" => (null, _localizer["This provider is not configured on the server yet — ask an admin."].Value),
            "failed" => (null, _localizer["The provider rejected the authorization. Please try again."].Value),
            _ => (null, null),
        };

        await LoadAsync();
    }

    public async Task<IActionResult> OnPostConnectAsync(VideoProviderType provider)
    {
        var teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null)
        {
            NoTeacherProfileLinked = true;
            return Page();
        }

        var redirectUri = BuildRedirectUri(provider);
        var result = await _connections.BeginConnectAsync(teacherId.Value, provider, redirectUri, HttpContext.RequestAborted);

        if (result.Outcome == BeginConnectOutcome.ProviderNotConfigured || result.AuthorizationUrl is null)
        {
            ErrorMessage = _localizer["{0} is not configured on this server yet — ask an admin to add its OAuth credentials.", Describe(provider)].Value;
            await LoadAsync();
            return Page();
        }

        return Redirect(result.AuthorizationUrl);
    }

    public async Task<IActionResult> OnPostDisconnectAsync(VideoProviderType provider)
    {
        var teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null)
        {
            NoTeacherProfileLinked = true;
            return Page();
        }

        var result = await _connections.DisconnectAsync(teacherId.Value, provider, HttpContext.RequestAborted);
        StatusMessage = result.Outcome == DisconnectOutcome.Disconnected
            ? _localizer["{0} disconnected. Meetings already created for existing sessions are unaffected.", Describe(provider)].Value
            : null;
        ErrorMessage = result.Outcome == DisconnectOutcome.NotFound ? _localizer["No such connection."].Value : null;

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostSetDefaultAsync(VideoProviderType provider)
    {
        var teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null)
        {
            NoTeacherProfileLinked = true;
            return Page();
        }

        var result = await _connections.SetDefaultProviderAsync(teacherId.Value, provider, HttpContext.RequestAborted);
        StatusMessage = result.Outcome == SetDefaultProviderOutcome.Updated
            ? _localizer["{0} is now your default for NEW sessions. Meetings already created for existing sessions are unchanged.", Describe(provider)].Value
            : null;
        ErrorMessage = result.Outcome == SetDefaultProviderOutcome.NotConnected
            ? _localizer["You must connect that provider before making it your default."].Value
            : null;

        await LoadAsync();
        return Page();
    }

    public string BuildRedirectUri(VideoProviderType provider) =>
        $"{Request.Scheme}://{Request.Host}/oauth/{(provider == VideoProviderType.Zoom ? "zoom" : "google")}/callback";

    public static string Describe(VideoProviderType provider) =>
        provider == VideoProviderType.Zoom ? "Zoom" : "Google Meet";

    private async Task<long?> ResolveTeacherIdAsync()
    {
        var userId = long.Parse(_userManager.GetUserId(User)!);
        var teacher = await _db.Teachers.FirstOrDefaultAsync(t => t.UserId == userId);
        return teacher?.Id;
    }

    private async Task LoadAsync()
    {
        var teacherId = await ResolveTeacherIdAsync();
        if (teacherId is null)
        {
            NoTeacherProfileLinked = true;
            return;
        }

        Connections = await _connections.GetConnectionsAsync(teacherId.Value, HttpContext.RequestAborted);
    }
}
