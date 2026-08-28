using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MVTeaches.Domain.Integrations;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;

namespace MVTeaches.Infrastructure.Integrations.Zoom;

/// <summary>
/// Owner clarification (2026-08-29): "Validate Zoom webhook signatures and
/// timestamps before trusting the payload... Reject replayed, stale,
/// unsigned ... events. Map every accepted event to the exact provider,
/// external meeting, application session, teacher connection, and provider
/// account. A valid event must still not be permitted to mutate an
/// unrelated teacher or session."
///
/// Deliberately advisory only: nothing here ever touches attendance,
/// entitlement, or billing (D-83) — it only keeps ProvisionedMeeting's own
/// operational status honest when something happens to a meeting on Zoom's
/// side outside MVTeaches' own flow (e.g. a teacher deletes it manually).
/// </summary>
public static class ZoomWebhookHandler
{
    private static readonly Duration TimestampTolerance = Duration.FromMinutes(5);

    public static async Task<IResult> HandleAsync(HttpContext context, MvTeachesDbContext db,
        IOptions<ZoomOptions> zoomOptions, IClock clock, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("MVTeaches.ZoomWebhook");
        var options = zoomOptions.Value;
        if (!options.IsConfigured || string.IsNullOrWhiteSpace(options.WebhookSecretToken))
        {
            return Results.NotFound();
        }

        string rawBody;
        using (var reader = new StreamReader(context.Request.Body))
        {
            rawBody = await reader.ReadToEndAsync();
        }

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(rawBody).RootElement;
        }
        catch (JsonException)
        {
            return Results.BadRequest();
        }

        var eventName = root.TryGetProperty("event", out var eventProp) ? eventProp.GetString() : null;
        if (eventName is null)
        {
            return Results.BadRequest();
        }

        // Zoom's one-time challenge when the webhook URL is first configured —
        // answered from the same secret, no signature to check yet.
        if (eventName == "endpoint.url_validation")
        {
            var plainToken = root.GetProperty("payload").GetProperty("plainToken").GetString();
            if (string.IsNullOrEmpty(plainToken))
            {
                return Results.BadRequest();
            }

            var encrypted = ZoomWebhookValidator.ComputeUrlValidationHash(options.WebhookSecretToken, plainToken);
            return Results.Ok(new { plainToken, encryptedToken = encrypted });
        }

        var timestamp = context.Request.Headers["x-zm-request-timestamp"].FirstOrDefault();
        var signature = context.Request.Headers["x-zm-signature"].FirstOrDefault();
        var now = clock.GetCurrentInstant();

        if (timestamp is null || !ZoomWebhookValidator.IsFreshTimestamp(timestamp, now, TimestampTolerance))
        {
            return Results.Unauthorized();
        }

        if (!ZoomWebhookValidator.IsValidSignature(options.WebhookSecretToken, timestamp, rawBody, signature))
        {
            return Results.Unauthorized();
        }

        if (eventName is "meeting.deleted" or "meeting.ended")
        {
            await HandleMeetingLifecycleEventAsync(root, eventName, db, clock, logger);
        }
        // Every other subscribed event is accepted (200) but otherwise
        // ignored — nothing else this app does depends on Zoom's event feed.

        return Results.Ok();
    }

    private static async Task HandleMeetingLifecycleEventAsync(JsonElement root, string eventName,
        MvTeachesDbContext db, IClock clock, ILogger logger)
    {
        if (!root.TryGetProperty("payload", out var payload) || !payload.TryGetProperty("object", out var obj))
        {
            return;
        }

        var externalMeetingId = obj.TryGetProperty("id", out var idProp) ? idProp.GetRawText().Trim('"') : null;
        var hostId = obj.TryGetProperty("host_id", out var hostProp) ? hostProp.GetString() : null;
        if (string.IsNullOrEmpty(externalMeetingId))
        {
            return;
        }

        var meeting = await db.ProvisionedMeetings.FirstOrDefaultAsync(
            m => m.Provider == VideoProviderType.Zoom && m.ExternalMeetingId == externalMeetingId && m.IsActive);
        if (meeting is null)
        {
            return; // not (or no longer) an active MVTeaches meeting — nothing to update
        }

        var connection = await db.TeacherMeetingConnections.FirstOrDefaultAsync(c => c.Id == meeting.ConnectionId);
        // The event must actually belong to the connection that owns this
        // meeting — never let a Zoom event mutate an unrelated teacher's row.
        if (connection is null || (hostId is not null && connection.ExternalAccountId != hostId))
        {
            logger.LogWarning("Zoom webhook {Event} for meeting {MeetingId} did not match its owning connection's account — ignored.",
                eventName, externalMeetingId);
            return;
        }

        if (eventName == "meeting.deleted")
        {
            // Deleted directly on Zoom's side, outside MVTeaches' own cancel
            // flow — free up the session for a fresh meeting on next Start/Join.
            meeting.MarkOrphaned("Deleted directly on Zoom (webhook meeting.deleted) — a fresh meeting will be created on next Start/Join.");
            await db.SaveChangesAsync();
        }

        // meeting.ended is purely informational here — attendance/consumption
        // is never derived from Zoom (D-83) — so no state change is needed.
    }
}
