using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Payroll;
using MVTeaches.Domain.Delivery;
using MVTeaches.Domain.Payroll;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;

namespace MVTeaches.Infrastructure.Payroll;

/// <summary>
/// Technical Study §18.1/§18.2 (D-26) — see IPayrollService's remarks for the
/// full cycle this orchestrates. Every state transition itself is enforced by
/// SessionDelivery/PayrollPeriod (Domain); this class's job is purely the
/// lookups and cross-entity work those entities cannot do alone.
/// </summary>
public class PayrollService : IPayrollService
{
    private readonly MvTeachesDbContext _db;
    private readonly IPayrollRateResolver _rateResolver;
    private readonly IClock _clock;

    public PayrollService(MvTeachesDbContext db, IPayrollRateResolver rateResolver, IClock clock)
    {
        _db = db;
        _rateResolver = rateResolver;
        _clock = clock;
    }

    public async Task<DeclareDeliveryResult> DeclareAsync(long sessionId, long declaredByUserId, int declaredMinutes, string? note, CancellationToken ct)
    {
        var session = await _db.ClassSessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null)
        {
            return new DeclareDeliveryResult(DeclareDeliveryOutcome.SessionNotFound);
        }

        // §18.3 rule 6: a session already marked NotDelivered never enters the
        // payroll pipeline at all — there is nothing here for a teacher to declare.
        if (session.Status == ClassSessionStatus.NotDelivered)
        {
            return new DeclareDeliveryResult(DeclareDeliveryOutcome.SessionNotDelivered);
        }

        var delivery = await _db.SessionDeliveries.FirstOrDefaultAsync(d => d.SessionId == sessionId, ct);
        if (delivery is null)
        {
            // Lazily provisioned — the documented cycle has no separate
            // "create a delivery row" step; Pending starts implicitly here.
            delivery = new SessionDelivery(sessionId, session.TeacherId);
            _db.SessionDeliveries.Add(delivery);
        }
        else if (delivery.State != DeliveryState.Pending)
        {
            return new DeclareDeliveryResult(DeclareDeliveryOutcome.AlreadyDeclared);
        }

        delivery.Declare(declaredByUserId, declaredMinutes, note, _clock.GetCurrentInstant());
        await _db.SaveChangesAsync(ct);
        return new DeclareDeliveryResult(DeclareDeliveryOutcome.Declared);
    }

    public async Task<VerifyDeliveryResult> VerifyAsync(long sessionId, long verifiedByUserId, string? note, CancellationToken ct)
    {
        var delivery = await _db.SessionDeliveries.FirstOrDefaultAsync(d => d.SessionId == sessionId, ct);
        if (delivery is null)
        {
            return new VerifyDeliveryResult(VerifyDeliveryOutcome.DeliveryNotFound);
        }

        if (delivery.State != DeliveryState.Declared)
        {
            return new VerifyDeliveryResult(VerifyDeliveryOutcome.NotDeclared);
        }

        if (verifiedByUserId == delivery.DeclaredByUserId)
        {
            return new VerifyDeliveryResult(VerifyDeliveryOutcome.SameActorAsDeclarer);
        }

        var session = await _db.ClassSessions.FirstAsync(s => s.Id == sessionId, ct);
        var onDate = OccurrenceLocalDate(session);

        var resolved = await _rateResolver.ResolveAsync(session.TeacherId, session.CourseId, session.LevelId, session.AgeGroupId, onDate, ct);
        if (resolved is null)
        {
            return new VerifyDeliveryResult(VerifyDeliveryOutcome.NoApplicableRate);
        }

        // D-59/D-62: the scheduled duration, always — never the teacher's own
        // declared_minutes, never a measured value.
        delivery.Verify(verifiedByUserId, session.DurationMinutes, resolved.Rate, resolved.Unit,
            resolved.TeacherRateId, note, _clock.GetCurrentInstant());
        await _db.SaveChangesAsync(ct);
        return new VerifyDeliveryResult(VerifyDeliveryOutcome.Verified);
    }

    public async Task<RejectDeliveryResult> RejectAsync(long sessionId, long rejectedByUserId, string reason, CancellationToken ct)
    {
        var delivery = await _db.SessionDeliveries.FirstOrDefaultAsync(d => d.SessionId == sessionId, ct);
        if (delivery is null)
        {
            return new RejectDeliveryResult(RejectDeliveryOutcome.DeliveryNotFound);
        }

        if (delivery.State != DeliveryState.Declared)
        {
            return new RejectDeliveryResult(RejectDeliveryOutcome.NotDeclared);
        }

        // Rejection is a verification-adjacent action (it stands in for "verify,
        // but the answer is no") — §18.3 rule 3's separation of duties applies
        // here too, even though SessionDelivery.Reject itself does not persist
        // who rejected.
        if (rejectedByUserId == delivery.DeclaredByUserId)
        {
            return new RejectDeliveryResult(RejectDeliveryOutcome.SameActorAsDeclarer);
        }

        delivery.Reject(reason);
        await _db.SaveChangesAsync(ct);
        return new RejectDeliveryResult(RejectDeliveryOutcome.Rejected);
    }

    public async Task<OpenPayrollPeriodResult> OpenPeriodAsync(int countryId, LocalDate periodStart, LocalDate periodEnd, CancellationToken ct)
    {
        var period = new PayrollPeriod(countryId, periodStart, periodEnd);
        _db.PayrollPeriods.Add(period);
        await _db.SaveChangesAsync(ct); // UNIQUE(country_id, period_start, period_end) guards a duplicate period
        return new OpenPayrollPeriodResult(period.Id);
    }

    public async Task<int> AggregateVerifiedDeliveriesAsync(long periodId, CancellationToken ct)
    {
        var period = await GetPeriodAsync(periodId, ct);
        if (period.Status != PayrollPeriodStatus.Open)
        {
            throw new InvalidOperationException($"Cannot aggregate into a {period.Status} period — only an Open one collects deliveries.");
        }

        // Only deliveries not yet claimed by ANY period — this is what makes a
        // rerun safe: a delivery aggregated last night is simply skipped tonight.
        var candidates = await _db.SessionDeliveries
            .Where(d => d.State == DeliveryState.Verified && d.PayrollPeriodId == null)
            .Join(_db.ClassSessions, d => d.SessionId, s => s.Id, (d, s) => new { Delivery = d, Session = s })
            .Where(x => x.Session.CountryId == period.CountryId)
            .ToListAsync(ct);

        var created = 0;
        foreach (var candidate in candidates)
        {
            var localDate = OccurrenceLocalDate(candidate.Session);
            if (localDate < period.PeriodStart || localDate > period.PeriodEnd)
            {
                continue; // Verified, but for a different period's date range.
            }

            _db.PayrollLines.Add(new PayrollLine(period.Id, candidate.Delivery.TeacherId, candidate.Session.Id,
                candidate.Delivery.VerifiedMinutes!.Value, candidate.Delivery.RateAmount!.Value,
                candidate.Delivery.RateCurrency!, candidate.Delivery.PayableAmount!.Value));
            candidate.Delivery.AssignToPayrollPeriod(period.Id);
            created++;
        }

        if (created > 0)
        {
            await _db.SaveChangesAsync(ct); // UNIQUE(period_id, session_id) is the actual backstop against a double line
        }

        return created;
    }

    public async Task MoveToReviewAsync(long periodId, CancellationToken ct)
    {
        var period = await GetPeriodAsync(periodId, ct);
        period.MoveToReview();
        await _db.SaveChangesAsync(ct);
    }

    public async Task ApprovePeriodAsync(long periodId, long approvedByUserId, CancellationToken ct)
    {
        var period = await GetPeriodAsync(periodId, ct);
        period.Approve(approvedByUserId, _clock.GetCurrentInstant());
        await _db.SaveChangesAsync(ct);
    }

    public async Task MarkPeriodPaidAsync(long periodId, CancellationToken ct)
    {
        var period = await GetPeriodAsync(periodId, ct);
        period.MarkPaid();

        var sessionIds = await _db.PayrollLines.Where(l => l.PeriodId == periodId).Select(l => l.SessionId).ToListAsync(ct);
        var deliveries = await _db.SessionDeliveries.Where(d => sessionIds.Contains(d.SessionId)).ToListAsync(ct);
        foreach (var delivery in deliveries)
        {
            delivery.MarkPaid();
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task ClosePeriodAsync(long periodId, CancellationToken ct)
    {
        var period = await GetPeriodAsync(periodId, ct);
        period.Close();
        await _db.SaveChangesAsync(ct);
    }

    private async Task<PayrollPeriod> GetPeriodAsync(long periodId, CancellationToken ct) =>
        await _db.PayrollPeriods.FirstOrDefaultAsync(p => p.Id == periodId, ct)
            ?? throw new InvalidOperationException($"Payroll period {periodId} not found.");

    /// <summary>The session's own local calendar date, in the zone it was
    /// scheduled in — the same convention ScheduleGenerationService uses, so a
    /// session generated for "Tuesday" always lands in the payroll period that
    /// actually covers that Tuesday, regardless of the server's own time zone.</summary>
    private static LocalDate OccurrenceLocalDate(ClassSession session) =>
        session.StartsAtUtc.InZone(DateTimeZoneProviders.Tzdb[session.ScheduleTimeZone]).Date;
}
