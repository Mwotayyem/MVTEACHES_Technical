using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MVTeaches.Domain.Delivery;
using MVTeaches.Domain.Payroll;

namespace MVTeaches.Infrastructure.Persistence.Configurations;

public class SessionDeliveryConfiguration : IEntityTypeConfiguration<SessionDelivery>
{
    public void Configure(EntityTypeBuilder<SessionDelivery> b)
    {
        b.ToTable("session_delivery");
        b.HasKey(x => x.SessionId); // 1:1 with the session (§17.3)
        b.Property(x => x.SessionId).ValueGeneratedNever();

        b.Property(x => x.TeacherId).HasColumnName("teacher_id");
        b.Property(x => x.DeclaredByUserId).HasColumnName("declared_by");
        b.Property(x => x.DeclaredAtUtc).HasColumnName("declared_at_utc");
        b.Property(x => x.DeclaredMinutes).HasColumnName("declared_minutes");
        b.Property(x => x.TeacherNote).HasColumnName("teacher_note");

        b.Property(x => x.VerifiedByUserId).HasColumnName("verified_by");
        b.Property(x => x.VerifiedAtUtc).HasColumnName("verified_at_utc");
        b.Property(x => x.VerifiedMinutes).HasColumnName("verified_minutes");
        b.Property(x => x.AdminNote).HasColumnName("admin_note");

        b.Property(x => x.RateAmount).HasColumnName("rate_amount").HasColumnType("numeric(12,3)");
        b.Property(x => x.RateCurrency).HasColumnName("rate_currency").HasMaxLength(3);
        b.Property(x => x.RateSourceId).HasColumnName("rate_source_id");

        b.Property(x => x.PayableAmount).HasColumnName("payable_amount").HasColumnType("numeric(12,3)");
        b.Property(x => x.PayrollPeriodId).HasColumnName("payroll_period_id");
        b.Property(x => x.State).HasColumnName("state").HasConversion<string>().HasMaxLength(20).HasDefaultValue(DeliveryState.Pending);

        b.HasIndex(x => new { x.TeacherId, x.VerifiedAtUtc }).HasFilter("\"state\" = 'Verified'");
    }
}

public class TeacherRateConfiguration : IEntityTypeConfiguration<Domain.Payroll.TeacherRate>
{
    public void Configure(EntityTypeBuilder<Domain.Payroll.TeacherRate> b)
    {
        b.ToTable("teacher_rates", t => t.HasCheckConstraint("ck_rate_effective_range", "effective_to IS NULL OR effective_to > effective_from"));
        b.HasKey(x => x.Id);

        b.Property(x => x.TeacherId).HasColumnName("teacher_id");
        b.Property(x => x.CourseId).HasColumnName("course_id");
        b.Property(x => x.LevelId).HasColumnName("level_id");
        b.Property(x => x.AgeGroupId).HasColumnName("age_group_id");

        b.OwnsOne(x => x.Rate, m =>
        {
            m.Property(p => p.Amount).HasColumnName("rate_amount").HasColumnType("numeric(12,3)");
            m.Property(p => p.Currency).HasColumnName("rate_currency").HasMaxLength(3);
        });
        b.Navigation(x => x.Rate).IsRequired();

        b.Property(x => x.Unit).HasColumnName("rate_unit").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.EffectiveFrom).HasColumnName("effective_from").HasColumnType("date");
        b.Property(x => x.EffectiveTo).HasColumnName("effective_to").HasColumnType("date");
        b.Property(x => x.CreatedByUserId).HasColumnName("created_by");
        b.Ignore(x => x.Specificity);

        b.HasIndex(x => new { x.TeacherId, x.EffectiveFrom }).HasDatabaseName("ix_rates_lookup").IsDescending(false, true);
    }
}

public class PayrollPeriodConfiguration : IEntityTypeConfiguration<PayrollPeriod>
{
    public void Configure(EntityTypeBuilder<PayrollPeriod> b)
    {
        b.ToTable("payroll_periods");
        b.HasKey(x => x.Id);

        b.Property(x => x.CountryId).HasColumnName("country_id");
        b.Property(x => x.PeriodStart).HasColumnName("period_start").HasColumnType("date");
        b.Property(x => x.PeriodEnd).HasColumnName("period_end").HasColumnType("date");
        b.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).HasDefaultValue(PayrollPeriodStatus.Open);
        b.Property(x => x.ApprovedByUserId).HasColumnName("approved_by");
        b.Property(x => x.ApprovedAtUtc).HasColumnName("approved_at_utc");
        b.Ignore(x => x.IsLocked);

        b.HasIndex(x => new { x.CountryId, x.PeriodStart, x.PeriodEnd }).IsUnique();
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.PeriodId);
    }
}

public class PayrollLineConfiguration : IEntityTypeConfiguration<PayrollLine>
{
    public void Configure(EntityTypeBuilder<PayrollLine> b)
    {
        b.ToTable("payroll_lines");
        b.HasKey(x => x.Id);

        b.Property(x => x.PeriodId).HasColumnName("period_id");
        b.Property(x => x.TeacherId).HasColumnName("teacher_id");
        b.Property(x => x.SessionId).HasColumnName("session_id");
        b.Property(x => x.Minutes).HasColumnName("minutes");
        b.Property(x => x.RateAmount).HasColumnName("rate_amount").HasColumnType("numeric(12,3)");
        b.Property(x => x.RateCurrency).HasColumnName("rate_currency").HasMaxLength(3);
        b.Property(x => x.Amount).HasColumnName("amount").HasColumnType("numeric(12,3)");

        // ⭐ Cannot pay the same session twice, even if aggregation reruns (§18.2).
        b.HasIndex(x => new { x.PeriodId, x.SessionId }).IsUnique();
    }
}
