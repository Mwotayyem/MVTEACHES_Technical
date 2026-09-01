using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.People;

namespace MVTeaches.Infrastructure.Persistence.Configurations;

/// <summary>
/// The two tables added by owner decision 2026-09-01 (offer posters, student
/// notes). Both follow the conventions already in this folder: snake_case
/// column names, enums stored as their string name, and no navigation
/// properties — ids only, resolved by the reading screen.
/// </summary>
public class PromotionalPosterConfiguration : IEntityTypeConfiguration<PromotionalPoster>
{
    public void Configure(EntityTypeBuilder<PromotionalPoster> b)
    {
        b.ToTable("promotional_posters");
        b.HasKey(x => x.Id);

        b.Property(x => x.Title).HasColumnName("title").HasMaxLength(200).IsRequired();
        b.Property(x => x.Details).HasColumnName("details").HasMaxLength(2000);
        b.Property(x => x.ImageFileId).HasColumnName("image_file_id");
        b.Property(x => x.IsActive).HasColumnName("is_active");
        b.Property(x => x.SortOrder).HasColumnName("sort_order");
        b.Property(x => x.LevelId).HasColumnName("level_id");
        b.Property(x => x.PricingPlanId).HasColumnName("pricing_plan_id");
        b.Property(x => x.CreatedByUserId).HasColumnName("created_by");
        b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
        b.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");

        // The student screen reads exactly this: active posters, in order.
        b.HasIndex(x => new { x.IsActive, x.SortOrder });
    }
}

public class StudentNoteConfiguration : IEntityTypeConfiguration<StudentNote>
{
    public void Configure(EntityTypeBuilder<StudentNote> b)
    {
        b.ToTable("student_notes");
        b.HasKey(x => x.Id);

        b.Property(x => x.StudentId).HasColumnName("student_id");
        b.Property(x => x.Category).HasColumnName("category").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Text).HasColumnName("text").HasMaxLength(2000).IsRequired();
        b.Property(x => x.AuthorUserId).HasColumnName("author_user_id");
        b.Property(x => x.AuthorName).HasColumnName("author_name").HasMaxLength(200).IsRequired();
        b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");

        // One student's notes, newest first — the only way this is ever read.
        b.HasIndex(x => new { x.StudentId, x.CreatedAtUtc });
    }
}
