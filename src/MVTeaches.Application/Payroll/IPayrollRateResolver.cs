using MVTeaches.Domain.Common;
using MVTeaches.Domain.Payroll;
using NodaTime;

namespace MVTeaches.Application.Payroll;

/// <summary>
/// Technical Study §9.2 (D-27) — most-specific-wins selection at verification
/// time: (course+level+ageGroup) then (course+level) then (course) then the
/// teacher's own default (all nulls). Referenced by TeacherRate's own remarks
/// as the mechanism that implements <see cref="TeacherRate.Specificity"/>.
/// </summary>
public interface IPayrollRateResolver
{
    Task<ResolvedRate?> ResolveAsync(long teacherId, long courseId, int levelId, int ageGroupId, LocalDate onDate, CancellationToken cancellationToken);
}

/// <summary>The winning rate, already reduced to what <c>SessionDelivery.Verify</c>
/// needs — the caller never re-derives specificity itself.</summary>
public record ResolvedRate(long TeacherRateId, Money Rate, RateUnit Unit);
