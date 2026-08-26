using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MVTeaches.Domain.Certificates;
using MVTeaches.Domain.Files;
using MVTeaches.Domain.Homework;

namespace MVTeaches.Infrastructure.Persistence.Configurations;

public class LevelProgressConfiguration : IEntityTypeConfiguration<LevelProgress>
{
    public void Configure(EntityTypeBuilder<LevelProgress> b)
    {
        b.ToTable("level_progress");
        b.HasKey(x => new { x.StudentId, x.LevelId, x.CourseId });
        b.Property(x => x.MinutesCompleted).HasColumnName("minutes_completed").HasDefaultValue(0);
        b.Property(x => x.CompletedAtUtc).HasColumnName("completed_at_utc");
        // Deliberately no "required minutes" column here — see the entity's remarks (D-65).
    }
}

public class CertificateConfiguration : IEntityTypeConfiguration<Certificate>
{
    public void Configure(EntityTypeBuilder<Certificate> b)
    {
        b.ToTable("certificates");
        b.HasKey(x => x.Id);
        b.Property(x => x.StudentId).HasColumnName("student_id");
        b.Property(x => x.LevelId).HasColumnName("level_id");
        b.Property(x => x.CourseId).HasColumnName("course_id");
        b.Property(x => x.CertificateNumber).HasColumnName("certificate_no").IsRequired();
        b.HasIndex(x => x.CertificateNumber).IsUnique();
        b.Property(x => x.MinutesCompleted).HasColumnName("minutes_completed");
        b.Property(x => x.IssuedAtUtc).HasColumnName("issued_at_utc");
        b.Property(x => x.IssuedByUserId).HasColumnName("issued_by");
        b.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).HasDefaultValue(CertificateStatus.Issued);
        b.Property(x => x.FileId).HasColumnName("file_id");

        // ⚠️ Exactly one certificate per (student, level, course) — §27.2.
        b.HasIndex(x => new { x.StudentId, x.LevelId, x.CourseId }).IsUnique();
    }
}

public class HomeworkConfiguration : IEntityTypeConfiguration<Domain.Homework.Homework>
{
    public void Configure(EntityTypeBuilder<Domain.Homework.Homework> b)
    {
        b.ToTable("homework");
        b.HasKey(x => x.Id);
        b.Property(x => x.SessionId).HasColumnName("session_id");
        b.Property(x => x.TeacherId).HasColumnName("teacher_id");
        b.Property(x => x.Title).HasColumnName("title").IsRequired();
        b.Property(x => x.Instructions).HasColumnName("instructions");
        b.Property(x => x.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.DueAtUtc).HasColumnName("due_at_utc");
        b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
    }
}

public class HomeworkSubmissionConfiguration : IEntityTypeConfiguration<HomeworkSubmission>
{
    public void Configure(EntityTypeBuilder<HomeworkSubmission> b)
    {
        b.ToTable("homework_submissions");
        b.HasKey(x => x.Id);
        b.Property(x => x.HomeworkId).HasColumnName("homework_id");
        b.Property(x => x.StudentId).HasColumnName("student_id");
        b.Property(x => x.FileId).HasColumnName("file_id");
        b.Property(x => x.SubmittedAtUtc).HasColumnName("submitted_at_utc");
        b.Property(x => x.SubmittedByUserId).HasColumnName("submitted_by");
        b.Property(x => x.Grade).HasColumnName("grade").HasColumnType("numeric(5,2)");
        b.Property(x => x.Feedback).HasColumnName("feedback");
        b.Property(x => x.GradedByTeacherId).HasColumnName("graded_by");
        b.Property(x => x.GradedAtUtc).HasColumnName("graded_at_utc");

        // ⚠️ One submission per (homework, student) in MVP — see Q-16 in the study.
        b.HasIndex(x => new { x.HomeworkId, x.StudentId }).IsUnique();
    }
}

public class FileRecordConfiguration : IEntityTypeConfiguration<FileRecord>
{
    public void Configure(EntityTypeBuilder<FileRecord> b)
    {
        b.ToTable("files");
        b.HasKey(x => x.Id);
        b.Property(x => x.ObjectKey).HasColumnName("object_key");
        b.HasIndex(x => x.ObjectKey).IsUnique();
        b.Property(x => x.OriginalFileName).HasColumnName("original_file_name").IsRequired();
        b.Property(x => x.ContentType).HasColumnName("content_type").IsRequired();
        b.Property(x => x.SizeBytes).HasColumnName("size_bytes");
        b.Property(x => x.Sha256Hash).HasColumnName("sha256_hash").HasMaxLength(64);
        b.Property(x => x.Purpose).HasColumnName("purpose").HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.OwnerStudentId).HasColumnName("owner_student_id");
        b.Property(x => x.UploadedByUserId).HasColumnName("uploaded_by");
        b.Property(x => x.UploadedAtUtc).HasColumnName("uploaded_at_utc");
    }
}
