namespace MVTeaches.Domain.Catalog;

/// <summary>
/// Technical Study §24.1 (D-06→D-09, extended by D-53). "Branch" in the original
/// SRS resolves to Country/Market — there is no separate Branch entity (README §0).
/// Not limited to two rows: any country beyond Jordan/Palestine falls back to the
/// <see cref="IsDefaultIntl"/> USD row.
/// </summary>
public class Country
{
    public int Id { get; private set; }

    /// <summary>ISO 3166-1 alpha-2 (JO, PS, ...).</summary>
    public string Code { get; private set; } = string.Empty;

    public string NameAr { get; private set; } = string.Empty;
    public string NameEn { get; private set; } = string.Empty;

    /// <summary>ISO 4217 (JOD, ILS, USD, ...).</summary>
    public string CurrencyCode { get; private set; } = string.Empty;

    /// <summary>E.164 calling code, e.g. "+962".</summary>
    public string PhoneCountryCode { get; private set; } = string.Empty;

    /// <summary>IANA id, e.g. "Asia/Amman".</summary>
    public string DefaultTimeZone { get; private set; } = string.Empty;

    /// <summary>D-39: the only implementation in MVP is "manual" (bank transfer/CliQ).</summary>
    public string PaymentProviderKey { get; private set; } = "manual";

    /// <summary>D-53: the single "rest of the world" USD row.</summary>
    public bool IsDefaultIntl { get; private set; }

    public bool IsActive { get; private set; } = true;

    private Country() { }

    public Country(int id, string code, string nameAr, string nameEn, string currencyCode,
        string phoneCountryCode, string defaultTimeZone, bool isDefaultIntl = false)
    {
        Id = id;
        Code = code;
        NameAr = nameAr;
        NameEn = nameEn;
        CurrencyCode = currencyCode;
        PhoneCountryCode = phoneCountryCode;
        DefaultTimeZone = defaultTimeZone;
        IsDefaultIntl = isDefaultIntl;
        PaymentProviderKey = "manual";
    }
}
