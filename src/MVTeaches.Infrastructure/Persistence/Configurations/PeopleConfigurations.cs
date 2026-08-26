using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MVTeaches.Domain.People;
using MVTeaches.Infrastructure.Identity;

namespace MVTeaches.Infrastructure.Persistence.Configurations;

public class GuardianConfiguration : IEntityTypeConfiguration<Guardian>
{
    public void Configure(EntityTypeBuilder<Guardian> b)
    {
        b.ToTable("guardians");
        b.HasKey(x => x.Id);
        b.Property(x => x.UserId).HasColumnName("user_id");
        b.HasIndex(x => x.UserId).IsUnique(); // 1:1 with the login (§7.1)
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.FullName).HasColumnName("full_name").IsRequired();
        b.Ignore(x => x.Guardianships);
    }
}

public class StudentConfiguration : IEntityTypeConfiguration<Student>
{
    public void Configure(EntityTypeBuilder<Student> b)
    {
        b.ToTable("students");
        b.HasKey(x => x.Id);
        b.Property(x => x.UserId).HasColumnName("user_id"); // ⚠️ nullable by design — §7.1
        b.HasIndex(x => x.UserId).IsUnique().HasFilter("\"user_id\" IS NOT NULL");
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.CountryId).HasColumnName("country_id");
        b.Property(x => x.FullName).HasColumnName("full_name").IsRequired();
        b.Property(x => x.DateOfBirth).HasColumnName("date_of_birth").HasColumnType("date");
        b.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(30);
        b.Ignore(x => x.CanPressJoin);
    }
}

public class TeacherConfiguration : IEntityTypeConfiguration<Teacher>
{
    public void Configure(EntityTypeBuilder<Teacher> b)
    {
        b.ToTable("teachers");
        b.HasKey(x => x.Id);
        b.Property(x => x.UserId).HasColumnName("user_id");
        b.HasIndex(x => x.UserId).IsUnique(); // 1:1 (§7.1)
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.FullName).HasColumnName("full_name").IsRequired();
        b.Property(x => x.TimeZoneId).HasColumnName("timezone_id").IsRequired();
        b.Property(x => x.IsActive).HasColumnName("is_active");
    }
}

public class GuardianshipConfiguration : IEntityTypeConfiguration<Guardianship>
{
    public void Configure(EntityTypeBuilder<Guardianship> b)
    {
        b.ToTable("guardianships");
        b.HasKey(x => new { x.GuardianId, x.StudentId });

        b.HasOne(x => x.Guardian).WithMany(g => g.Guardianships).HasForeignKey(x => x.GuardianId);
        b.HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId);

        b.Property(x => x.Relationship).HasColumnName("relationship").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.IsPrimary).HasColumnName("is_primary");
        b.Property(x => x.CanPay).HasColumnName("can_pay");
        b.Property(x => x.LinkedAtUtc).HasColumnName("linked_at_utc");
        b.Property(x => x.LinkedByUserId).HasColumnName("linked_by");

        // ⭐ Exactly one primary guardian per student (§7.2's ux_guardianship_primary).
        b.HasIndex(x => x.StudentId)
            .IsUnique()
            .HasDatabaseName("ux_guardianship_primary")
            .HasFilter("\"is_primary\" = true");
    }
}
