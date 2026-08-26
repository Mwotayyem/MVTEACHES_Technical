using NodaTime;

namespace MVTeaches.Domain.Subscriptions;

/// <summary>
/// Technical Study §19.3 (D-54): up to 3 times/month with a reason, stops the
/// expiry countdown. Distinct from an Extension (D-18) — a freeze does not
/// count against the one-extension limit.
/// </summary>
public class SubscriptionFreeze
{
    public long Id { get; private set; }

    public long SubscriptionId { get; private set; }
    public LocalDate StartsOn { get; private set; }
    public LocalDate? EndsOn { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public long ApprovedByUserId { get; private set; }

    private SubscriptionFreeze() { }

    public SubscriptionFreeze(long subscriptionId, LocalDate startsOn, string reason, long approvedByUserId)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A freeze requires a reason.", nameof(reason));
        }

        SubscriptionId = subscriptionId;
        StartsOn = startsOn;
        Reason = reason;
        ApprovedByUserId = approvedByUserId;
    }

    public void Lift(LocalDate endsOn)
    {
        if (endsOn < StartsOn)
        {
            throw new ArgumentOutOfRangeException(nameof(endsOn));
        }

        EndsOn = endsOn;
    }

    public int DaysSoFar(LocalDate asOf)
    {
        var end = EndsOn ?? asOf;
        return end < StartsOn ? 0 : Period.DaysBetween(StartsOn, end);
    }
}
