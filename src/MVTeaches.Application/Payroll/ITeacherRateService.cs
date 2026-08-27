using MVTeaches.Domain.Common;
using MVTeaches.Domain.Payroll;
using NodaTime;

namespace MVTeaches.Application.Payroll;

public record CreateTeacherRateResult(long TeacherRateId);

/// <summary>
/// §9.2 (D-27) — there was no way to create a TeacherRate anywhere in the
/// application, which meant IPayrollService.VerifyAsync could never actually
/// succeed against a real teacher (IPayrollRateResolver, already built and
/// tested, always returns "no applicable rate" with an empty table).
/// </summary>
public interface ITeacherRateService
{
    Task<CreateTeacherRateResult> CreateRateAsync(long teacherId, long? courseId, int? levelId, int? ageGroupId,
        Money rate, RateUnit unit, LocalDate effectiveFrom, long createdByUserId, CancellationToken cancellationToken);
}
