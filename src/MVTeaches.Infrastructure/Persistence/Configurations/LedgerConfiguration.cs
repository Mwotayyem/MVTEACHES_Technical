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
        b.Property(x => x.SessionType).HasColumnName("session_type").HasConversion<string>().HasMaxLength(20);
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
        // Named directly in the HasIndex call (not via a chained HasDatabaseName)
        // — EF Core only recognizes this as a SECOND, distinct index on the same
        // SubscriptionId property when the name is passed here; otherwise it
        // silently merges into whichever other HasIndex call configured the
        // same property first, which is exactly what happened the first time
        // ux_ent_purchase below was added (it renamed this index instead of
        // creating a new one).
        b.HasIndex(x => x.SubscriptionId, "ix_ent_sub");

        // ⭐⭐ THE invariant: the SAME session can never consume the SAME
        // student's balance twice — a partial unique index scoped to the
        // Consumption reason only (§20.2's ux_ent_consumption).
        b.HasIndex(x => new { x.SessionId, x.StudentId })
            .IsUnique()
            .HasDatabaseName("ux_ent_consumption")
            .HasFilter("\"reason\" = 'Consumption'");

        // Release-readiness audit finding: the SAME subscription can never be
        // credited by more than one Purchase entry — mirrors ux_ent_consumption's
        // role on the spend side. Without this, two payments on the same
        // subscription confirmed concurrently (e.g. two admins, or a genuine
        // overpayment/duplicate manual entry) could each independently observe
        // "no Purchase entry posted yet" and both post one, double-crediting the
        // subscription's minutes — PaymentService.SettleSubscriptionIfFullyPaidAsync's
        // own pre-check is a plain SELECT with no ambient serializable
        // transaction, so it needs this real backstop the same way the Join race
        // (D-83) needed ux_ent_consumption.
        b.HasIndex(x => x.SubscriptionId, "ux_ent_purchase")
            .IsUnique()
            .HasFilter("\"reason\" = 'Purchase'");
    }
}
