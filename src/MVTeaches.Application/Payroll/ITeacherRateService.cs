using MVTeaches.Domain.Common;
using MVTeaches.Domain.Payroll;
using NodaTime;

namespace MVTeaches.Application.Payroll;

public enum CreateTeacherRateOutcome
{
    Created,

    /// <summary>Owner decision 2026-09-04: a rate for this exact
    /// (teacher, course, level, age group) combination already starts on the
    /// very same date. The previous rate cannot be closed at the new one's
    /// start — a period that ends the day it begins is not a period, and the
    /// database's own ck_rate_effective_range check refuses it — so the two
    /// would both stay open, which is the ambiguity this rule exists to
    /// prevent. Nothing is written.</summary>
    DuplicateStartDate,

    /// <summary>The new rate starts BEFORE one that already exists for the
    /// same combination. Closing the existing rate at the new one's start
    /// would end it before it began. Nothing is written — see the same owner
    /// decision; backdating a rate is a separate decision not taken yet.</summary>
    StartsBeforeExistingRate,
}

/// <summary><paramref name="TeacherRateId"/> is null unless
/// <paramref name="Outcome"/> is Created. <paramref name="ExistingEffectiveFrom"/>
/// names the rate that got in the way, so the screen can say which date is
/// already taken rather than only that something is.</summary>
public record CreateTeacherRateResult(CreateTeacherRateOutcome Outcome, long? TeacherRateId = null,
    LocalDate? ExistingEffectiveFrom = null);

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
