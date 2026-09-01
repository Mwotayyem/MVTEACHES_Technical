using System.Globalization;
using Microsoft.Extensions.Localization;
using MVTeaches.Web.Resources;
using NodaTime;

namespace MVTeaches.Web.Display;

/// <summary>
/// Presentation-only formatting shared by every Razor page. It exists because
/// the same three display defects kept appearing screen by screen:
///
///   1. ISO strings shown to end users ("2026-08-30", "2026-08-30T14:00:00Z").
///   2. An unset date rendering as "01/01/0001" (or the Unix epoch for an
///      <see cref="Instant"/>) instead of saying plainly that it is not set.
///   3. Money printed with InvariantCulture and a bare currency code.
///
/// Written as extension methods on the shared localizer so a view can call
/// <c>@T.Date(x)</c> with no new dependency, no service registration, and no
/// change to Program.cs. Nothing here reads or writes data, and no business
/// rule is expressed here — a value that is absent is reported as absent, it
/// is never substituted, defaulted, or rounded into something else.
/// </summary>
public static class DisplayFormat
{
    /// <summary>ISO 4217 codes whose minor unit is 3 digits (JOD, KWD, ...).
    /// Everything else is formatted with 2, which is the ISO default.</summary>
    private static readonly HashSet<string> ThreeDecimalCurrencies =
        new(StringComparer.OrdinalIgnoreCase) { "JOD", "KWD", "BHD", "OMR", "TND", "IQD", "LYD" };

    private const string DateFormat = "d MMMM yyyy";
    private const string DayAndDateFormat = "dddd d MMMM yyyy";
    private const string TimeFormat = "h:mm tt";

    /// <summary>The single wording used everywhere a value is genuinely absent —
    /// so a missing date never reaches a user as "01/01/0001" again.</summary>
    public static string NotSpecified(this IStringLocalizer<SharedResource> t) => t["Not specified"].Value;

    public static string Date(this IStringLocalizer<SharedResource> t, LocalDate date) =>
        IsUnset(date) ? t.NotSpecified() : date.ToDateTimeUnspecified().ToString(DateFormat, Culture);

    public static string Date(this IStringLocalizer<SharedResource> t, LocalDate? date) =>
        date is null ? t.NotSpecified() : t.Date(date.Value);

    public static string Date(this IStringLocalizer<SharedResource> t, DateOnly? date) =>
        date is null || date.Value.Year <= 1
            ? t.NotSpecified()
            : date.Value.ToDateTime(TimeOnly.MinValue).ToString(DateFormat, Culture);

    /// <summary>A stored instant shown as a plain UTC date — used for record
    /// timestamps (issued on, effective from) where no local zone is stored.</summary>
    public static string DateUtc(this IStringLocalizer<SharedResource> t, Instant instant) =>
        IsUnset(instant) ? t.NotSpecified() : instant.InUtc().Date.ToDateTimeUnspecified().ToString(DateFormat, Culture);

    /// <summary>A stored instant shown as date + time, explicitly labelled UTC
    /// so it can never be mistaken for a local wall-clock time.</summary>
    public static string DateTimeUtc(this IStringLocalizer<SharedResource> t, Instant instant)
    {
        if (IsUnset(instant))
        {
            return t.NotSpecified();
        }

        var local = instant.InUtc().LocalDateTime.ToDateTimeUnspecified();
        return $"{local.ToString(DateFormat, Culture)} · {local.ToString(TimeFormat, Culture)} UTC";
    }

    public static string DateTimeUtc(this IStringLocalizer<SharedResource> t, Instant? instant) =>
        instant is null ? t.NotSpecified() : t.DateTimeUtc(instant.Value);

    /// <summary>A class session's moment, rendered in the session's OWN stored
    /// schedule time zone (<c>ClassSession.ScheduleTimeZone</c>) — the wall-clock
    /// time the teacher and student actually agreed on, not UTC.</summary>
    public static string SessionMoment(this IStringLocalizer<SharedResource> t, Instant instant, string? timeZoneId)
    {
        if (IsUnset(instant))
        {
            return t.NotSpecified();
        }

        var zone = ResolveZone(timeZoneId);
        var local = instant.InZone(zone).LocalDateTime.ToDateTimeUnspecified();
        return $"{local.ToString(DayAndDateFormat, Culture)} · {local.ToString(TimeFormat, Culture)}";
    }

    public static string SessionMoment(this IStringLocalizer<SharedResource> t, Instant? instant, string? timeZoneId) =>
        instant is null ? t.NotSpecified() : t.SessionMoment(instant.Value, timeZoneId);

    /// <summary>Short session label for a &lt;select&gt; option: date, time, and
    /// zone city — never the internal session id.</summary>
    public static string SessionOption(this IStringLocalizer<SharedResource> t, Instant instant, string? timeZoneId)
    {
        if (IsUnset(instant))
        {
            return t.NotSpecified();
        }

        var zone = ResolveZone(timeZoneId);
        var local = instant.InZone(zone).LocalDateTime.ToDateTimeUnspecified();
        return $"{local.ToString(DateFormat, Culture)} · {local.ToString(TimeFormat, Culture)} ({ZoneLabel(timeZoneId)})";
    }

    /// <summary>"Asia/Amman" → "Amman" — the part a human reads, without the
    /// IANA prefix that means nothing to an admin or a parent.</summary>
    public static string ZoneLabel(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return "UTC";
        }

        var lastSegment = timeZoneId.Split('/').Last();
        return lastSegment.Replace('_', ' ');
    }

    public static string Money(this IStringLocalizer<SharedResource> t, decimal amount, string? currency)
    {
        var code = string.IsNullOrWhiteSpace(currency) ? string.Empty : currency.Trim().ToUpperInvariant();
        var decimals = ThreeDecimalCurrencies.Contains(code) ? 3 : 2;
        var formatted = amount.ToString("N" + decimals, Culture);
        return code.Length == 0 ? formatted : $"{formatted} {code}";
    }

    public static string Money(this IStringLocalizer<SharedResource> t, decimal? amount, string? currency) =>
        amount is null ? t.NotSpecified() : t.Money(amount.Value, currency);

    /// <summary>Minutes as a human duration ("90" → "1 h 30 min"), because the
    /// package/ledger tables all store minutes but nobody reads 600 as 10 hours.</summary>
    public static string Minutes(this IStringLocalizer<SharedResource> t, int minutes)
    {
        if (minutes <= 0)
        {
            return $"0 {t["Duration minutes short"].Value}";
        }

        var hours = minutes / 60;
        var remainder = minutes % 60;
        if (hours == 0)
        {
            return $"{minutes} {t["Duration minutes short"].Value}";
        }

        return remainder == 0
            ? $"{hours} {t["Duration hours short"].Value}"
            : $"{hours} {t["Duration hours short"].Value} {remainder} {t["Duration minutes short"].Value}";
    }

    private static CultureInfo Culture => CultureInfo.CurrentUICulture;

    private static bool IsUnset(LocalDate date) => date.Year <= 1;

    /// <summary>An instant is treated as unset when it is the CLR default
    /// (the Unix epoch, which is what an unresolved lookup leaves behind) or
    /// NodaTime's own minimum — neither is a real value in this domain.</summary>
    private static bool IsUnset(Instant instant) => instant == default || instant == Instant.MinValue;

    private static DateTimeZone ResolveZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return DateTimeZone.Utc;
        }

        return DateTimeZoneProviders.Tzdb.GetZoneOrNull(timeZoneId) ?? DateTimeZone.Utc;
    }
}
