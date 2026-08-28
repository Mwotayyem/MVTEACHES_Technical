using NodaTime;

namespace MVTeaches.Domain.Ledger;

/// <summary>
/// Technical Study §20.2 — "the single most important table in the project."
/// Append-only (§20.5 rule 1): there is no Update/Delete method on this class
/// on purpose, and Infrastructure must enforce it at the database level too
/// (revoked UPDATE/DELETE privileges and/or a trigger), not rely on this class
/// alone. A remaining balance is ALWAYS <c>SUM(delta_minutes)</c> computed at
/// read time — never a stored counter (D-36). See IEntitlementBalanceQuery.
///
/// The critical invariant this entry protects, together with a partial unique
/// index on (SessionId, StudentId) WHERE Reason = Consumption
/// (ux_ent_consumption in Infrastructure): the SAME session can never consume
/// the SAME student's balance twice, no matter how many times Join is pressed
/// or how many concurrent requests race for it.
/// </summary>
public class EntitlementLedgerEntry
{
    public long Id { get; private set; }

    public long StudentId { get; private set; }
    public long? SubscriptionId { get; private set; }
    public long CourseId { get; private set; }
    public int LevelId { get; private set; }

    /// <summary>The atomic unit is the minute (D-37), never the "session" or "hour" as
    /// an integer — see §20.3 for why a session-count unit breaks the moment two
    /// sessions have different durations.</summary>
    public int DeltaMinutes { get; private set; }

    public LedgerReason Reason { get; private set; }

    public long? SessionId { get; private set; }
    public long? PaymentId { get; private set; }
    public long? MigrationRecordId { get; private set; }

    /// <summary>Points at the entry this one corrects (§20.5 rule 2).</summary>
    public long? ReversesEntryId { get; private set; }

    /// <summary>NULL = the system itself (e.g. an expiry sweep).</summary>
    public long? PerformedByUserId { get; private set; }

    /// <summary>Mandatory when Reason == AdminAdjustment (enforced by the
    /// Application-layer service, not by a nullable check alone — see
    /// LedgerService.RecordAdminAdjustment).</summary>
    public string? Note { get; private set; }

    /// <summary>Only meaningful for MakeUpGranted — the admin-set expiry (D-63).
    /// No hardcoded 30-day default anywhere (D-65).</summary>
    public LocalDate? ExpiresOn { get; private set; }

    public Instant CreatedAtUtc { get; private set; }

    private EntitlementLedgerEntry() { }

    private EntitlementLedgerEntry(long studentId, long? subscriptionId, long courseId, int levelId,
        int deltaMinutes, LedgerReason reason, long? sessionId, long? paymentId, long? migrationRecordId,
        long? reversesEntryId, long? performedByUserId, string? note, LocalDate? expiresOn, Instant createdAtUtc)
    {
        if (deltaMinutes == 0)
        {
            throw new ArgumentException("A ledger entry with a zero delta is meaningless (§33.3 CHECK).", nameof(deltaMinutes));
        }

        if (reason == LedgerReason.AdminAdjustment && string.IsNullOrWhiteSpace(note))
        {
            throw new ArgumentException("A note is mandatory for AdminAdjustment entries (§20.5 rule 3).", nameof(note));
        }

        StudentId = studentId;
        SubscriptionId = subscriptionId;
        CourseId = courseId;
        LevelId = levelId;
        DeltaMinutes = deltaMinutes;
        Reason = reason;
        SessionId = sessionId;
        PaymentId = paymentId;
        MigrationRecordId = migrationRecordId;
        ReversesEntryId = reversesEntryId;
        PerformedByUserId = performedByUserId;
        Note = note;
        ExpiresOn = expiresOn;
        CreatedAtUtc = createdAtUtc;
    }

    /// <summary>The one and only debit path (D-83): a Join press, OR — per the
    /// owner's self-service-booking correction — the automatic no-show
    /// finalization of a session nobody joined (SessionFinalizationService).
    /// <paramref name="performedByUserId"/> is null for the automatic
    /// no-show case, matching this class's own "NULL = the system itself"
    /// convention.</summary>
    public static EntitlementLedgerEntry ForConsumption(long studentId, long subscriptionId, long courseId,
        int levelId, int minutes, long sessionId, long? performedByUserId, Instant createdAtUtc)
    {
        if (minutes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minutes));
        }

        return new EntitlementLedgerEntry(studentId, subscriptionId, courseId, levelId, -minutes,
            LedgerReason.Consumption, sessionId, null, null, null, performedByUserId, null, null, createdAtUtc);
    }

    public static EntitlementLedgerEntry ForPurchase(long studentId, long subscriptionId, long courseId,
        int levelId, int minutes, long paymentId, long performedByUserId, Instant createdAtUtc)
    {
        if (minutes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minutes));
        }

        return new EntitlementLedgerEntry(studentId, subscriptionId, courseId, levelId, minutes,
            LedgerReason.Purchase, null, paymentId, null, null, performedByUserId, null, null, createdAtUtc);
    }

    public static EntitlementLedgerEntry ForAdminGrant(long studentId, long subscriptionId, long courseId,
        int levelId, int minutes, long performedByUserId, string reason, Instant createdAtUtc)
    {
        if (minutes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minutes));
        }

        return new EntitlementLedgerEntry(studentId, subscriptionId, courseId, levelId, minutes,
            LedgerReason.AdminGrant, null, null, null, null, performedByUserId, reason, null, createdAtUtc);
    }

    public static EntitlementLedgerEntry ForMigrationOpening(long studentId, long courseId, int levelId,
        int minutes, long migrationRecordId, Instant createdAtUtc)
    {
        if (minutes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minutes));
        }

        return new EntitlementLedgerEntry(studentId, null, courseId, levelId, minutes,
            LedgerReason.MigrationOpening, null, null, migrationRecordId, null, null, null, null, createdAtUtc);
    }

    /// <summary>D-19/D-20: only ever issued when there is NO direct replacement session.</summary>
    public static EntitlementLedgerEntry ForMakeUpGranted(long studentId, long courseId, int levelId,
        int minutes, long performedByUserId, LocalDate expiresOn, Instant createdAtUtc)
    {
        if (minutes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minutes));
        }

        return new EntitlementLedgerEntry(studentId, null, courseId, levelId, minutes,
            LedgerReason.MakeUpGranted, null, null, null, null, performedByUserId, null, expiresOn, createdAtUtc);
    }

    public static EntitlementLedgerEntry ForExpiry(long studentId, long subscriptionId, long courseId,
        int levelId, int minutes, Instant createdAtUtc)
    {
        if (minutes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minutes));
        }

        return new EntitlementLedgerEntry(studentId, subscriptionId, courseId, levelId, -minutes,
            LedgerReason.Expiry, null, null, null, null, null, null, null, createdAtUtc);
    }

    public static EntitlementLedgerEntry ForAdminAdjustment(long studentId, long? subscriptionId, long courseId,
        int levelId, int deltaMinutes, long performedByUserId, string note, Instant createdAtUtc)
    {
        return new EntitlementLedgerEntry(studentId, subscriptionId, courseId, levelId, deltaMinutes,
            LedgerReason.AdminAdjustment, null, null, null, null, performedByUserId, note, null, createdAtUtc);
    }

    /// <summary>§20.5 rule 2: corrections are reversing entries, never edits.</summary>
    public static EntitlementLedgerEntry AsCorrectionOf(EntitlementLedgerEntry original, string note, long performedByUserId, Instant createdAtUtc)
    {
        return new EntitlementLedgerEntry(original.StudentId, original.SubscriptionId, original.CourseId,
            original.LevelId, -original.DeltaMinutes, LedgerReason.Correction, original.SessionId,
            original.PaymentId, original.MigrationRecordId, original.Id, performedByUserId, note, null, createdAtUtc);
    }
}
