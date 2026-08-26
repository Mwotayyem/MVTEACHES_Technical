using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MVTeaches.Domain.Ledger;

namespace MVTeaches.Infrastructure.Persistence.Configurations;

/// <summary>
/// §20.2 — "the single most important table in the project." Append-only:
/// this configuration does not, by itself, forbid UPDATE/DELETE — Infrastructure
/// must also revoke UPDATE/DELETE privileges on this table for the application
/// role at the database level (see the deployment scripts) since EF Core
/// mapping alone cannot guarantee that.
/// </summary>
public class EntitlementLedgerEntryConfiguration : IEntityTypeConfiguration<EntitlementLedgerEntry>
{
    public void Configure(EntityTypeBuilder<EntitlementLedgerEntry> b)
    {
        b.ToTable("entitlement_ledger", t => t.HasCheckConstraint("ck_ledger_delta_nonzero", "delta_minutes <> 0"));
        b.HasKey(x => x.Id);

        b.Property(x => x.StudentId).HasColumnName("student_id");
        b.Property(x => x.SubscriptionId).HasColumnName("subscription_id");
        b.Property(x => x.CourseId).HasColumnName("course_id");
        b.Property(x => x.LevelId).HasColumnName("level_id");
        b.Property(x => x.DeltaMinutes).HasColumnName("delta_minutes");
        b.Property(x => x.Reason).HasColumnName("reason").HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.SessionId).HasColumnName("session_id");
        b.Property(x => x.PaymentId).HasColumnName("payment_id");
        b.Property(x => x.MigrationRecordId).HasColumnName("migration_id");
        b.Property(x => x.ReversesEntryId).HasColumnName("reverses_id");
        b.Property(x => x.PerformedByUserId).HasColumnName("performed_by");
        b.Property(x => x.Note).HasColumnName("note");
        b.Property(x => x.ExpiresOn).HasColumnName("expires_on").HasColumnType("date");
        b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");

        b.HasIndex(x => new { x.StudentId, x.CreatedAtUtc }).HasDatabaseName("ix_ent_student");
        b.HasIndex(x => x.SubscriptionId).HasDatabaseName("ix_ent_sub");

        // ⭐⭐ THE invariant: the SAME session can never consume the SAME
        // student's balance twice — a partial unique index scoped to the
        // Consumption reason only (§20.2's ux_ent_consumption).
        b.HasIndex(x => new { x.SessionId, x.StudentId })
            .IsUnique()
            .HasDatabaseName("ux_ent_consumption")
            .HasFilter("\"reason\" = 'Consumption'");
    }
}
