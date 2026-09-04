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
        // Owner decision 2026-09-04 — see StudentLevel's own remarks. Restrict,
        // not Cascade: retiring a course must never silently erase the level
        // history of every student who ever took it.
        b.Property(x => x.CourseId).HasColumnName("course_id");
        b.HasOne<MVTeaches.Domain.Catalog.Course>().WithMany().HasForeignKey(x => x.CourseId)
            .OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.LevelId).HasColumnName("level_id");
        b.Property(x => x.AssignedByUserId).HasColumnName("assigned_by");
        b.Property(x => x.AssignedByRole).HasColumnName("assigned_by_role").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Source).HasColumnName("source").HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.PlacementInterviewId).HasColumnName("placement_interview_id");
        b.Property(x => x.Reason).HasColumnName("reason");
        b.Property(x => x.EffectiveFromUtc).HasColumnName("effective_from_utc");
        b.Property(x => x.IsCurrent).HasColumnName("is_current").HasDefaultValue(true);

        // ⭐ One current level per student PER COURSE (§10.3's
        // ux_student_current_level, widened by the owner's 2026-09-04
        // multi-course decision). The old index was on (StudentId) alone, which
        // made a second course's placement collide with the first course's and
        // was therefore the database-level reason a student could only ever
        // hold one level in total. The guarantee is unchanged in kind — still
        // exactly one current row — only its scope moved from the student to
        // the (student, course) pair.
        b.HasIndex(x => new { x.StudentId, x.CourseId }).IsUnique()
            .HasDatabaseName("ux_student_course_current_level").HasFilter("\"is_current\" = true");
    }
}

/// <summary>Owner decision 2026-08-30, reversing D-48. See
/// PlacementTestVersion's own remarks for the Draft/Published/IsActive
/// lifecycle this configuration enforces at the database level.</summary>
public class PlacementTestVersionConfiguration : IEntityTypeConfiguration<PlacementTestVersion>
{
    public void Configure(EntityTypeBuilder<PlacementTestVersion> b)
    {
        b.ToTable("placement_test_versions");
        b.HasKey(x => x.Id);
        b.Property(x => x.Title).HasColumnName("title").IsRequired();
        // Owner decision 2026-09-04 — see PlacementTestVersion.CourseId.
        b.Property(x => x.CourseId).HasColumnName("course_id");
        b.HasOne<MVTeaches.Domain.Catalog.Course>().WithMany().HasForeignKey(x => x.CourseId)
            .OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.IsActive).HasColumnName("is_active").HasDefaultValue(false);
        b.Property(x => x.CreatedByUserId).HasColumnName("created_by");
        b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
        b.Property(x => x.PublishedByUserId).HasColumnName("published_by");
        b.Property(x => x.PublishedAtUtc).HasColumnName("published_at_utc");

        // At most one active version at a time — the real backstop behind
        // PlacementTestAdminService.ActivateAsync's own deactivate-then-activate
        // sequence, the same pattern ux_guardianship_primary and
        // ux_student_current_level already use for "exactly one current X".
        b.HasIndex(x => x.IsActive).IsUnique().HasDatabaseName("ux_placement_test_active").HasFilter("\"is_active\" = true");
    }
}

public class PlacementQuestionConfiguration : IEntityTypeConfiguration<PlacementQuestion>
{
    public void Configure(EntityTypeBuilder<PlacementQuestion> b)
    {
        b.ToTable("placement_questions", t => t.HasCheckConstraint("ck_placement_question_points_positive", "points > 0"));
        b.HasKey(x => x.Id);
        b.Property(x => x.TestVersionId).HasColumnName("test_version_id");
        b.Property(x => x.Text).HasColumnName("text").IsRequired();
        b.Property(x => x.Points).HasColumnName("points");
        b.Property(x => x.SortOrder).HasColumnName("sort_order");

        b.HasOne<PlacementTestVersion>().WithMany().HasForeignKey(x => x.TestVersionId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class PlacementAnswerChoiceConfiguration : IEntityTypeConfiguration<PlacementAnswerChoice>
{
    public void Configure(EntityTypeBuilder<PlacementAnswerChoice> b)
    {
        b.ToTable("placement_answer_choices");
        b.HasKey(x => x.Id);
        b.Property(x => x.QuestionId).HasColumnName("question_id");
        b.Property(x => x.Text).HasColumnName("text").IsRequired();
        b.Property(x => x.IsCorrect).HasColumnName("is_correct");
        b.Property(x => x.SortOrder).HasColumnName("sort_order");

        b.HasOne<PlacementQuestion>().WithMany().HasForeignKey(x => x.QuestionId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class PlacementScoreRangeConfiguration : IEntityTypeConfiguration<PlacementScoreRange>
{
    public void Configure(EntityTypeBuilder<PlacementScoreRange> b)
    {
        b.ToTable("placement_score_ranges", t => t.HasCheckConstraint("ck_placement_score_range_valid", "max_score >= min_score AND min_score >= 0"));
        b.HasKey(x => x.Id);
        b.Property(x => x.TestVersionId).HasColumnName("test_version_id");
        b.Property(x => x.MinScore).HasColumnName("min_score");
        b.Property(x => x.MaxScore).HasColumnName("max_score");
        b.Property(x => x.LevelId).HasColumnName("level_id");

        b.HasOne<PlacementTestVersion>().WithMany().HasForeignKey(x => x.TestVersionId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<MVTeaches.Domain.Catalog.Level>().WithMany().HasForeignKey(x => x.LevelId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class PlacementAttemptConfiguration : IEntityTypeConfiguration<PlacementAttempt>
{
    public void Configure(EntityTypeBuilder<PlacementAttempt> b)
    {
        b.ToTable("placement_attempts");
        b.HasKey(x => x.Id);
        b.Property(x => x.StudentId).HasColumnName("student_id");
        b.Property(x => x.TestVersionId).HasColumnName("test_version_id");
        b.Property(x => x.ApprovedRetakeRequestId).HasColumnName("approved_retake_request_id");
        b.Property(x => x.StartedByUserId).HasColumnName("started_by");
        b.Property(x => x.StartedAtUtc).HasColumnName("started_at_utc");
        b.Property(x => x.CompletedAtUtc).HasColumnName("completed_at_utc");
        b.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Score).HasColumnName("score");
        b.Property(x => x.AssignedLevelId).HasColumnName("assigned_level_id");

        b.HasOne<PlacementTestVersion>().WithMany().HasForeignKey(x => x.TestVersionId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.StudentId).HasDatabaseName("ix_placement_attempts_student");

        // At most one IN-PROGRESS attempt per student — StartAttemptAsync's own
        // idempotent-resume check is the friendly path; this is the real guard
        // against two concurrent Start calls each creating their own row.
        b.HasIndex(x => x.StudentId).IsUnique().HasDatabaseName("ux_placement_attempt_in_progress").HasFilter("\"status\" = 'InProgress'");
    }
}

public class PlacementAttemptAnswerConfiguration : IEntityTypeConfiguration<PlacementAttemptAnswer>
{
    public void Configure(EntityTypeBuilder<PlacementAttemptAnswer> b)
    {
        b.ToTable("placement_attempt_answers");
        b.HasKey(x => x.Id);
        b.Property(x => x.AttemptId).HasColumnName("attempt_id");
        b.Property(x => x.QuestionId).HasColumnName("question_id");
        b.Property(x => x.SelectedAnswerChoiceId).HasColumnName("selected_choice_id");
        b.Property(x => x.IsCorrectSnapshot).HasColumnName("is_correct_snapshot");
        b.Property(x => x.PointsAwardedSnapshot).HasColumnName("points_awarded_snapshot");

        b.HasOne<PlacementAttempt>().WithMany().HasForeignKey(x => x.AttemptId).OnDelete(DeleteBehavior.Cascade);
        // One answer per (attempt, question) — a student cannot submit two
        // different answers to the same question in the same attempt.
        b.HasIndex(x => new { x.AttemptId, x.QuestionId }).IsUnique().HasDatabaseName("ux_placement_attempt_answer");
    }
}

public class PlacementRetakeRequestConfiguration : IEntityTypeConfiguration<PlacementRetakeRequest>
{
    public void Configure(EntityTypeBuilder<PlacementRetakeRequest> b)
    {
        b.ToTable("placement_retake_requests");
        b.HasKey(x => x.Id);
        b.Property(x => x.StudentId).HasColumnName("student_id");
        b.Property(x => x.RequestedByUserId).HasColumnName("requested_by");
        b.Property(x => x.RequestedAtUtc).HasColumnName("requested_at_utc");
        b.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.DecidedByUserId).HasColumnName("decided_by");
        b.Property(x => x.DecidedAtUtc).HasColumnName("decided_at_utc");
        b.Property(x => x.DecisionReason).HasColumnName("decision_reason");
        b.Property(x => x.ConsumedByAttemptId).HasColumnName("consumed_by_attempt_id");

        b.HasIndex(x => x.StudentId).HasDatabaseName("ix_placement_retake_student");

        // At most one PENDING retake request per student — the same
        // "friendly pre-check plus real DB backstop" split RequestRetakeAsync's
        // own read-then-write relies on.
        b.HasIndex(x => x.StudentId).IsUnique().HasDatabaseName("ux_placement_retake_pending").HasFilter("\"status\" = 'Pending'");

        // An approved retake can be consumed by at most one attempt.
        b.HasIndex(x => x.ConsumedByAttemptId).IsUnique().HasDatabaseName("ux_placement_retake_consumed").HasFilter("\"consumed_by_attempt_id\" IS NOT NULL");
    }
}
