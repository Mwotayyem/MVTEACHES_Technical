using MVTeaches.Domain.Subscriptions;
using NodaTime;

namespace MVTeaches.Application.Subscriptions;

public enum CreatePromoCodeOutcome
{
    Created,

    /// <summary>Not six characters of A-Z/0-9. The screen generates the code,
    /// so this is reached by a hand-made request, not by an admin typing.</summary>
    MalformedCode,

    /// <summary>Outside 1-100.</summary>
    InvalidDiscountPercent,

    /// <summary>Ends before it starts.</summary>
    InvalidWindow,

    /// <summary>A usage limit below 1. Empty means unlimited; zero would mean
    /// a code that can never be used, which is what disabling is for.</summary>
    InvalidUsageLimit,

    /// <summary>The code already exists. Raised both by the pre-check and by
    /// the unique index catching a race the pre-check could not see.</summary>
    DuplicateCode,

    /// <summary>"Specific packages" was chosen with no package named.</summary>
    NoPlansChosen,
}

public record CreatePromoCodeResult(CreatePromoCodeOutcome Outcome, long? PromoCodeId = null);

public enum UpdatePromoCodeOutcome
{
    Updated,
    NotFound,
    InvalidDiscountPercent,
    InvalidWindow,
    InvalidUsageLimit,
    NoPlansChosen,
}

/// <summary>Why a code cannot be used for this purchase. Every one of these is
/// decided server-side from the database, never from anything the browser
/// sent.</summary>
public enum PromoCodeRejection
{
    /// <summary>No such code.</summary>
    NotFound,

    /// <summary>Switched off by an admin.</summary>
    Inactive,

    /// <summary>Today is before its start date.</summary>
    NotStartedYet,

    /// <summary>Today is after its end date.</summary>
    Expired,

    /// <summary>Real, live, but not for this package.</summary>
    NotForThisPackage,

    /// <summary>Its total usage limit is used up.</summary>
    TotalLimitReached,

    /// <summary>This student has used it as many times as they may.</summary>
    StudentLimitReached,
}

/// <summary>
/// What a code is worth on one specific package, priced by the server.
/// <para><see cref="FinalPrice"/> is what the family will actually be charged
/// and what gets stamped on the subscription; <see cref="ListPrice"/> and
/// <see cref="DiscountAmount"/> exist so the screen can show its work, and so
/// the subscription can keep a record of what the discount was worth on the
/// day.</para>
/// </summary>
public record PromoCodeQuote(long PromoCodeId, string Code, int DiscountPercent,
    decimal ListPrice, decimal DiscountAmount, decimal FinalPrice, string Currency)
{
    /// <summary>A package that costs nothing after the discount. There is no
    /// payment to make, so the purchase path activates it directly.</summary>
    public bool IsFree => FinalPrice <= 0m;
}

public record ApplyPromoCodeResult(PromoCodeQuote? Quote, PromoCodeRejection? Rejection)
{
    public bool Accepted => Quote is not null;
}

/// <summary>
/// Owner decision 2026-09-05. The one authority on promo codes: whether a code
/// exists, whether it may be used here and now by this student on this package,
/// and what it is worth.
///
/// <para><b>The browser never sends a price.</b> It sends six characters. Every
/// figure that reaches a subscription is computed in
/// <see cref="ApplyAsync"/> from the plan's own price and the code's own stored
/// percentage, which is why a discount cannot be enlarged, invented, or moved
/// onto another package by editing a form.</para>
/// </summary>
public interface IPromoCodeService
{
    /// <summary>A fresh, unused six-character code. Checked against the
    /// database so the admin is not shown one that is already taken; the unique
    /// index is still what makes a duplicate impossible.</summary>
    Task<string> GenerateUnusedCodeAsync(CancellationToken cancellationToken);

    /// <summary><paramref name="pricingPlanIds"/> empty means "every package".</summary>
    Task<CreatePromoCodeResult> CreateAsync(string code, int discountPercent, bool isActive,
        LocalDate? startsOn, LocalDate? endsOn, int? maxTotalUses, int? maxUsesPerStudent,
        bool appliesToAllPlans, IReadOnlyList<long> pricingPlanIds, long createdByUserId,
        CancellationToken cancellationToken);

    /// <summary>The code itself is never changed - it has been handed out.</summary>
    Task<UpdatePromoCodeOutcome> UpdateAsync(long promoCodeId, int discountPercent,
        LocalDate? startsOn, LocalDate? endsOn, int? maxTotalUses, int? maxUsesPerStudent,
        bool appliesToAllPlans, IReadOnlyList<long> pricingPlanIds, CancellationToken cancellationToken);

    Task<bool> SetActiveAsync(long promoCodeId, bool isActive, CancellationToken cancellationToken);

    /// <summary>Prices <paramref name="code"/> against a package for a student,
    /// or says exactly why it cannot be used. Read-only: this quotes, it never
    /// consumes a use.</summary>
    Task<ApplyPromoCodeResult> ApplyAsync(string? code, long pricingPlanId, long studentId,
        CancellationToken cancellationToken);

    /// <summary>How many subscriptions actually carry each code - counted from
    /// those rows, never from a stored tally.</summary>
    Task<IReadOnlyDictionary<long, int>> CountUsesAsync(IReadOnlyList<long> promoCodeIds,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<long, IReadOnlyList<long>>> GetPlanScopesAsync(IReadOnlyList<long> promoCodeIds,
        CancellationToken cancellationToken);
}
