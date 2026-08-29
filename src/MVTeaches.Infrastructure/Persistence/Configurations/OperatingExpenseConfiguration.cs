using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MVTeaches.Domain.Finance;

namespace MVTeaches.Infrastructure.Persistence.Configurations;

public class OperatingExpenseConfiguration : IEntityTypeConfiguration<OperatingExpense>
{
    public void Configure(EntityTypeBuilder<OperatingExpense> b)
    {
        b.ToTable("operating_expenses", t => t.HasCheckConstraint("ck_operating_expense_amount_positive", "amount > 0"));
        b.HasKey(x => x.Id);

        b.Property(x => x.CountryId).HasColumnName("country_id");
        b.Property(x => x.Category).HasColumnName("category").HasMaxLength(100).IsRequired();

        b.OwnsOne(x => x.Amount, m =>
        {
            m.Property(p => p.Amount).HasColumnName("amount").HasColumnType("numeric(12,3)");
            m.Property(p => p.Currency).HasColumnName("currency").HasMaxLength(3);
        });
        b.Navigation(x => x.Amount).IsRequired();

        b.Property(x => x.IncurredOn).HasColumnName("incurred_on").HasColumnType("date");
        b.Property(x => x.Note).HasColumnName("note");
        b.Property(x => x.EnteredByUserId).HasColumnName("entered_by");
        b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");

        b.HasIndex(x => x.IncurredOn);
    }
}
