using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MVTeaches.Domain.Placement;

namespace MVTeaches.Infrastructure.Persistence.Configurations;

public class PlacementInterviewConfiguration : IEntityTypeConfiguration<PlacementInterview>
{
    public void Configure(EntityTypeBuilder<PlacementInterview> b)
    {
        b.ToTable("placement_interviews");
        b.HasKey(x => x.Id);
        b.Property(x => x.StudentId).HasColumnName("student_id");
        b.Property(x => x.InterviewerTeacherId).HasColumnName("interviewer_teacher_id");
        b.Property(x => x.ScheduledAtUtc).HasColumnName("scheduled_at_utc");
        b.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.AssignedLevelId).HasColumnName("assigned_level_id");
        b.Property(x => x.Notes).HasColumnName("notes");
    }
}

public class StudentLevelConfiguration : IEntityTypeConfiguration<StudentLevel>
{
    public void Configure(EntityTypeBuilder<StudentLevel> b)
    {
        b.ToTable("student_levels");
        b.HasKey(x => x.Id);
        b.Property(x => x.StudentId).HasColumnName("student_id");
        b.Property(x => x.LevelId).HasColumnName("level_id");
        b.Property(x => x.AssignedByUserId).HasColumnName("assigned_by");
        b.Property(x => x.AssignedByRole).HasColumnName("assigned_by_role").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Source).HasColumnName("source").HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.PlacementInterviewId).HasColumnName("placement_interview_id");
        b.Property(x => x.Reason).HasColumnName("reason");
        b.Property(x => x.EffectiveFromUtc).HasColumnName("effective_from_utc");
        b.Property(x => x.IsCurrent).HasColumnName("is_current").HasDefaultValue(true);

        // ⭐ One current level per student (§10.3's ux_student_current_level).
        b.HasIndex(x => x.StudentId).IsUnique().HasDatabaseName("ux_student_current_level").HasFilter("\"is_current\" = true");
    }
}
