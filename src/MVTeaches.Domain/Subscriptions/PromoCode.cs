using NodaTime;

namespace MVTeaches.Domain.Subscriptions;

/// <summary>
/// Owner decision 2026-09-05: a discount code the centre hands out, applied by
/// the family at purchase time.
///
/// <para>Everything a code decides lives here and is read from here — the
/// percentage above all. The browser only ever sends the six characters; the
/// price is computed server-side from this row, so a discount cannot be
/// invented, enlarged, or applied to a package it was never meant for by
/// editing a form.</para>
///
/// <para>The code itself is <b>generated</b>, not typed: six characters from
/// A–Z and 0–9, stored uppercase, unique. Uniqueness has a real unique index
/// behind it (see PromoCodeConfiguration) rather than only a pre-save check,
/// because two admins pressing "generate" at the same moment is exactly the
/// case a pre-save check cannot see.</para>
/// </summary>
public class PromoCode
{
    /// <summary>The alphabet a generated code is drawn from. Digits and
    /// capitals only: no lowercase (everything is stored and compared
    /// uppercase), no punctuation, no Arabic — a code is read aloud down a
    /// phone and typed by a parent, so it has to survive that trip.</summary>
    public const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    public const int CodeLength = 6;

    public long Id { get; private set; }

    /// <summary>Always uppercase, always <see cref="CodeLength"/> characters
    /// drawn from <see cref="Alphabet"/>. Normalised by the constructor, so
    /// "a7k2p9" and "A7K2P9" are the same code and only one of them can
    /// exist.</summary>
    public string Code { get; private set; } = string.Empty;

    /// <summary>1–100. Zero is not a discount and is refused rather than
    /// stored as a code that quietly does nothing; 100 is a legitimate,
    /// deliberate "this package is free" (see the purchase path).</summary>
    public int DiscountPercent { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>Null means "no start date" — usable from the moment it is
    /// created. Compared as a date in the same way every other date on this
    /// project is; no new timezone rule is introduced here.</summary>
    public LocalDate? StartsOn { get; private set; }

    /// <summary>Null means "never expires".</summary>
    public LocalDate? EndsOn { get; private set; }

    /// <summary>Null means unlimited. Counted from the subscriptions that
    /// actually recorded this code, never from a stored tally that could
    /// drift away from them.</summary>
    public int? MaxTotalUses { get; private set; }

    /// <summary>Null means unlimited per student.</summary>
    public int? MaxUsesPerStudent { get; private set; }

    public long CreatedByUserId { get; private set; }
    public Instant CreatedAtUtc { get; private set; }
    public Instant? UpdatedAtUtc { get; private set; }

    private PromoCode() { }

    public PromoCode(string code, int discountPercent, bool isActive, LocalDate? startsOn, LocalDate? endsOn,
        int? maxTotalUses, int? maxUsesPerStudent, long createdByUserId, Instant createdAtUtc)
    {
        Code = NormaliseCode(code);
        DiscountPercent = ValidatePercent(discountPercent);
        ValidateWindow(startsOn, endsOn);
        ValidateLimit(maxTotalUses, nameof(maxTotalUses));
        ValidateLimit(maxUsesPerStudent, nameof(maxUsesPerStudent));

        IsActive = isActive;
        StartsOn = startsOn;
        EndsOn = endsOn;
        MaxTotalUses = maxTotalUses;
        MaxUsesPerStudent = maxUsesPerStudent;
        CreatedByUserId = createdByUserId;
        CreatedAtUtc = createdAtUtc;
    }

    /// <summary>Everything an admin may change later. The CODE itself is not
    /// among them: it has been handed out, and rewriting it would silently
    /// change what a family already holds.</summary>
    public void Update(int discountPercent, LocalDate? startsOn, LocalDate? endsOn,
        int? maxTotalUses, int? maxUsesPerStudent, Instant nowUtc)
    {
        DiscountPercent = ValidatePercent(discountPercent);
        ValidateWindow(startsOn, endsOn);
        ValidateLimit(maxTotalUses, nameof(maxTotalUses));
        ValidateLimit(maxUsesPerStudent, nameof(maxUsesPerStudent));

        StartsOn = startsOn;
        EndsOn = endsOn;
        MaxTotalUses = maxTotalUses;
        MaxUsesPerStudent = maxUsesPerStudent;
        UpdatedAtUtc = nowUtc;
    }

    public void SetActive(bool isActive, Instant nowUtc)
    {
        IsActive = isActive;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Uppercased and trimmed, then checked. Accepting "a7k2p9" and
    /// storing "A7K2P9" is deliberate — the family types what they were told,
    /// in whatever case they type it — but anything that is not exactly six
    /// characters of <see cref="Alphabet"/> is refused outright rather than
    /// stripped into something that happens to fit.</summary>
    public static string NormaliseCode(string? code)
    {
        var normalised = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (!IsWellFormed(normalised))
        {
            throw new ArgumentException(
                $"A promo code must be exactly {CodeLength} characters, using A-Z and 0-9 only.", nameof(code));
        }

        return normalised;
    }

    /// <summary>The same rule as <see cref="NormaliseCode"/>, as a question
    /// rather than an exception — for the paths that must answer a family
    /// with a message instead of throwing.</summary>
    public static bool IsWellFormed(string? code)
    {
        if (code is null || code.Length != CodeLength)
        {
            return false;
        }

        foreach (var character in code)
        {
            if (Alphabet.IndexOf(character) < 0)
            {
                return false;
            }
        }

        return true;
    }

    private static int ValidatePercent(int percent) => percent is >= 1 and <= 100
        ? percent
        : throw new ArgumentOutOfRangeException(nameof(percent),
            "A discount must be between 1 and 100 percent.");

    private static void ValidateWindow(LocalDate? startsOn, LocalDate? endsOn)
    {
        if (startsOn is not null && endsOn is not null && endsOn < startsOn)
        {
            throw new ArgumentException("A promo code cannot end before it starts.", nameof(endsOn));
        }
    }

    private static void ValidateLimit(int? limit, string name)
    {
        if (limit is not null && limit < 1)
        {
            throw new ArgumentOutOfRangeException(name, "A usage limit must be at least 1, or empty for unlimited.");
        }
    }

    /// <summary>Whether this code is usable on <paramref name="on"/> — active,
    /// and inside its own window. Says nothing about scope or limits, which
    /// need the database; see IPromoCodeService.</summary>
    public bool IsOpenOn(LocalDate on) =>
        IsActive && (StartsOn is null || on >= StartsOn) && (EndsOn is null || on <= EndsOn);

    /// <summary>What a discount takes off a price, rounded to the currency's
    /// own three decimal places (JOD) the way every other amount on this
    /// project is stored. Rounded HALF UP so the customer is never charged a
    /// fraction more than the percentage promised, and computed on the
    /// discount rather than the remainder so the two figures shown on screen
    /// always add back up to the original.</summary>
    public decimal DiscountOn(decimal listPrice) =>
        Math.Round(listPrice * DiscountPercent / 100m, 3, MidpointRounding.AwayFromZero);

    public decimal FinalPriceFrom(decimal listPrice) => listPrice - DiscountOn(listPrice);
}

/// <summary>
/// Which packages a code applies to. No rows at all for a code means "every
/// package" — deliberately the absence of restriction rather than a flag plus
/// rows that could disagree with each other.
/// </summary>
public class PromoCodePlan
{
    public long Id { get; private set; }
    public long PromoCodeId { get; private set; }
    public long PricingPlanId { get; private set; }

    private PromoCodePlan() { }

    public PromoCodePlan(long promoCodeId, long pricingPlanId)
    {
        PromoCodeId = promoCodeId;
        PricingPlanId = pricingPlanId;
    }
}
