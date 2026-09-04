using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MVTeaches.Domain.Payments;
using MVTeaches.Domain.Subscriptions;

namespace MVTeaches.Infrastructure.Persistence.Configurations;

public class SubscriptionConfiguration : IEntityTypeConfiguration<Subscription>
{
    public void Configure(EntityTypeBuilder<Subscription> b)
    {
        b.ToTable("subscriptions", t => t.HasCheckConstraint("ck_subscription_dates", "expires_on > starts_on"));
        b.HasKey(x => x.Id);

        b.Property(x => x.StudentId).HasColumnName("student_id");
        b.Property(x => x.CountryId).HasColumnName("country_id");
        b.Property(x => x.CourseId).HasColumnName("course_id");
        b.Property(x => x.LevelId).HasColumnName("level_id");
        b.Property(x => x.SessionType).HasColumnName("session_type").HasConversion<string>().HasMaxLength(20);

        b.OwnsOne(x => x.Price, m =>
        {
            m.Property(p => p.Amount).HasColumnName("price_amount").HasColumnType("numeric(12,3)");
            m.Property(p => p.Currency).HasColumnName("price_currency").HasMaxLength(3);
        });
        b.Navigation(x => x.Price).IsRequired();

        b.Property(x => x.PricingPlanId).HasColumnName("price_plan_id");
        b.Property(x => x.SessionsCount).HasColumnName("sessions_count");
        b.Property(x => x.MinutesTotal).HasColumnName("minutes_total");
        b.Property(x => x.StartsOn).HasColumnName("starts_on").HasColumnType("date");
        b.Property(x => x.ExpiresOn).HasColumnName("expires_on").HasColumnType("date");
        b.Property(x => x.ValidityDays).HasColumnName("validity_days");
        b.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).HasDefaultValue(SubscriptionStatus.Draft);
        b.Property(x => x.Origin).HasColumnName("origin").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.CreatedByUserId).HasColumnName("created_by");
        b.Property(x => x.CreatedReason).HasColumnName("created_reason");
        b.Property(x => x.ExtendedByUserId).HasColumnName("extended_by");
        b.Property(x => x.ExtendedReason).HasColumnName("extended_reason");
        b.Property(x => x.ExtendedTo).HasColumnName("extended_to").HasColumnType("date");

        // Owner decision 2026-09-05 (promo codes): the discount snapshot.
        // list_price_amount carries the same numeric(12,3) as price_amount
        // because it is the same kind of money, and defaults to 0 only for the
        // rows that predate this column - PromoCodeSnapshotOnSubscriptions
        // backfills those to their own price, so "before the discount" is never
        // a misleading zero.
        b.Property(x => x.PromoCodeId).HasColumnName("promo_code_id");
        b.Property(x => x.ListPriceAmount).HasColumnName("list_price_amount").HasColumnType("numeric(12,3)");
        b.Property(x => x.DiscountPercent).HasColumnName("discount_percent");
        b.Property(x => x.DiscountAmount).HasColumnName("discount_amount").HasColumnType("numeric(12,3)");

        b.HasIndex(x => x.StudentId);
        b.HasIndex(x => x.ExpiresOn);

        // Every use of a code is counted from these rows, so the count can
        // never drift from the subscriptions that actually carry it.
        b.HasIndex(x => x.PromoCodeId);
    }
}

/// <summary>
/// Owner decision 2026-09-05. The unique index on code is the point of this
/// configuration: a pre-save "does this exist?" check cannot see two admins
/// pressing Generate in the same second, and the requirement was that a
/// duplicate is impossible even from a hand-made request.
/// </summary>
public class PromoCodeConfiguration : IEntityTypeConfiguration<PromoCode>
{
    public void Configure(EntityTypeBuilder<PromoCode> b)
    {
        b.ToTable("promo_codes", t =>
        {
            t.HasCheckConstraint("ck_promo_discount_percent", "discount_percent BETWEEN 1 AND 100");
            t.HasCheckConstraint("ck_promo_window", "ends_on IS NULL OR starts_on IS NULL OR ends_on >= starts_on");
            t.HasCheckConstraint("ck_promo_max_total_uses", "max_total_uses IS NULL OR max_total_uses >= 1");
            t.HasCheckConstraint("ck_promo_max_uses_per_student", "max_uses_per_student IS NULL OR max_uses_per_student >= 1");
        });
        b.HasKey(x => x.Id);

        b.Property(x => x.Code).HasColumnName("code").HasMaxLength(PromoCode.CodeLength).IsRequired();
        b.Property(x => x.DiscountPercent).HasColumnName("discount_percent");
        b.Property(x => x.IsActive).HasColumnName("is_active");
        b.Property(x => x.StartsOn).HasColumnName("starts_on").HasColumnType("date");
        b.Property(x => x.EndsOn).HasColumnName("ends_on").HasColumnType("date");
        b.Property(x => x.MaxTotalUses).HasColumnName("max_total_uses");
        b.Property(x => x.MaxUsesPerStudent).HasColumnName("max_uses_per_student");
        b.Property(x => x.CreatedByUserId).HasColumnName("created_by");
        b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
        b.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");

        b.HasIndex(x => x.Code).IsUnique().HasDatabaseName("ux_promo_codes_code");
    }
}

/// <summary>
/// Which packages a code is limited to. NO rows means every package - see
/// PromoCode's own remarks for why that is an absence rather than a flag.
/// </summary>
public class PromoCodePlanConfiguration : IEntityTypeConfiguration<PromoCodePlan>
{
    public void Configure(EntityTypeBuilder<PromoCodePlan> b)
    {
        b.ToTable("promo_code_plans");
        b.HasKey(x => x.Id);

        b.Property(x => x.PromoCodeId).HasColumnName("promo_code_id");
        b.Property(x => x.PricingPlanId).HasColumnName("pricing_plan_id");

        // The same package twice on one code is meaningless, and would make a
        // scope check depend on how many rows happened to be inserted.
        b.HasIndex(x => new { x.PromoCodeId, x.PricingPlanId })
            .IsUnique().HasDatabaseName("ux_promo_code_plan");

        b.HasOne<PromoCode>().WithMany().HasForeignKey(x => x.PromoCodeId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class SubscriptionFreezeConfiguration : IEntityTypeConfiguration<SubscriptionFreeze>
{
    public void Configure(EntityTypeBuilder<SubscriptionFreeze> b)
    {
        b.ToTable("subscription_freezes");
        b.HasKey(x => x.Id);
        b.Property(x => x.SubscriptionId).HasColumnName("subscription_id");
        b.Property(x => x.StartsOn).HasColumnName("starts_on").HasColumnType("date");
        b.Property(x => x.EndsOn).HasColumnName("ends_on").HasColumnType("date");
        b.Property(x => x.Reason).HasColumnName("reason").IsRequired();
        b.Property(x => x.ApprovedByUserId).HasColumnName("approved_by");

        b.HasIndex(x => x.SubscriptionId);
    }
}

public class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> b)
    {
        b.ToTable("payments", t => t.HasCheckConstraint("ck_payment_amount_positive", "amount > 0"));
        b.HasKey(x => x.Id);

        b.Property(x => x.StudentId).HasColumnName("student_id");
        b.Property(x => x.SubscriptionId).HasColumnName("subscription_id");
        b.Property(x => x.PayerUserId).HasColumnName("payer_user_id");

        b.OwnsOne(x => x.Amount, m =>
        {
            m.Property(p => p.Amount).HasColumnName("amount").HasColumnType("numeric(12,3)");
            m.Property(p => p.Currency).HasColumnName("currency").HasMaxLength(3);
        });
        b.Navigation(x => x.Amount).IsRequired();

        // 30, not 20 — "InternationalBankTransfer" (25 chars) is a real
        // PaymentMethod value; a real, reproduced bug this session found
        // (varchar(20) silently rejected it at the database level, a
        // DbUpdateException on every attempt to save one) and fixed here,
        // matching PaymentMethodConfig.Type's own already-correct length.
        b.Property(x => x.Method).HasColumnName("method").HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.ProviderKey).HasColumnName("provider_key").HasDefaultValue("manual");
        b.Property(x => x.ProviderTransactionId).HasColumnName("provider_txn_id");
        b.Property(x => x.ReferenceCode).HasColumnName("reference_code").IsRequired();
        b.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).HasDefaultValue(PaymentStatus.Pending);
        b.Property(x => x.ProofFileId).HasColumnName("proof_file_id");
        b.Property(x => x.ConfirmedByUserId).HasColumnName("confirmed_by");
        b.Property(x => x.ConfirmedAtUtc).HasColumnName("confirmed_at_utc");
        b.Property(x => x.RejectionReason).HasColumnName("rejection_reason");
        b.Property(x => x.PayerDisplayName).HasColumnName("payer_display_name");
        b.Property(x => x.TransferDate).HasColumnName("transfer_date").HasColumnType("date");
        b.Property(x => x.PaymentMethodConfigId).HasColumnName("payment_method_config_id");
        b.Property(x => x.ReceivedAmount).HasColumnName("received_amount").HasColumnType("numeric(12,3)");
        b.Property(x => x.ReceivedCurrency).HasColumnName("received_currency").HasMaxLength(3);
        b.Property(x => x.SupersedesPaymentId).HasColumnName("supersedes_payment_id");
        b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");

        // ⭐ Webhook replay safety (§21.6) + no duplicate receipt approval (§22).
        // For manual payments the same index also gives a coarse, global
        // de-dup on a bank reference number (ProviderTransactionId reused for
        // that purpose — see Payment.AttachTransferDetails) — a real bank
        // reference is only unique per bank/account in practice, not
        // globally, so PaymentService catches a 23505 here as a friendly
        // "looks like this reference was already used" outcome rather than
        // an unhandled crash; this is a deliberate, documented trade-off,
        // not an assumption that the scope is perfectly correct.
        b.HasIndex(x => new { x.ProviderKey, x.ProviderTransactionId }).IsUnique()
            .HasFilter("\"provider_txn_id\" IS NOT NULL");
        b.HasIndex(x => x.ReferenceCode).IsUnique();
        b.HasIndex(x => x.SupersedesPaymentId);
    }
}

public class RefundRequestConfiguration : IEntityTypeConfiguration<RefundRequest>
{
    public void Configure(EntityTypeBuilder<RefundRequest> b)
    {
        b.ToTable("refund_requests");
        b.HasKey(x => x.Id);
        b.Property(x => x.PaymentId).HasColumnName("payment_id");
        b.Property(x => x.RequestedByUserId).HasColumnName("requested_by");
        b.Property(x => x.RequestedAtUtc).HasColumnName("requested_at_utc");
        b.Property(x => x.Reason).HasColumnName("reason").IsRequired();
        b.Property(x => x.Status).HasColumnName("status").HasDefaultValue("Rejected-Policy");
        b.Property(x => x.ResolvedByUserId).HasColumnName("resolved_by");
        b.Property(x => x.ResolvedAtUtc).HasColumnName("resolved_at_utc");
    }
}

public class PaymentMethodConfigConfiguration : IEntityTypeConfiguration<PaymentMethodConfig>
{
    public void Configure(EntityTypeBuilder<PaymentMethodConfig> b)
    {
        b.ToTable("payment_method_configs");
        b.HasKey(x => x.Id);

        b.Property(x => x.Type).HasColumnName("type").HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.BeneficiaryName).HasColumnName("beneficiary_name").HasMaxLength(200);
        b.Property(x => x.CliqAlias).HasColumnName("cliq_alias").HasMaxLength(100);
        b.Property(x => x.Iban).HasColumnName("iban").HasMaxLength(50);
        b.Property(x => x.BankName).HasColumnName("bank_name").HasMaxLength(200);
        b.Property(x => x.SwiftBic).HasColumnName("swift_bic").HasMaxLength(20);
        b.Property(x => x.CountryName).HasColumnName("country_name").HasMaxLength(100);
        b.Property(x => x.Instructions).HasColumnName("instructions");
        b.Property(x => x.AcceptedCurrenciesCsv).HasColumnName("accepted_currencies_csv").HasMaxLength(100);
        b.Property(x => x.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        b.Property(x => x.CreatedByUserId).HasColumnName("created_by");
        b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
        b.Property(x => x.DeactivatedByUserId).HasColumnName("deactivated_by");
        b.Property(x => x.DeactivatedAtUtc).HasColumnName("deactivated_at_utc");

        b.HasIndex(x => new { x.Type, x.IsActive });
    }
}
