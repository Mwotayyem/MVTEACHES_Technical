using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Common;
using MVTeaches.Domain.Payments;
using MVTeaches.Domain.Subscriptions;
using MVTeaches.Web.Display;
using NodaTime;
using Xunit;

namespace MVTeaches.Tests.Web;

/// <summary>
/// Owner decision 2026-09-04 — the testable core of the "safe MVP notes"
/// stage. Three of that stage's items are behaviour rather than markup and are
/// pinned here:
///
/// 1. The financial report's new read-only "student dues" section, which is
///    just <see cref="MoneyStanding.ComputeByCurrency"/> applied to every open
///    subscription at once. Testing the helper is testing the section: the page
///    adds no arithmetic of its own, deliberately, so that it can never quote a
///    family a different number than /Admin/Students does.
/// 2. The account-lockout window, shortened from 15 minutes to 2. A number in
///    Program.cs is exactly the kind of thing that gets "tidied" back later, so
///    the agreed value is asserted rather than trusted.
/// 3. Session times rendered in a viewer's own zone — the mechanism behind
///    showing a student their country's time rather than the centre's.
///
/// The remaining items in that stage (nav headings, the phone fields, the
/// compensation suggestion) are page markup driven by page state; they are
/// covered where their state is produced, not re-asserted as HTML here.
/// </summary>
public class MvpSafeStageTests : IClassFixture<AuthorizationTests.Factory>
{
    private readonly AuthorizationTests.Factory _factory;

    public MvpSafeStageTests(AuthorizationTests.Factory factory) => _factory = factory;

    private static readonly LocalDate AnyDate = new(2026, 1, 1);

    private static Subscription OpenSubscription(long studentId, decimal amount, string currency)
    {
        var subscription = new Subscription(studentId, countryId: 1, courseId: 1, levelId: 1,
            SessionType.Group, new Money(amount, currency), pricingPlanId: 1, sessionsCount: 10,
            minutesTotal: 600, AnyDate, validityDays: 90, SubscriptionOrigin.SelfPurchase,
            createdByUserId: 1, createdReason: null);
        return subscription;
    }

    private static Payment ConfirmedPayment(long studentId, long subscriptionId, decimal received, string currency)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        var payment = new Payment(studentId, subscriptionId, payerUserId: null,
            new Money(received, currency), PaymentMethod.BankTransfer,
            providerKey: "manual", referenceCode: $"REF-{studentId}-{subscriptionId}", now);
        // ReceivedAmount/ReceivedCurrency are what MoneyStanding actually adds
        // up — a payment confirmed without them counts as nothing, which is
        // exactly the behaviour these tests must exercise honestly.
        payment.Confirm(confirmedByUserId: 1, now, received, currency);
        return payment;
    }

    /// <summary>The ordinary reading an admin opens the report for: what was
    /// billed, what actually arrived, and what is still owed — per currency,
    /// never summed across them (D-53).</summary>
    [Fact]
    public void Student_dues_report_billed_paid_and_outstanding_per_currency()
    {
        var jodPaidInFull = OpenSubscription(studentId: 1, 50m, "JOD");
        var jodPaidShort = OpenSubscription(studentId: 2, 50m, "JOD");
        var usdUnpaid = OpenSubscription(studentId: 3, 100m, "USD");
        SetId(jodPaidInFull, 1);
        SetId(jodPaidShort, 2);
        SetId(usdUnpaid, 3);

        var payments = new[]
        {
            ConfirmedPayment(1, 1, 50m, "JOD"),
            ConfirmedPayment(2, 2, 40m, "JOD"), // the 40-of-50 shortfall case
        };

        var byCurrency = MoneyStanding.ComputeByCurrency(
            new[] { jodPaidInFull, jodPaidShort, usdUnpaid }, payments);

        Assert.Equal(2, byCurrency.Count);

        var jod = byCurrency["JOD"];
        Assert.Equal(100m, jod.Billed);
        Assert.Equal(90m, jod.Paid);
        Assert.Equal(10m, jod.Outstanding);

        // Never folded into the JOD row — the two currencies stay apart.
        var usd = byCurrency["USD"];
        Assert.Equal(100m, usd.Billed);
        Assert.Equal(0m, usd.Paid);
        Assert.Equal(100m, usd.Outstanding);
    }

    /// <summary>The trap this figure has to avoid: one family paying too much
    /// must never quietly cancel out another family's real debt. Outstanding is
    /// summed per subscription and clamped at zero each time, never computed as
    /// billed-minus-paid at the currency level.</summary>
    [Fact]
    public void One_students_overpayment_never_hides_another_students_debt()
    {
        var overpaid = OpenSubscription(studentId: 1, 50m, "JOD");
        var unpaid = OpenSubscription(studentId: 2, 50m, "JOD");
        SetId(overpaid, 11);
        SetId(unpaid, 12);

        var payments = new[] { ConfirmedPayment(1, 11, 70m, "JOD") };

        var jod = MoneyStanding.ComputeByCurrency(new[] { overpaid, unpaid }, payments)["JOD"];

        // Billed 100, paid 70 — a naive subtraction would report 30 owed and
        // silently forgive 20 of the second family's debt.
        Assert.Equal(100m, jod.Billed);
        Assert.Equal(70m, jod.Paid);
        Assert.Equal(50m, jod.Outstanding);
    }

    /// <summary>Owner decision 2026-09-04 (Review Required — Auth): the lockout
    /// window is two minutes, and the attempt count is deliberately unchanged
    /// at five. Both are asserted because the pair is the security trade-off,
    /// not either number alone.</summary>
    [Fact]
    public void Account_lockout_lasts_two_minutes_after_five_failed_attempts()
    {
        using var scope = _factory.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<IdentityOptions>>().Value;

        Assert.Equal(TimeSpan.FromMinutes(2), options.Lockout.DefaultLockoutTimeSpan);
        Assert.Equal(5, options.Lockout.MaxFailedAccessAttempts);
    }

    /// <summary>Owner decision 2026-09-04 (time zone by student country): the
    /// same stored instant reads as a different wall clock in two zones, which
    /// is the whole reason a student is shown their own country's time and the
    /// centre's time is labelled beside it when the two differ. Storage is
    /// untouched — one Instant goes in, and only the rendering differs.</summary>
    [Fact]
    public void A_session_reads_as_the_local_wall_clock_of_whichever_zone_it_is_shown_in()
    {
        // 09:00 UTC on a fixed date.
        var moment = Instant.FromUtc(2026, 6, 15, 9, 0);

        var amman = moment.InZone(DateTimeZoneProviders.Tzdb["Asia/Amman"]).LocalDateTime;
        var kuwait = moment.InZone(DateTimeZoneProviders.Tzdb["Asia/Kuwait"]).LocalDateTime;

        Assert.Equal(12, amman.Hour);  // UTC+3 in June
        Assert.Equal(12, kuwait.Hour); // UTC+3 all year
        Assert.NotEqual(
            moment.InZone(DateTimeZoneProviders.Tzdb["Asia/Amman"]).LocalDateTime.Hour,
            moment.InZone(DateTimeZoneProviders.Tzdb["Europe/London"]).LocalDateTime.Hour);

        // And the label a viewer actually sees is the city, not the IANA id.
        Assert.Equal("Amman", DisplayFormat.ZoneLabel("Asia/Amman"));
        Assert.Equal("London", DisplayFormat.ZoneLabel("Europe/London"));
        // A missing zone must never silently masquerade as local time.
        Assert.Equal("UTC", DisplayFormat.ZoneLabel(null));
    }

    /// <summary>Subscription ids are database-generated; these tests never touch
    /// the database, so the payment-to-subscription link is set directly. Doing
    /// it here keeps that reflection in one place rather than in each test.</summary>
    private static void SetId(Subscription subscription, long id) =>
        typeof(Subscription).GetProperty(nameof(Subscription.Id))!
            .SetValue(subscription, id);
}
