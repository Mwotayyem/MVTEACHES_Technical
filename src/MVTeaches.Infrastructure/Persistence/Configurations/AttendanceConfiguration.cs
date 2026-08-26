using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MVTeaches.Domain.Attendance;

namespace MVTeaches.Infrastructure.Persistence.Configurations;

/// <summary>
/// D-83 anchor. <see cref="AttendanceRecord"/> is written exactly once per
/// (SessionId, StudentId) — the UNIQUE index below is what actually makes a
/// repeated or concurrent Join press a guaranteed no-op; JoinAttendanceService
/// relies on this constraint firing (23505) rather than a read-then-write
/// check, because read-then-write cannot be made race-free on its own.
/// </summary>
public class AttendanceRecordConfiguration : IEntityTypeConfiguration<AttendanceRecord>
{
    public void Configure(EntityTypeBuilder<AttendanceRecord> b)
    {
        b.ToTable("attendance");
        b.HasKey(x => x.Id);

        b.Property(x => x.SessionId).HasColumnName("session_id");
        b.Property(x => x.StudentId).HasColumnName("student_id");
        b.Property(x => x.MarkedByUserId).HasColumnName("marked_by");
        b.Property(x => x.MarkedAtUtc).HasColumnName("marked_at_utc");
        b.Property(x => x.Note).HasColumnName("note");

        // ⭐⭐ THE invariant: the first Join wins, every later one is rejected by
        // the database itself, not by application logic (§16.2).
        b.HasIndex(x => new { x.SessionId, x.StudentId }).IsUnique().HasDatabaseName("ux_attendance_session_student");
    }
}
