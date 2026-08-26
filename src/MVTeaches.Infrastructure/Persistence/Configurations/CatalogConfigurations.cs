using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Common;

namespace MVTeaches.Infrastructure.Persistence.Configurations;

public class CountryConfiguration : IEntityTypeConfiguration<Country>
{
    public void Configure(EntityTypeBuilder<Country> b)
    {
        b.ToTable("countries");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Code).HasColumnName("code").HasMaxLength(2).IsRequired();
        b.HasIndex(x => x.Code).IsUnique();
        b.Property(x => x.NameAr).HasColumnName("name_ar").IsRequired();
        b.Property(x => x.NameEn).HasColumnName("name_en").IsRequired();
        b.Property(x => x.CurrencyCode).HasColumnName("currency_code").HasMaxLength(3).IsRequired();
        b.Property(x => x.PhoneCountryCode).HasColumnName("phone_country_code").IsRequired();
        b.Property(x => x.DefaultTimeZone).HasColumnName("default_timezone").IsRequired();
        b.Property(x => x.PaymentProviderKey).HasColumnName("payment_provider_key").HasDefaultValue("manual");
        b.Property(x => x.IsDefaultIntl).HasColumnName("is_default_intl");
        b.Property(x => x.IsActive).HasColumnName("is_active");
    }
}

public class CourseConfiguration : IEntityTypeConfiguration<Course>
{
    public void Configure(EntityTypeBuilder<Course> b)
    {
        b.ToTable("courses");
        b.HasKey(x => x.Id);
        b.Property(x => x.Code).HasColumnName("code").IsRequired();
        b.HasIndex(x => x.Code).IsUnique();
        b.Property(x => x.NameAr).HasColumnName("name_ar").IsRequired();
        b.Property(x => x.NameEn).HasColumnName("name_en").IsRequired();
        b.Property(x => x.IsLeveled).HasColumnName("is_leveled");
        b.Property(x => x.GrantsCertificate).HasColumnName("grants_certificate");
        b.Property(x => x.IsActive).HasColumnName("is_active");
    }
}

public class LevelConfiguration : IEntityTypeConfiguration<Level>
{
    public void Configure(EntityTypeBuilder<Level> b)
    {
        b.ToTable("levels");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Code).HasColumnName("code").IsRequired();
        b.HasIndex(x => x.Code).IsUnique();
        b.Property(x => x.NameAr).HasColumnName("name_ar").IsRequired();
        b.Property(x => x.NameEn).HasColumnName("name_en").IsRequired();
        b.Property(x => x.SortOrder).HasColumnName("sort_order");
        b.HasIndex(x => x.SortOrder).IsUnique();
        b.Property(x => x.IsActive).HasColumnName("is_active");
    }
}

public class AgeGroupConfiguration : IEntityTypeConfiguration<AgeGroup>
{
    public void Configure(EntityTypeBuilder<AgeGroup> b)
    {
        b.ToTable("age_groups");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Code).HasColumnName("code").IsRequired();
        b.HasIndex(x => x.Code).IsUnique();
        b.Property(x => x.MinAge).HasColumnName("min_age");
        b.Property(x => x.MaxAge).HasColumnName("max_age");
        b.Property(x => x.IsMinor).HasColumnName("is_minor");
        b.ToTable(t => t.HasCheckConstraint("ck_age_groups_range", "max_age IS NULL OR max_age >= min_age"));
    }
}

public class PricingPlanConfiguration : IEntityTypeConfiguration<PricingPlan>
{
    public void Configure(EntityTypeBuilder<PricingPlan> b)
    {
        b.ToTable("pricing_plans", t => t.HasCheckConstraint("ck_pricing_plans_amount", "amount >= 0"));
        b.HasKey(x => x.Id);
        b.Property(x => x.CountryId).HasColumnName("country_id");
        b.Property(x => x.CourseId).HasColumnName("course_id");
        b.Property(x => x.LevelId).HasColumnName("level_id");
        b.Property(x => x.AgeGroupId).HasColumnName("age_group_id");
        b.Property(x => x.SessionType).HasColumnName("session_type").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.SessionsCount).HasColumnName("sessions_count");
        b.Property(x => x.MinutesTotal).HasColumnName("minutes_total");

        b.OwnsOne(x => x.Amount, m =>
        {
            m.Property(p => p.Amount).HasColumnName("amount").HasColumnType("numeric(12,3)");
            m.Property(p => p.Currency).HasColumnName("currency").HasMaxLength(3);
        });
        b.Navigation(x => x.Amount).IsRequired();

        b.Property(x => x.ValidityDays).HasColumnName("validity_days");
        b.Property(x => x.IsActive).HasColumnName("is_active");
        b.Property(x => x.EffectiveFrom).HasColumnName("effective_from");
        b.Property(x => x.EffectiveTo).HasColumnName("effective_to");
        b.Property(x => x.CreatedByUserId).HasColumnName("created_by");

        b.HasIndex(x => new { x.CountryId, x.CourseId, x.LevelId, x.IsActive }).HasDatabaseName("ix_plans_lookup");
    }
}
