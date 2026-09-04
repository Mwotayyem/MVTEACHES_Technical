using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Subscriptions;
using MVTeaches.Domain.Subscriptions;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;

namespace MVTeaches.Infrastructure.Subscriptions;

/// <inheritdoc />
public class PromoCodeService : IPromoCodeService
{
    private readonly MvTeachesDbContext _db;
    private readonly IClock _clock;

    public PromoCodeService(MvTeachesDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>Drawn from a cryptographic RNG rather than Random: these are
    /// handed out one at a time and a guessable sequence would let somebody
    /// work out the next code before it is issued.</summary>
    private static string RandomCode()
    {
        var characters = new char[PromoCode.CodeLength];
        for (var i = 0; i < characters.Length; i++)
        {
            characters[i] = PromoCode.Alphabet[RandomNumberGenerator.GetInt32(PromoCode.Alphabet.Length)];
        }

        return new string(characters);
    }

    public async Task<string> GenerateUnusedCodeAsync(CancellationToken cancellationToken)
    {
        // Ten attempts is generous: the space is 36^6 (over two billion), so a
        // collision means the table is enormous, not that the loop is unlucky.
        // The unique index still stands behind CreateAsync either way - this
        // loop exists so an admin is not handed a code that is already taken,
        // not to guarantee anything on its own.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var candidate = RandomCode();
            if (!await _db.PromoCodes.AnyAsync(p => p.Code == candidate, cancellationToken))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not generate an unused promo code after 10 attempts.");
    }

    public async Task<CreatePromoCodeResult> CreateAsync(string code, int discountPercent, bool isActive,
        LocalDate? startsOn, LocalDate? endsOn, int? maxTotalUses, int? maxUsesPerStudent,
        bool appliesToAllPlans, IReadOnlyList<long> pricingPlanIds, long createdByUserId,
        CancellationToken cancellationToken)
    {
        if (!PromoCode.IsWellFormed((code ?? string.Empty).Trim().ToUpperInvariant()))
        {
            return new CreatePromoCodeResult(CreatePromoCodeOutcome.MalformedCode);
        }

        if (discountPercent is < 1 or > 100)
        {
            return new CreatePromoCodeResult(CreatePromoCodeOutcome.InvalidDiscountPercent);
        }

        if (startsOn is not null && endsOn is not null && endsOn < startsOn)
        {
            return new CreatePromoCodeResult(CreatePromoCodeOutcome.InvalidWindow);
        }

        if (maxTotalUses is < 1 || maxUsesPerStudent is < 1)
        {
            return new CreatePromoCodeResult(CreatePromoCodeOutcome.InvalidUsageLimit);
        }

        var plans = appliesToAllPlans ? new List<long>() : pricingPlanIds.Distinct().ToList();
        if (!appliesToAllPlans && plans.Count == 0)
        {
            return new CreatePromoCodeResult(CreatePromoCodeOutcome.NoPlansChosen);
        }

        var normalised = PromoCode.NormaliseCode(code);
        if (await _db.PromoCodes.AnyAsync(p => p.Code == normalised, cancellationToken))
        {
            return new CreatePromoCodeResult(CreatePromoCodeOutcome.DuplicateCode);
        }

        var promoCode = new PromoCode(normalised, discountPercent, isActive, startsOn, endsOn,
            maxTotalUses, maxUsesPerStudent, createdByUserId, _clock.GetCurrentInstant());
        _db.PromoCodes.Add(promoCode);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex, "ux_promo_codes_code"))
        {
            // The check above cannot see a code inserted between its own read
            // and this write. The index can, and does - this is the guarantee,
            // and the check above is only there to answer politely in the
            // ordinary case.
            _db.ChangeTracker.Clear();
            return new CreatePromoCodeResult(CreatePromoCodeOutcome.DuplicateCode);
        }

        if (plans.Count > 0)
        {
            foreach (var planId in plans)
            {
                _db.PromoCodePlans.Add(new PromoCodePlan(promoCode.Id, planId));
            }

            await _db.SaveChangesAsync(cancellationToken);
        }

        return new CreatePromoCodeResult(CreatePromoCodeOutcome.Created, promoCode.Id);
    }

    public async Task<UpdatePromoCodeOutcome> UpdateAsync(long promoCodeId, int discountPercent,
        LocalDate? startsOn, LocalDate? endsOn, int? maxTotalUses, int? maxUsesPerStudent,
        bool appliesToAllPlans, IReadOnlyList<long> pricingPlanIds, CancellationToken cancellationToken)
    {
        var promoCode = await _db.PromoCodes.FirstOrDefaultAsync(p => p.Id == promoCodeId, cancellationToken);
        if (promoCode is null)
        {
            return UpdatePromoCodeOutcome.NotFound;
        }

        if (discountPercent is < 1 or > 100)
        {
            return UpdatePromoCodeOutcome.InvalidDiscountPercent;
        }

        if (startsOn is not null && endsOn is not null && endsOn < startsOn)
        {
            return UpdatePromoCodeOutcome.InvalidWindow;
        }

        if (maxTotalUses is < 1 || maxUsesPerStudent is < 1)
        {
            return UpdatePromoCodeOutcome.InvalidUsageLimit;
        }

        var plans = appliesToAllPlans ? new List<long>() : pricingPlanIds.Distinct().ToList();
        if (!appliesToAllPlans && plans.Count == 0)
        {
            return UpdatePromoCodeOutcome.NoPlansChosen;
        }

        // Changing the percentage changes only what FUTURE purchases get: every
        // subscription already bought carries its own snapshot of what this
        // code was worth on the day, and none of them is touched here.
        promoCode.Update(discountPercent, startsOn, endsOn, maxTotalUses, maxUsesPerStudent,
            _clock.GetCurrentInstant());

        var existing = await _db.PromoCodePlans.Where(x => x.PromoCodeId == promoCodeId).ToListAsync(cancellationToken);
        _db.PromoCodePlans.RemoveRange(existing);
        foreach (var planId in plans)
        {
            _db.PromoCodePlans.Add(new PromoCodePlan(promoCodeId, planId));
        }

        await _db.SaveChangesAsync(cancellationToken);
        return UpdatePromoCodeOutcome.Updated;
    }

    public async Task<bool> SetActiveAsync(long promoCodeId, bool isActive, CancellationToken cancellationToken)
    {
        var promoCode = await _db.PromoCodes.FirstOrDefaultAsync(p => p.Id == promoCodeId, cancellationToken);
        if (promoCode is null)
        {
            return false;
        }

        promoCode.SetActive(isActive, _clock.GetCurrentInstant());
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<ApplyPromoCodeResult> ApplyAsync(string? code, long pricingPlanId, long studentId,
        CancellationToken cancellationToken)
    {
        var normalised = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (!PromoCode.IsWellFormed(normalised))
        {
            // Malformed and unknown are answered the same way on purpose: a
            // family typing a code should not be able to learn the shape of a
            // valid one by watching the message change.
            return new ApplyPromoCodeResult(null, PromoCodeRejection.NotFound);
        }

        var promoCode = await _db.PromoCodes.FirstOrDefaultAsync(p => p.Code == normalised, cancellationToken);
        if (promoCode is null)
        {
            return new ApplyPromoCodeResult(null, PromoCodeRejection.NotFound);
        }

        if (!promoCode.IsActive)
        {
            return new ApplyPromoCodeResult(null, PromoCodeRejection.Inactive);
        }

        // Today, in the same terms every other date on this project uses - no
        // new timezone rule is introduced here.
        var today = _clock.GetCurrentInstant().InUtc().Date;
        if (promoCode.StartsOn is not null && today < promoCode.StartsOn)
        {
            return new ApplyPromoCodeResult(null, PromoCodeRejection.NotStartedYet);
        }

        if (promoCode.EndsOn is not null && today > promoCode.EndsOn)
        {
            return new ApplyPromoCodeResult(null, PromoCodeRejection.Expired);
        }

        var plan = await _db.PricingPlans.FirstOrDefaultAsync(p => p.Id == pricingPlanId, cancellationToken);
        if (plan is null)
        {
            return new ApplyPromoCodeResult(null, PromoCodeRejection.NotForThisPackage);
        }

        // No scope rows at all means every package; otherwise this package has
        // to be named.
        var scoped = await _db.PromoCodePlans.Where(x => x.PromoCodeId == promoCode.Id)
            .Select(x => x.PricingPlanId).ToListAsync(cancellationToken);
        if (scoped.Count > 0 && !scoped.Contains(pricingPlanId))
        {
            return new ApplyPromoCodeResult(null, PromoCodeRejection.NotForThisPackage);
        }

        if (promoCode.MaxTotalUses is { } totalLimit)
        {
            var used = await _db.Subscriptions.CountAsync(s => s.PromoCodeId == promoCode.Id, cancellationToken);
            if (used >= totalLimit)
            {
                return new ApplyPromoCodeResult(null, PromoCodeRejection.TotalLimitReached);
            }
        }

        if (promoCode.MaxUsesPerStudent is { } studentLimit)
        {
            var usedByStudent = await _db.Subscriptions
                .CountAsync(s => s.PromoCodeId == promoCode.Id && s.StudentId == studentId, cancellationToken);
            if (usedByStudent >= studentLimit)
            {
                return new ApplyPromoCodeResult(null, PromoCodeRejection.StudentLimitReached);
            }
        }

        // The only place a discounted price is ever produced. The list price is
        // the PLAN's own stored amount and the percentage is the CODE's own
        // stored value - neither comes from the caller.
        var listPrice = plan.Amount.Amount;
        var discount = promoCode.DiscountOn(listPrice);
        var finalPrice = listPrice - discount;

        return new ApplyPromoCodeResult(
            new PromoCodeQuote(promoCode.Id, promoCode.Code, promoCode.DiscountPercent,
                listPrice, discount, finalPrice, plan.Amount.Currency),
            null);
    }

    public async Task<IReadOnlyDictionary<long, int>> CountUsesAsync(IReadOnlyList<long> promoCodeIds,
        CancellationToken cancellationToken)
    {
        if (promoCodeIds.Count == 0)
        {
            return new Dictionary<long, int>();
        }

        return await _db.Subscriptions
            .Where(s => s.PromoCodeId != null && promoCodeIds.Contains(s.PromoCodeId!.Value))
            .GroupBy(s => s.PromoCodeId!.Value)
            .Select(g => new { PromoCodeId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.PromoCodeId, x => x.Count, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<long, IReadOnlyList<long>>> GetPlanScopesAsync(
        IReadOnlyList<long> promoCodeIds, CancellationToken cancellationToken)
    {
        if (promoCodeIds.Count == 0)
        {
            return new Dictionary<long, IReadOnlyList<long>>();
        }

        var rows = await _db.PromoCodePlans
            .Where(x => promoCodeIds.Contains(x.PromoCodeId))
            .ToListAsync(cancellationToken);

        return rows.GroupBy(x => x.PromoCodeId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<long>)g.Select(x => x.PricingPlanId).ToList());
    }

    private static bool IsUniqueViolation(DbUpdateException ex, string constraintName) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: "23505" } pg
        && (pg.ConstraintName is null || pg.ConstraintName.Contains(constraintName, StringComparison.OrdinalIgnoreCase));
}
