using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using MVTeaches.Application.Integrations;
using MVTeaches.Domain.Audit;
using MVTeaches.Domain.Integrations;
using MVTeaches.Domain.Notifications;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Infrastructure.Integrations.Security;
using MVTeaches.Infrastructure.Integrations.Zoom;
using MVTeaches.Infrastructure.Persistence;
using MVTeaches.Infrastructure.Resources;
using NodaTime;

namespace MVTeaches.Infrastructure.Integrations;

/// <inheritdoc cref="IMeetingProvisioningService"/>
public class MeetingProvisioningService : IMeetingProvisioningService
{
    /// <summary>A crashed/timed-out provisioning attempt becomes reclaimable
    /// after this long — long enough for a real Zoom/Google API round trip,
    /// short enough that a genuinely stuck session doesn't stay blocked.</summary>
    private static readonly Duration ClaimStaleAfter = Duration.FromMinutes(2);

    /// <summary>Google Meet free-plan limits (support.google.com/meet/answer/7317473):
    /// 3+ participants for up to 60 minutes; a true one-to-one for up to 24 hours.</summary>
    private const int GoogleGroupMinutesLimit = 60;
    private const int GoogleOneToOneMinutesLimit = 24 * 60;

    private readonly MvTeachesDbContext _db;
    private readonly IEnumerable<IVideoMeetingProviderClient> _clients;
    private readonly TokenRefreshCoordinator _tokenRefresh;
    private readonly IClock _clock;
    private readonly ILogger<MeetingProvisioningService> _logger;
    private readonly IStringLocalizer<InfrastructureResource> _localizer;

    public MeetingProvisioningService(MvTeachesDbContext db, IEnumerable<IVideoMeetingProviderClient> clients,
        TokenRefreshCoordinator tokenRefresh, IClock clock, ILogger<MeetingProvisioningService> logger,
        IStringLocalizer<InfrastructureResource> localizer)
    {
        _db = db;
        _clients = clients;
        _tokenRefresh = tokenRefresh;
        _clock = clock;
        _logger = logger;
        _localizer = localizer;
    }

    public async Task<ProvisionMeetingResult> GetOrProvisionReadyMeetingAsync(long sessionId, CancellationToken cancellationToken)
    {
        var session = await _db.ClassSessions.FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is null)
        {
            return new ProvisionMeetingResult(ProvisionMeetingOutcome.SessionNotFound);
        }

        // A session the centre cancelled (or marked not-delivered, or that is
        // already Completed) must never acquire a meeting. Without this,
        // CancelForSessionAsync's own deactivation of the meeting row would
        // simply invite the next Start/Join press to provision a brand-new
        // meeting for a session that no longer happens.
        if (session.Status != ClassSessionStatus.Scheduled)
        {
            return new ProvisionMeetingResult(ProvisionMeetingOutcome.SessionNotProvisionable,
                Detail: _localizer["This session is {0} — no meeting is created for it.", _localizer["ClassSessionStatus." + session.Status].Value].Value);
        }

        var existing = await _db.ProvisionedMeetings.FirstOrDefaultAsync(m => m.SessionId == sessionId && m.IsActive, cancellationToken);

        if (existing is not null)
        {
            if (existing.Status == MeetingProvisioningStatus.Ready)
            {
                return new ProvisionMeetingResult(ProvisionMeetingOutcome.Ready, existing.JoinUrl, existing.Provider);
            }

            var connection = await _db.TeacherMeetingConnections.FirstOrDefaultAsync(c => c.Id == existing.ConnectionId, cancellationToken);
            if (connection is null || connection.Status != ProviderConnectionStatus.Connected)
            {
                // Never silently fall back to a different connection/provider.
                return new ProvisionMeetingResult(ProvisionMeetingOutcome.ProviderDisconnected, Provider: existing.Provider);
            }

            // Owner decision 2026-08-30: a duration shortfall is a warning to
            // the teacher, never a refusal to create the meeting.
            var existingWarning = CheckCapability(connection, session);

            if (!await TryClaimAsync(existing.Id, cancellationToken))
            {
                return new ProvisionMeetingResult(ProvisionMeetingOutcome.StillProvisioning, Provider: existing.Provider,
                    CapabilityWarning: existingWarning);
            }

            await _db.Entry(existing).ReloadAsync(cancellationToken);
            return await CreateExternalMeetingAsync(existing, connection, session, existingWarning, cancellationToken);
        }

        var defaultConnection = await _db.TeacherMeetingConnections.FirstOrDefaultAsync(
            c => c.TeacherId == session.TeacherId && c.IsDefault && c.Status == ProviderConnectionStatus.Connected, cancellationToken);
        if (defaultConnection is null)
        {
            return new ProvisionMeetingResult(ProvisionMeetingOutcome.NoProviderConnection);
        }

        // Owner decision 2026-08-30 (supersedes the duration-blocking half of
        // D-92): the teacher's own plan limit never prevents the meeting from
        // being created at the session's real scheduled duration.
        var capabilityWarning = CheckCapability(defaultConnection, session);

        var fresh = new ProvisionedMeeting(sessionId, defaultConnection.Id, defaultConnection.Provider, _clock.GetCurrentInstant());
        _db.ProvisionedMeetings.Add(fresh);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Another concurrent request already created the active row for
            // this session — this is the whole point of ux_provisioned_meeting_active_session.
            _db.ChangeTracker.Clear();
            return new ProvisionMeetingResult(ProvisionMeetingOutcome.StillProvisioning, Provider: defaultConnection.Provider,
                CapabilityWarning: capabilityWarning);
        }

        return await CreateExternalMeetingAsync(fresh, defaultConnection, session, capabilityWarning, cancellationToken);
    }

    public async Task<string?> GetCapabilityWarningAsync(long sessionId, CancellationToken cancellationToken)
    {
        var session = await _db.ClassSessions.FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is null)
        {
            return null;
        }

        // Prefer the connection that already owns this session's meeting, so
        // the warning describes the account the meeting will actually run on
        // rather than a default the teacher changed after it was provisioned.
        var existing = await _db.ProvisionedMeetings
            .FirstOrDefaultAsync(m => m.SessionId == sessionId && m.IsActive, cancellationToken);

        var connection = existing is not null
            ? await _db.TeacherMeetingConnections.FirstOrDefaultAsync(c => c.Id == existing.ConnectionId, cancellationToken)
            : await _db.TeacherMeetingConnections.FirstOrDefaultAsync(
                c => c.TeacherId == session.TeacherId && c.IsDefault && c.Status == ProviderConnectionStatus.Connected,
                cancellationToken);

        return connection is null ? null : CheckCapability(connection, session);
    }

    public async Task<string?> GetHostStartUrlAsync(long sessionId, long requestingTeacherId, CancellationToken cancellationToken)
    {
        var session = await _db.ClassSessions.FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is null || session.TeacherId != requestingTeacherId)
        {
            return null;
        }

        var meeting = await _db.ProvisionedMeetings.FirstOrDefaultAsync(
            m => m.SessionId == sessionId && m.IsActive && m.Status == MeetingProvisioningStatus.Ready, cancellationToken);
        if (meeting?.ExternalMeetingId is null)
        {
            return null;
        }

        // Google Meet: no distinct host secret — organizer authority comes
        // from the connected identity, so the stored participant URL IS the
        // host's own link too. Re-fetching from the API would be pointless.
        if (meeting.Provider == VideoProviderType.GoogleMeet)
        {
            return meeting.JoinUrl;
        }

        var connection = await _db.TeacherMeetingConnections.FirstOrDefaultAsync(c => c.Id == meeting.ConnectionId, cancellationToken);
        if (connection is null || connection.Status != ProviderConnectionStatus.Connected)
        {
            return null;
        }

        var client = _clients.First(c => c.Provider == meeting.Provider);
        var accessToken = await _tokenRefresh.GetValidAccessTokenAsync(connection, client, cancellationToken);
        if (accessToken is null)
        {
            return null;
        }

        try
        {
            // Fetched fresh on every call — never cached or persisted.
            return await client.GetHostStartUrlAsync(accessToken, meeting.ExternalMeetingId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fetch a fresh Zoom start_url for session {SessionId}.", sessionId);
            return null;
        }
    }

    public async Task<TeacherReassignmentResult> ReassignTeacherAsync(long sessionId, long newTeacherId, long performedByUserId, CancellationToken cancellationToken)
    {
        var session = await _db.ClassSessions.FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is null)
        {
            return new TeacherReassignmentResult(TeacherReassignmentOutcome.SessionNotFound);
        }

        if (session.Status != ClassSessionStatus.Scheduled || session.StartsAtUtc <= _clock.GetCurrentInstant())
        {
            return new TeacherReassignmentResult(TeacherReassignmentOutcome.SessionNotReassignable,
                _localizer["Only a future session that hasn't started yet can be reassigned."].Value);
        }

        // Owner clarification (2026-08-29): a teacher with no usable
        // Zoom/Google connection must not be assignable to any online
        // session — reassignment is an assignment too.
        var newTeacherReady = await _db.TeacherMeetingConnections.AnyAsync(
            c => c.TeacherId == newTeacherId && c.Status == ProviderConnectionStatus.Connected, cancellationToken);
        if (!newTeacherReady)
        {
            return new TeacherReassignmentResult(TeacherReassignmentOutcome.NewTeacherNotReadyForOnlineSessions,
                _localizer["This teacher has no connected Zoom or Google Meet account — connect one from the Teacher portal before assigning online sessions."].Value);
        }

        var oldTeacherId = session.TeacherId;

        try
        {
            session.ReassignTeacher(newTeacherId);
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsExclusionViolation(ex))
        {
            _db.ChangeTracker.Clear();
            return new TeacherReassignmentResult(TeacherReassignmentOutcome.NewTeacherOverlaps,
                _localizer["The new teacher already has another session scheduled at this exact time."].Value);
        }

        await SupersedeOldMeetingAsync(sessionId, cancellationToken);
        await NotifyEnrolledStudentsOfTeacherChangeAsync(session, cancellationToken);

        _db.AuditLogEntries.Add(new AuditLogEntry("ClassSession", sessionId.ToString(), "TeacherReassigned", performedByUserId,
            null, JsonSerializer.Serialize(new { TeacherId = oldTeacherId }), JsonSerializer.Serialize(new { TeacherId = newTeacherId }),
            _clock.GetCurrentInstant()));
        await _db.SaveChangesAsync(cancellationToken);

        return new TeacherReassignmentResult(TeacherReassignmentOutcome.Reassigned);
    }

    public async Task CancelForSessionAsync(long sessionId, string reason, CancellationToken cancellationToken)
    {
        var meeting = await _db.ProvisionedMeetings.FirstOrDefaultAsync(m => m.SessionId == sessionId && m.IsActive, cancellationToken);
        if (meeting is null)
        {
            return;
        }

        await TryRemoteCancelAsync(meeting, cancellationToken);
        meeting.MarkCancelled(reason);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task SupersedeOldMeetingAsync(long sessionId, CancellationToken cancellationToken)
    {
        var oldMeeting = await _db.ProvisionedMeetings.FirstOrDefaultAsync(m => m.SessionId == sessionId && m.IsActive, cancellationToken);
        if (oldMeeting is null)
        {
            return;
        }

        var cleanedUp = await TryRemoteCancelAsync(oldMeeting, cancellationToken);
        if (cleanedUp)
        {
            oldMeeting.MarkCancelled("Superseded by a teacher reassignment.");
        }
        else
        {
            // The owning connection was unavailable — flag for admin action
            // rather than silently linking the new teacher to it.
            oldMeeting.MarkOrphaned("Teacher reassigned, but the previous connection was unavailable to clean up this meeting.");
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Best-effort remote cancellation against the meeting's OWN
    /// owning connection only. Returns false (never throws) when cleanup
    /// could not be completed, so the caller can flag it instead.</summary>
    private async Task<bool> TryRemoteCancelAsync(ProvisionedMeeting meeting, CancellationToken cancellationToken)
    {
        if (meeting.ExternalMeetingId is null)
        {
            return true; // never actually provisioned externally — nothing to clean up
        }

        var connection = await _db.TeacherMeetingConnections.FirstOrDefaultAsync(c => c.Id == meeting.ConnectionId, cancellationToken);
        if (connection is null || connection.Status != ProviderConnectionStatus.Connected)
        {
            return false;
        }

        try
        {
            var client = _clients.First(c => c.Provider == meeting.Provider);
            var accessToken = await _tokenRefresh.GetValidAccessTokenAsync(connection, client, cancellationToken);
            if (accessToken is null)
            {
                return false;
            }

            await client.CancelMeetingAsync(accessToken, meeting.ExternalMeetingId, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not remotely cancel {Provider} meeting {ExternalMeetingId}.", meeting.Provider, meeting.ExternalMeetingId);
            return false;
        }
    }

    private async Task NotifyEnrolledStudentsOfTeacherChangeAsync(ClassSession session, CancellationToken cancellationToken)
    {
        var enrollments = await _db.SessionEnrollments
            .Where(e => e.SessionId == session.Id && e.State == EnrollmentState.Active)
            .ToListAsync(cancellationToken);
        if (enrollments.Count == 0)
        {
            return;
        }

        var studentIds = enrollments.Select(e => e.StudentId).ToList();
        var students = await _db.Students.Where(s => studentIds.Contains(s.Id)).ToListAsync(cancellationToken);
        var level = await _db.Levels.FirstOrDefaultAsync(l => l.Id == session.LevelId, cancellationToken);
        var zone = DateTimeZoneProviders.Tzdb[session.ScheduleTimeZone];
        var localStart = session.StartsAtUtc.InZone(zone);
        var now = _clock.GetCurrentInstant();

        foreach (var student in students)
        {
            if (student.UserId is null)
            {
                continue; // no independent login to notify (a guardian-only child) — nothing lost financially or in scheduling
            }

            var payload = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["StudentName"] = student.FullName,
                ["LevelCode"] = level?.Code ?? "?",
                ["SessionDate"] = localStart.Date.ToString("yyyy-MM-dd", null),
                ["SessionTime"] = localStart.TimeOfDay.ToString("HH:mm", null),
                ["Reason"] = "TeacherChanged",
            });

            _db.NotificationOutboxItems.Add(new NotificationOutboxItem(
                NotificationEvent.SessionCancelledOrMoved, NotificationChannel.WhatsApp, student.UserId.Value, payload, now));
        }
    }

    /// <summary>Owner clarification (2026-08-29): "Determine one-to-one
    /// versus group capability using the application session's real seat
    /// capacity, not merely its current booking count." Returns a
    /// human-readable blocking message, or null when the session's
    /// configured duration is within the connected account's limit.</summary>
    /// <summary>
    /// Owner decision 2026-08-30, superseding the duration-blocking half of D-92.
    /// Returns a WARNING for the teacher, or null when the connected account
    /// comfortably covers the session. It is deliberately not a veto: the
    /// session's scheduled duration is the financial source of truth and is set
    /// by the centre, so an account limit is an operational fact the teacher
    /// must manage, never a reason to shorten the lesson, alter the student's
    /// debit, change the teacher's pay, or silently switch provider. The only
    /// remaining hard block is having no connected account at all
    /// (<see cref="ProvisionMeetingOutcome.NoProviderConnection"/>).
    /// </summary>
    private string? CheckCapability(TeacherMeetingConnection connection, ClassSession session)
    {
        var isGroupCapable = session.Capacity > 1;

        if (connection.Provider == VideoProviderType.Zoom)
        {
            if (connection.CapabilityTier == MeetingCapabilityTier.Full
                || session.DurationMinutes <= ZoomVideoMeetingProviderClient.ZoomBasicMinutesLimit)
            {
                return null;
            }

            return _localizer["Your Zoom account is Basic (free), which Zoom ends after {0} minutes (one-to-one meetings included), but this session is scheduled for {1} minutes. The session, the student's deducted minutes, and your pay all stay at the full scheduled duration. If Zoom ends the meeting early, use \"Continue meeting\" to bring everyone back into the same session.",
                ZoomVideoMeetingProviderClient.ZoomBasicMinutesLimit, session.DurationMinutes].Value;
        }

        // GoogleMeet — never assumed paid (see GoogleMeetProviderClient's own remarks).
        var limit = isGroupCapable ? GoogleGroupMinutesLimit : GoogleOneToOneMinutesLimit;
        if (session.DurationMinutes <= limit)
        {
            return null;
        }

        return isGroupCapable
            ? _localizer["A free Google account ends group Google Meet calls after {0} minutes, but this session is scheduled for {1} minutes. The session, the student's deducted minutes, and your pay all stay at the full scheduled duration. If Google ends the meeting early, use \"Continue meeting\" to bring everyone back into the same session.",
                GoogleGroupMinutesLimit, session.DurationMinutes].Value
            : _localizer["A free Google account ends one-to-one Google Meet calls after {0} hours, but this session is scheduled for {1} minutes. The session, the student's deducted minutes, and your pay all stay at the full scheduled duration.",
                GoogleOneToOneMinutesLimit / 60, session.DurationMinutes].Value;
    }

    private async Task<bool> TryClaimAsync(long provisionedMeetingId, CancellationToken cancellationToken)
    {
        var now = _clock.GetCurrentInstant();
        var staleThreshold = now.Minus(ClaimStaleAfter);
        // "Id" is quoted deliberately: the PK column keeps EF's default
        // PascalCase name (only the mapped business columns are snake_case),
        // so an unquoted `id` would not resolve in PostgreSQL.
        var claimedRows = await _db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE provisioned_meetings
            SET status = 'Provisioning', claimed_at_utc = {now}, claim_token = {Guid.NewGuid()}, status_detail = NULL
            WHERE ""Id"" = {provisionedMeetingId}
              AND status <> 'Ready'
              AND (status <> 'Provisioning' OR claimed_at_utc < {staleThreshold})
        ", cancellationToken);
        return claimedRows == 1;
    }

    private async Task<ProvisionMeetingResult> CreateExternalMeetingAsync(ProvisionedMeeting meeting,
        TeacherMeetingConnection connection, ClassSession session, string? capabilityWarning,
        CancellationToken cancellationToken)
    {
        var client = _clients.First(c => c.Provider == connection.Provider);
        var accessToken = await _tokenRefresh.GetValidAccessTokenAsync(connection, client, cancellationToken);
        if (accessToken is null)
        {
            connection.MarkError("Access token could not be refreshed — the teacher must reconnect.");
            meeting.MarkDisconnected("The connected account's token could not be refreshed.");
            await _db.SaveChangesAsync(cancellationToken);
            return new ProvisionMeetingResult(ProvisionMeetingOutcome.ProviderDisconnected, Provider: connection.Provider,
                CapabilityWarning: capabilityWarning);
        }

        var isGroupCapable = session.Capacity > 1;
        var topic = $"MVTeaches session #{session.Id}";

        try
        {
            var handle = await client.CreateMeetingAsync(accessToken,
                new ProviderMeetingRequest(session.Id, session.StartsAtUtc, session.DurationMinutes, isGroupCapable, topic),
                cancellationToken);
            meeting.MarkReady(handle.ExternalMeetingId, handle.JoinUrl, _clock.GetCurrentInstant());
            await _db.SaveChangesAsync(cancellationToken);

            // The exact-boundary case (a free Google group session of exactly
            // 60 minutes) is a narrower warning than CheckCapability's
            // over-the-limit one, and the two are mutually exclusive by
            // construction: CheckCapability only fires above the limit.
            string? boundaryWarning = connection.Provider == VideoProviderType.GoogleMeet
                && connection.CapabilityTier != MeetingCapabilityTier.Full
                && isGroupCapable && session.DurationMinutes == GoogleGroupMinutesLimit
                ? _localizer["Google may end this free-tier group meeting automatically at the 60-minute mark."].Value
                : null;

            return new ProvisionMeetingResult(ProvisionMeetingOutcome.Ready, handle.JoinUrl, connection.Provider,
                CapabilityWarning: capabilityWarning ?? boundaryWarning);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to provision a {Provider} meeting for session {SessionId}.", connection.Provider, session.Id);
            meeting.MarkFailed("The video provider rejected the meeting request. Try again shortly, or ask an admin to check the connection.");
            await _db.SaveChangesAsync(cancellationToken);
            return new ProvisionMeetingResult(ProvisionMeetingOutcome.Failed,
                Detail: _localizer["The video provider rejected the meeting request."].Value, Provider: connection.Provider,
                CapabilityWarning: capabilityWarning);
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: "23505" };

    private static bool IsExclusionViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: "23P01" };
}
