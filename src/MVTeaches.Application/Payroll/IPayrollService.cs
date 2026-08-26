using NodaTime;

namespace MVTeaches.Application.Payroll;

public enum DeclareDeliveryOutcome
{
    Declared,
    SessionNotFound,

    /// <summary>§18.3 rule 6: a NotDelivered session never enters the payroll
    /// pipeline at all — there is nothing to declare.</summary>
    SessionNotDelivered,
    AlreadyDeclared,
}

public record DeclareDeliveryResult(DeclareDeliveryOutcome Outcome);

public enum VerifyDeliveryOutcome
{
    Verified,
    DeliveryNotFound,
    NotDeclared,

    /// <summary>§18.3 rule 3: whoever declares must not be the same person who verifies.</summary>
    SameActorAsDeclarer,

    /// <summary>No applicable TeacherRate exists for this teacher/course/level/age-group
    /// as of the session's date — the admin must create one before this delivery can be verified.</summary>
    NoApplicableRate,
}

public record VerifyDeliveryResult(VerifyDeliveryOutcome Outcome);

public enum RejectDeliveryOutcome
{
    Rejected,
    DeliveryNotFound,
    NotDeclared,
    SameActorAsDeclarer,
}

public record RejectDeliveryResult(RejectDeliveryOutcome Outcome);

public record OpenPayrollPeriodResult(long PeriodId);

/// <summary>
/// Technical Study §18.1/§18.2 (D-26) — the full declare → verify → aggregate →
/// review → approve → pay cycle. Each method is a thin orchestration layer
/// over the domain state machines already enforced by SessionDelivery and
/// PayrollPeriod; this service's own job is exactly the parts those entities
/// cannot do alone: looking up the right rate (§9.2), enforcing separation of
/// duties across two different calls (§18.3 rule 3), and the cross-entity
/// aggregation query behind step [4].
/// </summary>
public interface IPayrollService
{
    /// <summary>Step [1] — the teacher declares after the session. Creates the
    /// SessionDelivery row on first declaration for a session (lazily — there
    /// is no separate "provision a delivery row" step in the documented cycle).</summary>
    Task<DeclareDeliveryResult> DeclareAsync(long sessionId, long declaredByUserId, int declaredMinutes, string? note, CancellationToken cancellationToken);

    /// <summary>Step [2] — the admin verifies. Verified minutes are ALWAYS the
    /// session's own scheduled duration (D-59/D-62) — never taken from the
    /// teacher's declaration or any caller-supplied value.</summary>
    Task<VerifyDeliveryResult> VerifyAsync(long sessionId, long verifiedByUserId, string? note, CancellationToken cancellationToken);

    Task<RejectDeliveryResult> RejectAsync(long sessionId, long rejectedByUserId, string reason, CancellationToken cancellationToken);

    /// <summary>Step [3] — opens a new payroll period for a country.</summary>
    Task<OpenPayrollPeriodResult> OpenPeriodAsync(int countryId, LocalDate periodStart, LocalDate periodEnd, CancellationToken cancellationToken);

    /// <summary>Step [4] — pulls every not-yet-assigned Verified delivery whose
    /// session falls within the period's country and date range into a
    /// PayrollLine. Safe to call repeatedly on an Open period as more
    /// deliveries get verified during the month; a delivery already assigned
    /// to a period is never picked up again. Returns the number of lines created.</summary>
    Task<int> AggregateVerifiedDeliveriesAsync(long periodId, CancellationToken cancellationToken);

    /// <summary>Freezes the period for step [5]'s admin review — no further
    /// aggregation happens against a period once it leaves Open.</summary>
    Task MoveToReviewAsync(long periodId, CancellationToken cancellationToken);

    /// <summary>Step [6] — locks the period. §18.3 rule 1: an approved period
    /// is never edited retroactively; a correction is a settlement entry in
    /// the NEXT period.</summary>
    Task ApprovePeriodAsync(long periodId, long approvedByUserId, CancellationToken cancellationToken);

    /// <summary>Step [7] — records the actual payout. Marks the period AND
    /// every SessionDelivery it aggregated as Paid together.</summary>
    Task MarkPeriodPaidAsync(long periodId, CancellationToken cancellationToken);

    Task ClosePeriodAsync(long periodId, CancellationToken cancellationToken);
}
