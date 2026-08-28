using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Integrations;
using MVTeaches.Domain.Integrations;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;

namespace MVTeaches.Web.Pages.OAuth;

/// <summary>
/// The single OAuth redirect endpoint for both providers (routed as
/// /oauth/zoom/callback and /oauth/google/callback so each provider's app
/// registration gets its own exact redirect URI, as both require).
///
/// It is <c>[Authorize(Roles = Teacher)]</c> on purpose: this is the
/// "initiating browser session" half of the owner's state-binding
/// requirement — the callback can only complete while the teacher who
/// started it is still signed in, and the still-signed-in identity is what
/// gets compared against the state row's TeacherId server-side. A forged or
/// stolen callback URL opened by anyone else lands on the login page or is
/// rejected as a mismatch; it can never complete someone else's connection.
///
/// Nothing here renders the code, the state, or any token — every outcome
/// leaves as a redirect carrying only a short opaque status word.
/// </summary>
[Authorize(Roles = RoleNames.Teacher)]
public class CallbackModel : PageModel
{
    private readonly MvTeachesDbContext _db;
    private readonly ITeacherMeetingConnectionService _connections;
    private readonly UserManager<ApplicationUser> _userManager;

    public CallbackModel(MvTeachesDbContext db, ITeacherMeetingConnectionService connections,
        UserManager<ApplicationUser> userManager)
    {
        _db = db;
        _connections = connections;
        _userManager = userManager;
    }

    public async Task<IActionResult> OnGetAsync(string providerKey, string? code, string? state, string? error)
    {
        var provider = providerKey switch
        {
            "zoom" => VideoProviderType.Zoom,
            "google" => VideoProviderType.GoogleMeet,
            _ => (VideoProviderType?)null,
        };

        if (provider is null)
        {
            return NotFound();
        }

        // The teacher declined consent at the provider, or the provider
        // reported its own error — nothing to exchange.
        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
        {
            return RedirectToPage("/Teacher/Connections", new { status = "failed" });
        }

        var userId = long.Parse(_userManager.GetUserId(User)!);
        var teacher = await _db.Teachers.FirstOrDefaultAsync(t => t.UserId == userId);
        if (teacher is null)
        {
            return RedirectToPage("/Teacher/Connections", new { status = "mismatch" });
        }

        var redirectUri = $"{Request.Scheme}://{Request.Host}/oauth/{providerKey}/callback";
        var result = await _connections.CompleteConnectAsync(provider.Value, state, code, teacher.Id, redirectUri,
            HttpContext.RequestAborted);

        var status = result.Outcome switch
        {
            CompleteConnectOutcome.Connected => "connected",
            CompleteConnectOutcome.InvalidOrExpiredState => "state_invalid",
            CompleteConnectOutcome.TeacherMismatch => "mismatch",
            CompleteConnectOutcome.ProviderNotConfigured => "not_configured",
            _ => "failed",
        };

        return RedirectToPage("/Teacher/Connections", new { status });
    }
}
