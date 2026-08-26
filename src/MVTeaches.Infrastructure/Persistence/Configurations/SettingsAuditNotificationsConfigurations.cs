using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MVTeaches.Domain.Audit;
using MVTeaches.Domain.Notifications;
using MVTeaches.Domain.Settings;

namespace MVTeaches.Infrastructure.Persistence.Configurations;

public class SettingConfiguration : IEntityTypeConfiguration<Setting>
{
    public void Configure(EntityTypeBuilder<Setting> b)
    {
        b.ToTable("settings");
        b.HasKey(x => x.Key);
        b.Property(x => x.Key).HasColumnName("key").HasConversion<string>().HasMaxLength(60).ValueGeneratedNever();
        b.Property(x => x.Value).HasColumnName("value").IsRequired();
        b.Property(x => x.LastUpdatedByUserId).HasColumnName("last_updated_by");
        b.Property(x => x.LastUpdatedAtUtc).HasColumnName("last_updated_at_utc");
        b.Ignore(x => x.PreviousValueForAudit);
    }
}

public class AuditLogEntryConfiguration : IEntityTypeConfiguration<AuditLogEntry>
{
    public void Configure(EntityTypeBuilder<AuditLogEntry> b)
    {
        b.ToTable("audit_logs");
        b.HasKey(x => x.Id);
        b.Property(x => x.EntityType).HasColumnName("entity_type").IsRequired();
        b.Property(x => x.EntityId).HasColumnName("entity_id").IsRequired();
        b.Property(x => x.Action).HasColumnName("action").IsRequired();
        b.Property(x => x.PerformedByUserId).HasColumnName("performed_by");
        b.Property(x => x.Reason).HasColumnName("reason");
        b.Property(x => x.BeforeJson).HasColumnName("before_json").HasColumnType("jsonb");
        b.Property(x => x.AfterJson).HasColumnName("after_json").HasColumnType("jsonb");
        b.Property(x => x.OccurredAtUtc).HasColumnName("occurred_at_utc");

        b.HasIndex(x => new { x.EntityType, x.EntityId, x.OccurredAtUtc }).IsDescending(false, false, true);
    }
}

public class NotificationOutboxItemConfiguration : IEntityTypeConfiguration<NotificationOutboxItem>
{
    public void Configure(EntityTypeBuilder<NotificationOutboxItem> b)
    {
        b.ToTable("notification_outbox");
        b.HasKey(x => x.Id);
        b.Property(x => x.Event).HasColumnName("event").HasConversion<string>().HasMaxLength(50);
        b.Property(x => x.Channel).HasColumnName("channel").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.RecipientUserId).HasColumnName("recipient_user_id");
        b.Property(x => x.PayloadJson).HasColumnName("payload_json").HasColumnType("jsonb");
        b.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).HasDefaultValue(NotificationOutboxStatus.Pending);
        b.Property(x => x.ScheduledForUtc).HasColumnName("scheduled_for_utc");
        b.Property(x => x.SentAtUtc).HasColumnName("sent_at_utc");
        b.Property(x => x.AttemptCount).HasColumnName("attempt_count").HasDefaultValue(0);
        b.Property(x => x.LastError).HasColumnName("last_error");

        // The dispatcher's scan index (§33.4).
        b.HasIndex(x => new { x.Status, x.ScheduledForUtc }).HasFilter("\"status\" = 'Pending'");
    }
}
