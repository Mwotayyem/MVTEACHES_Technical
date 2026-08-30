using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Payments;
using MVTeaches.Domain.Payments;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;

namespace MVTeaches.Infrastructure.Payments;

/// <inheritdoc cref="IPaymentMethodConfigService"/>
public class PaymentMethodConfigService : IPaymentMethodConfigService
{
    private readonly MvTeachesDbContext _db;
    private readonly IClock _clock;

    public PaymentMethodConfigService(MvTeachesDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<CreatePaymentMethodResult> CreateAsync(PaymentMethod type, string beneficiaryName, string? cliqAlias,
        string? iban, string? bankName, string? swiftBic, string? countryName, string? instructions,
        IReadOnlyList<string> acceptedCurrencies, long createdByUserId, CancellationToken cancellationToken)
    {
        var csv = string.Join(",", acceptedCurrencies.Select(c => c.Trim().ToUpperInvariant()).Where(c => c.Length > 0));
        var config = new PaymentMethodConfig(type, beneficiaryName, cliqAlias, iban, bankName, swiftBic, countryName,
            instructions, csv, createdByUserId, _clock.GetCurrentInstant());

        _db.PaymentMethodConfigs.Add(config);
        await _db.SaveChangesAsync(cancellationToken);

        return new CreatePaymentMethodResult(config.Id);
    }

    public async Task DeactivateAsync(long id, long deactivatedByUserId, CancellationToken cancellationToken)
    {
        var config = await _db.PaymentMethodConfigs.FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Payment method not found.");
        config.Deactivate(deactivatedByUserId, _clock.GetCurrentInstant());
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PaymentMethodConfig>> ListActiveAsync(CancellationToken cancellationToken) =>
        await _db.PaymentMethodConfigs.Where(c => c.IsActive).OrderBy(c => c.Type).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<PaymentMethodConfig>> ListAllAsync(CancellationToken cancellationToken) =>
        await _db.PaymentMethodConfigs.OrderByDescending(c => c.Id).ToListAsync(cancellationToken);
}
