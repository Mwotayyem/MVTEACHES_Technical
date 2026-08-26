using NodaTime;

namespace MVTeaches.Domain.Payroll;

public enum PayrollPeriodStatus
{
    Open,
    Review,
    Approved,
    Paid,
    Closed,
}

/// <summary>Technical Study §18.2 (D-26). §18.3 rule 1: an approved period is
/// locked — corrections happen via a settlement entry in the NEXT period, never
/// a retroactive edit.</summary>
public class PayrollPeriod
{
    public long Id { get; private set; }
    public int CountryId { get; private set; }

    public DateOnly PeriodStart { get; private set; }
    public DateOnly PeriodEnd { get; private set; }

    public PayrollPeriodStatus Status { get; private set; } = PayrollPeriodStatus.Open;
    public long? ApprovedByUserId { get; private set; }
    public Instant? ApprovedAtUtc { get; private set; }

    private readonly List<PayrollLine> _lines = new();
    public IReadOnlyCollection<PayrollLine> Lines => _lines;

    private PayrollPeriod() { }

    public PayrollPeriod(int countryId, DateOnly periodStart, DateOnly periodEnd)
    {
        if (periodEnd <= periodStart)
        {
            throw new ArgumentException("Period end must be after period start.");
        }

        CountryId = countryId;
        PeriodStart = periodStart;
        PeriodEnd = periodEnd;
    }

    public void MoveToReview()
    {
        if (Status != PayrollPeriodStatus.Open)
        {
            throw new InvalidOperationException($"Cannot move a {Status} period to review.");
        }

        Status = PayrollPeriodStatus.Review;
    }

    public void Approve(long approvedByUserId, Instant nowUtc)
    {
        if (Status != PayrollPeriodStatus.Review)
        {
            throw new InvalidOperationException("A period must be under Review before it can be approved.");
        }

        Status = PayrollPeriodStatus.Approved;
        ApprovedByUserId = approvedByUserId;
        ApprovedAtUtc = nowUtc;
    }

    public void MarkPaid()
    {
        if (Status != PayrollPeriodStatus.Approved)
        {
            throw new InvalidOperationException("Only an Approved period can be marked Paid.");
        }

        Status = PayrollPeriodStatus.Paid;
    }

    public void Close()
    {
        if (Status != PayrollPeriodStatus.Paid)
        {
            throw new InvalidOperationException("Only a Paid period can be closed.");
        }

        Status = PayrollPeriodStatus.Closed;
    }

    public bool IsLocked => Status is PayrollPeriodStatus.Approved or PayrollPeriodStatus.Paid or PayrollPeriodStatus.Closed;
}

/// <summary>Technical Study §18.2. UNIQUE(PeriodId, SessionId) is the constraint
/// that makes double-paying a session impossible even if aggregation reruns —
/// enforced at the database level in Infrastructure, mirrored here as an invariant.</summary>
public class PayrollLine
{
    public long Id { get; private set; }

    public long PeriodId { get; private set; }
    public long TeacherId { get; private set; }
    public long SessionId { get; private set; }

    public int Minutes { get; private set; }
    public decimal RateAmount { get; private set; }
    public string RateCurrency { get; private set; } = string.Empty;
    public decimal Amount { get; private set; }

    private PayrollLine() { }

    public PayrollLine(long periodId, long teacherId, long sessionId, int minutes, decimal rateAmount, string rateCurrency, decimal amount)
    {
        PeriodId = periodId;
        TeacherId = teacherId;
        SessionId = sessionId;
        Minutes = minutes;
        RateAmount = rateAmount;
        RateCurrency = rateCurrency;
        Amount = amount;
    }
}
