using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MVTeaches.Domain.Migration;

namespace MVTeaches.Infrastructure.Persistence.Configurations;

public class MigrationBatchConfiguration : IEntityTypeConfiguration<MigrationBatch>
{
    public void Configure(EntityTypeBuilder<MigrationBatch> b)
    {
        b.ToTable("migration_batches");
        b.HasKey(x => x.Id);
        b.Property(x => x.BatchId).HasColumnName("batch_id");
        b.HasIndex(x => x.BatchId).IsUnique();
        b.Property(x => x.SourceFileName).HasColumnName("source_file_name").IsRequired();
        b.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.TotalRows).HasColumnName("total_rows");
        b.Property(x => x.ValidRows).HasColumnName("valid_rows");
        b.Property(x => x.ErrorRows).HasColumnName("error_rows");
        b.Property(x => x.ImportedRows).HasColumnName("imported_rows");
        b.Property(x => x.UploadedByUserId).HasColumnName("uploaded_by");
        b.Property(x => x.UploadedAtUtc).HasColumnName("uploaded_at_utc");
        b.Property(x => x.ImportedAtUtc).HasColumnName("imported_at_utc");
        b.Property(x => x.RolledBackAtUtc).HasColumnName("rolled_back_at_utc");
    }
}

public class MigrationRecordConfiguration : IEntityTypeConfiguration<MigrationRecord>
{
    public void Configure(EntityTypeBuilder<MigrationRecord> b)
    {
        b.ToTable("migration_records");
        b.HasKey(x => x.Id);
        b.Property(x => x.BatchId).HasColumnName("batch_id");
        b.Property(x => x.Source).HasColumnName("source").IsRequired();
        b.Property(x => x.SourceReference).HasColumnName("source_reference");
        b.Property(x => x.StudentId).HasColumnName("student_id");
        b.Property(x => x.GuardianId).HasColumnName("guardian_id");
        b.Property(x => x.RawPayloadJson).HasColumnName("raw_payload").HasColumnType("jsonb").IsRequired();
        b.Property(x => x.LevelCode).HasColumnName("level_code");
        b.Property(x => x.RemainingMinutes).HasColumnName("remaining_minutes");
        b.Property(x => x.AmountPaid).HasColumnName("amount_paid").HasColumnType("numeric(12,3)");
        b.Property(x => x.Currency).HasColumnName("currency").HasMaxLength(3);
        b.Property(x => x.PaidOn).HasColumnName("paid_on").HasColumnType("date");
        b.Property(x => x.SubscriptionStart).HasColumnName("subscription_start").HasColumnType("date");
        b.Property(x => x.SubscriptionEnd).HasColumnName("subscription_end").HasColumnType("date");
        b.Property(x => x.Notes).HasColumnName("notes");
        b.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.ErrorMessage).HasColumnName("error_message");
        b.Property(x => x.ImportedByUserId).HasColumnName("imported_by");
        b.Property(x => x.ImportedAtUtc).HasColumnName("imported_at_utc");

        b.HasIndex(x => x.BatchId);
    }
}
