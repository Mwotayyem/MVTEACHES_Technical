using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MVTeaches.Domain.Integrations;

namespace MVTeaches.Infrastructure.Persistence.Configurations;

/// <summary>
/// Owner clarification (2026-08-29): provider-neutral Zoom/Google Meet.
/// Every uniqueness guarantee the feature depends on is a real database
/// constraint here, never an application-level check alone — see each
/// index's own remarks for which race it closes.
/// </summary>
public class TeacherMeetingConnectionConfiguration : IEntityTypeConfiguration<TeacherMeetingConnection>
{
    public void Configure(EntityTypeBuilder<TeacherMeetingConnection> b)
    {
        b.ToTable("teacher_meeting_connections");
        b.HasKey(x => x.Id);

        b.Property(x => x.TeacherId).HasColumnName("teacher_id");
        b.Property(x => x.Provider).HasColumnName("provider").HasConversion<string>().HasMaxLength(20);

        b.Property(x => x.ExternalAccountId).HasColumnName("external_account_id").IsRequired();
        b.Property(x => x.ExternalAccountEmail).HasColumnName("external_account_email");

        // Encrypted (Data-Protection) blobs only — never plaintext. See ITokenProtector.
        b.Property(x => x.EncryptedAccessToken).HasColumnName("encrypted_access_token").IsRequired();
        b.Property(x => x.EncryptedRefreshToken).HasColumnName("encrypted_refresh_token");
        b.Property(x => x.AccessTokenExpiresAtUtc).HasColumnName("access_token_expires_at_utc");
        b.Property(x => x.TokenVersion).HasColumnName("token_version").HasDefaultValue(1);

        b.Property(x => x.CapabilityTier).HasColumnName("capability_tier").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.CapabilityMinutesLimit).HasColumnName("capability_minutes_limit");
        b.Property(x => x.CapabilityVerifiedAtUtc).HasColumnName("capability_verified_at_utc");

        b.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.StatusDetail).HasColumnName("status_detail");
        b.Property(x => x.IsDefault).HasColumnName("is_default").HasDefaultValue(false);

        b.Property(x => x.ConnectedAtUtc).HasColumnName("connected_at_utc");
        b.Property(x => x.DisconnectedAtUtc).HasColumnName("disconnected_at_utc");

        // Exactly one connection row per (teacher, provider) — reconnecting
        // reuses it (TeacherMeetingConnection.Reconnect), it never accumulates.
        b.HasIndex(x => new { x.TeacherId, x.Provider }).IsUnique().HasDatabaseName("ux_teacher_meeting_connection");

        // At most one DEFAULT connection per teacher — the provider selection
        // a fresh session's provisioning reads.
        b.HasIndex(x => x.TeacherId).IsUnique().HasDatabaseName("ux_teacher_meeting_connection_default")
            .HasFilter("\"is_default\" = true");
    }
}

public class ProvisionedMeetingConfiguration : IEntityTypeConfiguration<ProvisionedMeeting>
{
    public void Configure(EntityTypeBuilder<ProvisionedMeeting> b)
    {
        b.ToTable("provisioned_meetings");
        b.HasKey(x => x.Id);

        b.Property(x => x.SessionId).HasColumnName("session_id");
        b.Property(x => x.ConnectionId).HasColumnName("connection_id");
        b.Property(x => x.Provider).HasColumnName("provider").HasConversion<string>().HasMaxLength(20);

        b.Property(x => x.ExternalMeetingId).HasColumnName("external_meeting_id");
        b.Property(x => x.JoinUrl).HasColumnName("join_url");

        b.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.StatusDetail).HasColumnName("status_detail");

        b.Property(x => x.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        b.Property(x => x.SupersededByMeetingId).HasColumnName("superseded_by_meeting_id");

        b.Property(x => x.ClaimedAtUtc).HasColumnName("claimed_at_utc");
        b.Property(x => x.ClaimToken).HasColumnName("claim_token");

        b.Property(x => x.ProvisionedAtUtc).HasColumnName("provisioned_at_utc");
        b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");

        b.HasIndex(x => x.ConnectionId);

        // ⭐⭐ THE invariant: at most one ACTIVE external meeting per session,
        // even under concurrent/retried provisioning or a teacher reassignment
        // that supersedes the old row — see ProvisionedMeeting.Supersede.
        b.HasIndex(x => x.SessionId).IsUnique().HasDatabaseName("ux_provisioned_meeting_active_session")
            .HasFilter("\"is_active\" = true");
    }
}

public class OAuthAuthorizationStateConfiguration : IEntityTypeConfiguration<OAuthAuthorizationState>
{
    public void Configure(EntityTypeBuilder<OAuthAuthorizationState> b)
    {
        b.ToTable("oauth_authorization_states");
        b.HasKey(x => x.Id);

        b.Property(x => x.Provider).HasColumnName("provider").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.TeacherId).HasColumnName("teacher_id");
        b.Property(x => x.StateToken).HasColumnName("state_token").IsRequired();
        b.Property(x => x.CodeVerifier).HasColumnName("code_verifier").IsRequired();

        b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
        b.Property(x => x.ExpiresAtUtc).HasColumnName("expires_at_utc");
        b.Property(x => x.ConsumedAtUtc).HasColumnName("consumed_at_utc");

        // A forged/reused state token must be rejected outright, not merely
        // treated as "not found" — uniqueness is what makes a guessed or
        // replayed value collide instead of silently matching nothing.
        b.HasIndex(x => x.StateToken).IsUnique().HasDatabaseName("ux_oauth_state_token");
    }
}
