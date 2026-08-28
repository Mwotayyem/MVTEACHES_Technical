using NodaTime;

namespace MVTeaches.Domain.Integrations;

/// <summary>
/// Owner clarification (2026-08-29): a teacher authorizes MVTeaches against
/// their OWN Zoom or Google account via OAuth; the centre never holds a
/// shared/centre-level account. At most one row exists per
/// (TeacherId, Provider) — see ux_teacher_meeting_connection — so
/// reconnecting after a disconnect/revoke reuses the same row
/// (<see cref="Reconnect"/>) rather than accumulating history rows; the
/// audit trail for meeting ownership lives on <see cref="ProvisionedMeeting"/>
/// and AuditLogEntry, not here.
///
/// <see cref="EncryptedAccessToken"/>/<see cref="EncryptedRefreshToken"/> are
/// ASP.NET Core Data-Protection-encrypted blobs (see ITokenProtector) — this
/// entity and every EF configuration around it must never see or log the
/// plaintext token.
/// </summary>
public class TeacherMeetingConnection
{
    public long Id { get; private set; }
    public long TeacherId { get; private set; }
    public VideoProviderType Provider { get; private set; }

    public string ExternalAccountId { get; private set; } = string.Empty;
    public string? ExternalAccountEmail { get; private set; }

    public string EncryptedAccessToken { get; private set; } = string.Empty;
    public string? EncryptedRefreshToken { get; private set; }
    public Instant? AccessTokenExpiresAtUtc { get; private set; }

    /// <summary>Bumped on every token write. Used only as an observability
    /// counter here — the actual concurrent-refresh guard is the conditional
    /// UPDATE the Infrastructure layer issues against the stored refresh
    /// token itself (see TokenRefreshCoordinator), the same
    /// read-the-row-then-conditional-write idiom as ClassSession.TryTakeSeat.</summary>
    public int TokenVersion { get; private set; }

    public MeetingCapabilityTier CapabilityTier { get; private set; } = MeetingCapabilityTier.Unknown;
    public int? CapabilityMinutesLimit { get; private set; }
    public Instant? CapabilityVerifiedAtUtc { get; private set; }

    public ProviderConnectionStatus Status { get; private set; }
    public string? StatusDetail { get; private set; }

    /// <summary>The provider MVTeaches uses for this teacher's future,
    /// unprovisioned sessions. Changing this must never touch a session
    /// whose meeting already exists (§ owner clarification) — enforced by
    /// MeetingProvisioningService, not here.</summary>
    public bool IsDefault { get; private set; }

    public Instant ConnectedAtUtc { get; private set; }
    public Instant? DisconnectedAtUtc { get; private set; }

    private TeacherMeetingConnection() { }

    public TeacherMeetingConnection(long teacherId, VideoProviderType provider, string externalAccountId,
        string? externalAccountEmail, string encryptedAccessToken, string? encryptedRefreshToken,
        Instant? accessTokenExpiresAtUtc, Instant nowUtc)
    {
        if (string.IsNullOrWhiteSpace(externalAccountId))
        {
            throw new ArgumentException("An external provider account id is required.", nameof(externalAccountId));
        }

        TeacherId = teacherId;
        Provider = provider;
        ExternalAccountId = externalAccountId;
        ExternalAccountEmail = externalAccountEmail;
        EncryptedAccessToken = encryptedAccessToken;
        EncryptedRefreshToken = encryptedRefreshToken;
        AccessTokenExpiresAtUtc = accessTokenExpiresAtUtc;
        Status = ProviderConnectionStatus.Connected;
        ConnectedAtUtc = nowUtc;
        TokenVersion = 1;
    }

    /// <summary>Re-authorizing the same (Teacher, Provider) pair after a
    /// disconnect/revoke/error — possibly under a different external account
    /// if the teacher's plan or login changed.</summary>
    public void Reconnect(string externalAccountId, string? externalAccountEmail, string encryptedAccessToken,
        string? encryptedRefreshToken, Instant? accessTokenExpiresAtUtc, Instant nowUtc)
    {
        ExternalAccountId = externalAccountId;
        ExternalAccountEmail = externalAccountEmail;
        EncryptedAccessToken = encryptedAccessToken;
        EncryptedRefreshToken = encryptedRefreshToken;
        AccessTokenExpiresAtUtc = accessTokenExpiresAtUtc;
        Status = ProviderConnectionStatus.Connected;
        StatusDetail = null;
        DisconnectedAtUtc = null;
        ConnectedAtUtc = nowUtc;
        CapabilityTier = MeetingCapabilityTier.Unknown;
        CapabilityMinutesLimit = null;
        CapabilityVerifiedAtUtc = null;
        TokenVersion++;
    }

    public void UpdateTokens(string encryptedAccessToken, string? encryptedRefreshToken, Instant? accessTokenExpiresAtUtc)
    {
        EncryptedAccessToken = encryptedAccessToken;
        if (encryptedRefreshToken is not null)
        {
            EncryptedRefreshToken = encryptedRefreshToken;
        }

        AccessTokenExpiresAtUtc = accessTokenExpiresAtUtc;
        TokenVersion++;
    }

    public void UpdateCapability(MeetingCapabilityTier tier, int? minutesLimit, Instant verifiedAtUtc)
    {
        CapabilityTier = tier;
        CapabilityMinutesLimit = minutesLimit;
        CapabilityVerifiedAtUtc = verifiedAtUtc;
    }

    public void MarkDefault() => IsDefault = true;

    public void ClearDefault() => IsDefault = false;

    public void Disconnect(Instant nowUtc)
    {
        Status = ProviderConnectionStatus.Disconnected;
        StatusDetail = null;
        DisconnectedAtUtc = nowUtc;
        IsDefault = false;
    }

    public void MarkRevoked(Instant nowUtc, string? detail = null)
    {
        Status = ProviderConnectionStatus.Revoked;
        StatusDetail = detail;
        DisconnectedAtUtc = nowUtc;
        IsDefault = false;
    }

    public void MarkError(string detail)
    {
        Status = ProviderConnectionStatus.Error;
        StatusDetail = detail;
    }

    public bool IsUsable => Status == ProviderConnectionStatus.Connected;
}
