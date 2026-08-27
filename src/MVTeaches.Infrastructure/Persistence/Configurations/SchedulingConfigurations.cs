using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Scheduling;
using NodaTime;

namespace MVTeaches.Infrastructure.Persistence.Configurations;

public class RecurringScheduleConfiguration : IEntityTypeConfiguration<RecurringSchedule>
{
    public void Configure(EntityTypeBuilder<RecurringSchedule> b)
    {
        b.ToTable("recurring_schedules", t => t.HasCheckConstraint(
            "ck_recurring_days_len", "array_length(days_of_week, 1) BETWEEN 1 AND 7"));
        b.HasKey(x => x.Id);

        b.Property(x => x.CountryId).HasColumnName("country_id");
        b.Property(x => x.CourseId).HasColumnName("course_id");
        b.Property(x => x.LevelId).HasColumnName("level_id");
        b.Property(x => x.AgeGroupId).HasColumnName("age_group_id");
        b.Property(x => x.TeacherId).HasColumnName("teacher_id");

        var daysConverter = new ValueConverter<IReadOnlyList<IsoDayOfWeek>, short[]>(
            v => v.Select(d => (short)d).ToArray(),
            v => v.Select(s => (IsoDayOfWeek)s).ToArray());
        var daysComparer = new ValueComparer<IReadOnlyList<IsoDayOfWeek>>(
            (a, bb) => a!.SequenceEqual(bb!),
            v => v.Aggregate(0, (h, d) => HashCode.Combine(h, d)),
            v => v.ToArray());

        b.Property(x => x.DaysOfWeek)
            .HasColumnName("days_of_week")
            .HasColumnType("smallint[]")
            .HasConversion(daysConverter, daysComparer);

        b.Property(x => x.StartLocal).HasColumnName("start_local").HasColumnType("time");
        b.Property(x => x.DurationMinutes).HasColumnName("duration_minutes");
        b.Property(x => x.TimeZoneId).HasColumnName("timezone_id").IsRequired();
        b.Property(x => x.StartsOn).HasColumnName("starts_on").HasColumnType("date");
        b.Property(x => x.EndsOn).HasColumnName("ends_on").HasColumnType("date");
        b.Property(x => x.Capacity).HasColumnName("capacity");
        b.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.CreatedByUserId).HasColumnName("created_by");
    }
}

public class ClassSessionConfiguration : IEntityTypeConfiguration<ClassSession>
{
    public void Configure(EntityTypeBuilder<ClassSession> b)
    {
        b.ToTable("class_sessions", t =>
        {
            t.HasCheckConstraint("ck_session_end_after_start", "ends_at_utc > starts_at_utc");
            t.HasCheckConstraint("ck_session_seats", "seats_taken <= capacity");
            t.HasCheckConstraint("ck_session_capacity_band", "capacity BETWEEN 1 AND 10");
            t.HasCheckConstraint("ck_session_duration_positive", "duration_minutes > 0");
            // ⭐⭐ no_teacher_overlap (§14.2): a physical impossibility, not an
            // application check. Requires the btree_gist extension — enabled in
            // the initial migration. See Migrations/*_InitialCreate for the
            // ALTER TABLE ... EXCLUDE USING gist statement (EF Core's fluent
            // API has no first-class support for PostgreSQL EXCLUDE
            // constraints, so it is added as raw SQL there).
        });
        b.HasKey(x => x.Id);

        b.Property(x => x.CountryId).HasColumnName("country_id");
        b.Property(x => x.RecurringScheduleId).HasColumnName("recurring_id");
        b.Property(x => x.CourseId).HasColumnName("course_id");
        b.Property(x => x.LevelId).HasColumnName("level_id");
        b.Property(x => x.AgeGroupId).HasColumnName("age_group_id");
        b.Property(x => x.TeacherId).HasColumnName("teacher_id");

        b.Property(x => x.StartsAtUtc).HasColumnName("starts_at_utc");
        b.Property(x => x.EndsAtUtc).HasColumnName("ends_at_utc");
        b.Property(x => x.DurationMinutes).HasColumnName("duration_minutes");
        b.Property(x => x.ScheduleTimeZone).HasColumnName("schedule_tz").IsRequired();
        b.Property(x => x.LocalStartText).HasColumnName("local_start_text").IsRequired();

        b.Property(x => x.SessionType).HasColumnName("session_type").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Capacity).HasColumnName("capacity").HasDefaultValue(4);
        b.Property(x => x.SeatsTaken).HasColumnName("seats_taken").HasDefaultValue(0);

        b.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).HasDefaultValue(ClassSessionStatus.Scheduled);
        b.Property(x => x.CancelReason).HasColumnName("cancel_reason");
        b.Property(x => x.CancelledByUserId).HasColumnName("cancelled_by");
        b.Property(x => x.ReplacedBySessionId).HasColumnName("replaced_by_id");
        b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");

        b.HasIndex(x => x.TeacherId);
        b.HasIndex(x => new { x.StartsAtUtc, x.EndsAtUtc });
    }
}

public class SessionEnrollmentConfiguration : IEntityTypeConfiguration<SessionEnrollment>
{
    public void Configure(EntityTypeBuilder<SessionEnrollment> b)
    {
        b.ToTable("session_enrollments");
        b.HasKey(x => x.Id);

        b.Property(x => x.SessionId).HasColumnName("session_id");
        b.Property(x => x.StudentId).HasColumnName("student_id");
        b.Property(x => x.AgeGroupAtEnrollment).HasColumnName("age_group_at_enrollment");
        b.Property(x => x.State).HasColumnName("state").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.EnrolledAtUtc).HasColumnName("enrolled_at_utc");
        b.Property(x => x.EnrolledByUserId).HasColumnName("enrolled_by");
        b.Property(x => x.CompensatesForSessionId).HasColumnName("compensates_for_session_id");

        // §15.1: prevents a duplicate ACTIVE enrollment for the same student in
        // the same session — cancelled/transferred history is kept, not unique.
        b.HasIndex(x => new { x.SessionId, x.StudentId })
            .IsUnique()
            .HasDatabaseName("ux_enrollment_active")
            .HasFilter("\"state\" = 'Active'");
    }
}

public class TeacherAvailabilityRuleConfiguration : IEntityTypeConfiguration<TeacherAvailabilityRule>
{
    public void Configure(EntityTypeBuilder<TeacherAvailabilityRule> b)
    {
        b.ToTable("teacher_availability_rules", t => t.HasCheckConstraint("ck_avail_end_after_start", "end_local > start_local"));
        b.HasKey(x => x.Id);
        b.Property(x => x.TeacherId).HasColumnName("teacher_id");
        b.Property(x => x.DayOfWeek).HasColumnName("day_of_week").HasConversion(v => (short)v, v => (IsoDayOfWeek)v);
        b.Property(x => x.StartLocal).HasColumnName("start_local").HasColumnType("time");
        b.Property(x => x.EndLocal).HasColumnName("end_local").HasColumnType("time");
        b.Property(x => x.TimeZoneId).HasColumnName("timezone_id").IsRequired();
        b.Property(x => x.ValidFrom).HasColumnName("valid_from").HasColumnType("date");
        b.Property(x => x.ValidTo).HasColumnName("valid_to").HasColumnType("date");
    }
}

public class TeacherTimeOffConfiguration : IEntityTypeConfiguration<TeacherTimeOff>
{
    public void Configure(EntityTypeBuilder<TeacherTimeOff> b)
    {
        b.ToTable("teacher_time_off");
        b.HasKey(x => x.Id);
        b.Property(x => x.TeacherId).HasColumnName("teacher_id");
        b.Property(x => x.StartsAtUtc).HasColumnName("starts_at_utc");
        b.Property(x => x.EndsAtUtc).HasColumnName("ends_at_utc");
        b.Property(x => x.Reason).HasColumnName("reason");
        b.Property(x => x.CreatedByUserId).HasColumnName("created_by");
    }
}

/// <summary>§15.3 — a skipped occurrence is recorded here, never silently dropped.</summary>
public class ScheduleGenerationExceptionConfiguration : IEntityTypeConfiguration<ScheduleGenerationException>
{
    public void Configure(EntityTypeBuilder<ScheduleGenerationException> b)
    {
        b.ToTable("schedule_generation_exceptions");
        b.HasKey(x => x.Id);

        b.Property(x => x.RecurringScheduleId).HasColumnName("recurring_id");
        b.Property(x => x.OccurrenceDate).HasColumnName("occurrence_date").HasColumnType("date");
        b.Property(x => x.Reason).HasColumnName("reason").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Detail).HasColumnName("detail").IsRequired();
        b.Property(x => x.DetectedAtUtc).HasColumnName("detected_at_utc");
        b.Property(x => x.Resolved).HasColumnName("resolved").HasDefaultValue(false);
        b.Property(x => x.ResolvedByUserId).HasColumnName("resolved_by");
        b.Property(x => x.ResolvedAtUtc).HasColumnName("resolved_at_utc");

        // The admin screen's default view is "unresolved, newest first" — this
        // index serves exactly that query, not a speculative one.
        b.HasIndex(x => new { x.Resolved, x.DetectedAtUtc });

        // One recorded exception per (schedule, occurrence date) — a nightly
        // rerun that keeps failing the same collision must not pile up
        // duplicate rows for the admin to wade through.
        b.HasIndex(x => new { x.RecurringScheduleId, x.OccurrenceDate }).IsUnique()
            .HasDatabaseName("ux_schedule_generation_exception");
    }
}
