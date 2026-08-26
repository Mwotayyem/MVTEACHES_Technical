using MVTeaches.Domain.Common;
using MVTeaches.Domain.Payroll;
using NodaTime;

namespace MVTeaches.Domain.Delivery;

public enum DeliveryState
{
    Pending,
    Declared,
    Verified,
    Rejected,
    Paid,
}

/// <summary>
/// Technical Study §17.3 (D-26). Answers ONE question only: "was the lesson
/// delivered, for the teacher's pay?" It has nothing to do with, and must never
/// be joined into, whether any given student's balance was consumed — that is
/// entirely decided by AttendanceRecord/EntitlementLedgerEntry via a student's
/// own Join press (D-83). Keep these two pipelines separate in every query.
///
/// Primary key is the session id itself (1:1 with ClassSession) — a session is
/// delivered at most once.
/// </summary>
public class SessionDelivery
{
    public long SessionId { get; private set; }
    public long TeacherId { get; private set; }

    public long? DeclaredByUserId { get; private set; }
    public Instant? DeclaredAtUtc { get; private set; }
    public int? DeclaredMinutes { get; private set; }
    public string? TeacherNote { get; private set; }

    public long? VerifiedByUserId { get; private set; }
    public Instant? VerifiedAtUtc { get; private set; }
    public int? VerifiedMinutes { get; private set; }
    public string? AdminNote { get; private set; }

    /// <summary>Snapshot at verification time (§9.2 golden rule) — never re-read
    /// from TeacherRates later when reconstructing historical payroll.</summary>
    public decimal? RateAmount { get; private set; }
    public string? RateCurrency { get; private set; }
    public long? RateSourceId { get; private set; }

    public decimal? PayableAmount { get; private set; }
    public long? PayrollPeriodId { get; private set; }

    public DeliveryState State { get; private set; } = DeliveryState.Pending;

    private SessionDelivery() { }

    public SessionDelivery(long sessionId, long teacherId)
    {
        SessionId = sessionId;
        TeacherId = teacherId;
    }

    /// <summary>Step 1 — teacher declares. §18.3 rule 3: whoever declares must not be the same
    /// person who verifies (enforced by the Application-layer authorization check, not here).</summary>
    public void Declare(long declaredByUserId, int declaredMinutes, string? note, Instant nowUtc)
    {
        if (State != DeliveryState.Pending)
        {
            throw new InvalidOperationException($"Cannot declare a delivery already in state {State}.");
        }

        if (declaredMinutes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(declaredMinutes));
        }

        DeclaredByUserId = declaredByUserId;
        DeclaredAtUtc = nowUtc;
        DeclaredMinutes = declaredMinutes;
        TeacherNote = note;
        State = DeliveryState.Declared;
    }

    public void MarkNotDelivered()
    {
        if (State != DeliveryState.Pending)
        {
            throw new InvalidOperationException($"Cannot mark not-delivered from state {State}.");
        }

        State = DeliveryState.Rejected;
    }

    /// <summary>Step 2 — admin verifies and the rate is snapshotted (§9.2/§18.1).
    /// Verified minutes are always the SCHEDULED duration, never a measured value (D-59/D-62) —
    /// the caller must pass the session's scheduled duration, not a Zoom-derived one.</summary>
    public void Verify(long verifiedByUserId, int verifiedMinutes, Money rate, RateUnit rateUnit, long rateSourceId, string? note, Instant nowUtc)
    {
        if (State != DeliveryState.Declared)
        {
            throw new InvalidOperationException($"Cannot verify a delivery in state {State}; it must be Declared first.");
        }

        if (verifiedByUserId == DeclaredByUserId)
        {
            throw new InvalidOperationException("The verifier must not be the same person who declared (§18.3 rule 3).");
        }

        if (verifiedMinutes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(verifiedMinutes));
        }

        VerifiedByUserId = verifiedByUserId;
        VerifiedAtUtc = nowUtc;
        VerifiedMinutes = verifiedMinutes;
        AdminNote = note;

        RateAmount = rate.Amount;
        RateCurrency = rate.Currency;
        RateSourceId = rateSourceId;
        // §9.2 (D-27): a PerHour rate scales with the scheduled duration; a
        // PerSession rate is a flat amount regardless of duration — treating
        // both the same way (as this method used to, always dividing by 60)
        // would silently overpay or underpay every PerSession-rated teacher.
        PayableAmount = rateUnit == RateUnit.PerSession
            ? Math.Round(rate.Amount, 3)
            : Math.Round(verifiedMinutes / 60m * rate.Amount, 3);

        State = DeliveryState.Verified;
    }

    public void Reject(string reason)
    {
        if (State != DeliveryState.Declared)
        {
            throw new InvalidOperationException($"Cannot reject a delivery in state {State}; it must be Declared first.");
        }

        AdminNote = reason;
        State = DeliveryState.Rejected;
    }

    public void AssignToPayrollPeriod(long payrollPeriodId)
    {
        if (State != DeliveryState.Verified)
        {
            throw new InvalidOperationException("Only a Verified delivery can be assigned to a payroll period.");
        }

        PayrollPeriodId = payrollPeriodId;
    }

    public void MarkPaid()
    {
        if (State != DeliveryState.Verified || PayrollPeriodId is null)
        {
            throw new InvalidOperationException("Only a Verified delivery already assigned to a payroll period can be marked Paid.");
        }

        State = DeliveryState.Paid;
    }
}
