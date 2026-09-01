using MVTeaches.Domain.Payments;
using MVTeaches.Domain.Subscriptions;

namespace MVTeaches.Web.Display;

/// <summary>Billed, paid, and outstanding for one currency's worth of a
/// student's CURRENT (Draft or Active) packages. A closed package — Expired
/// or Cancelled — contributes nothing here, on either side: it is done, and
/// showing its old payment as "still counting" toward today's balance is
/// exactly the confusion this type exists to prevent.</summary>
public readonly record struct MoneyStandingFacts(decimal Billed, decimal Paid, decimal Outstanding)
{
    public bool IsSettled => Outstanding <= 0m;

    public int PaidPercent => Billed <= 0m ? 100
        : (int)Math.Round(Math.Clamp((double)(Paid / Billed) * 100d, 0d, 100d));
}

/// <summary>
/// The single source of truth for "how does this student's (or the school's)
/// money currently stand", reused everywhere that question is asked: the
/// student profile, the register cards, the roster modal, the dashboard.
///
/// This exists because of a real mistake, not a hypothetical one. The first
/// version of these screens summed EVERY confirmed payment in a currency as
/// "Paid", but only summed Draft/Active subscriptions as "Billed" — so a
/// student with one old, fully-paid, since-Expired package and one new
/// small unpaid one showed as "Paid 140 / Billed 50", which is not merely
/// unclear, it is nonsensical (paid more than was ever billed). The already-
/// existing <c>IPaymentService.GetSubscriptionFundingStatusAsync</c> (used on
/// /Admin/Payments) never had this bug: it always scopes a payment to the ONE
/// subscription it was actually recorded against. This type applies that
/// same scoping rule wherever a bulk read makes calling that service
/// per-subscription too expensive (a register of 200 students), so the two
/// paths can never drift into disagreeing again.
/// </summary>
public static class MoneyStanding
{
    /// <summary>Groups by currency across whatever subscriptions/payments are
    /// passed in — one student's own, or the whole school's at once. Only
    /// Draft/Active subscriptions are billed; a payment counts toward a
    /// subscription only when it is actually recorded against that exact
    /// subscription id, confirmed, and in that subscription's own currency —
    /// the same three conditions <c>GetSubscriptionFundingStatusAsync</c>
    /// checks. A payment with no subscription id (recorded as "not against a
    /// package"), or tied to a Cancelled/Expired one, is currency history, not
    /// current standing, and does not appear here.</summary>
    public static IReadOnlyDictionary<string, MoneyStandingFacts> ComputeByCurrency(
        IReadOnlyCollection<Subscription> subscriptions, IReadOnlyCollection<Payment> payments)
    {
        var openSubs = subscriptions.Where(s => s.Status is SubscriptionStatus.Draft or SubscriptionStatus.Active).ToList();
        if (openSubs.Count == 0)
        {
            return new Dictionary<string, MoneyStandingFacts>();
        }

        var openSubIds = openSubs.Select(s => s.Id).ToHashSet();
        var confirmedBySubscription = payments
            .Where(p => p.Status == PaymentStatus.Confirmed && p.SubscriptionId is not null
                        && openSubIds.Contains(p.SubscriptionId.Value))
            .GroupBy(p => p.SubscriptionId!.Value)
            .ToDictionary(g => g.Key, g => g
                // A confirmed payment in a different currency than its own
                // subscription contributes nothing — D-53, no automatic FX,
                // ever. This mirrors GetSubscriptionFundingStatusAsync's own
                // currency guard exactly.
                .Where(p => p.ReceivedCurrency == openSubs.First(s => s.Id == g.Key).Price.Currency)
                .Sum(p => p.ReceivedAmount ?? 0m));

        return openSubs
            .GroupBy(s => s.Price.Currency)
            .ToDictionary(
                g => g.Key,
                g => new MoneyStandingFacts(
                    Billed: g.Sum(s => s.Price.Amount),
                    Paid: g.Sum(s => confirmedBySubscription.GetValueOrDefault(s.Id)),
                    // Summed per-subscription and clamped at zero PER
                    // subscription, then added up — never Billed minus Paid
                    // at the currency level. That subtraction would let one
                    // subscription's overpayment silently cancel out a
                    // different subscription's real shortfall.
                    Outstanding: g.Sum(s => Math.Max(0m, s.Price.Amount - confirmedBySubscription.GetValueOrDefault(s.Id)))));
    }

    /// <summary>The one row a compact card (the register, the roster modal)
    /// actually shows: the currency of the RUNNING package if there is one,
    /// else the most recently created open package, else nothing to show at
    /// all — a student with no open package has no "current standing".</summary>
    public static (string? Currency, MoneyStandingFacts Facts) ComputePrimary(
        IReadOnlyCollection<Subscription> subscriptions, IReadOnlyCollection<Payment> payments)
    {
        var byCurrency = ComputeByCurrency(subscriptions, payments);
        if (byCurrency.Count == 0)
        {
            return (null, default);
        }

        var openSubs = subscriptions.Where(s => s.Status is SubscriptionStatus.Draft or SubscriptionStatus.Active).ToList();
        var currency = openSubs.FirstOrDefault(s => s.Status == SubscriptionStatus.Active)?.Price.Currency
                       ?? openSubs.OrderByDescending(s => s.Id).First().Price.Currency;
        return (currency, byCurrency.GetValueOrDefault(currency));
    }
}
