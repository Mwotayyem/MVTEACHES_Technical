using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Payroll;
using MVTeaches.Domain.Common;
using MVTeaches.Domain.Payroll;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;

namespace MVTeaches.Infrastructure.Payroll;

/// <inheritdoc cref="ITeacherRateService"/>
public class TeacherRateService : ITeacherRateService
{
    private readonly MvTeachesDbContext _db;

    public TeacherRateService(MvTeachesDbContext db) => _db = db;

    /// <summary>
    /// Owner decision 2026-09-04: <b>exactly one open rate per (teacher,
    /// course, level, age group)</b>. Adding a new one closes the previous at
    /// the new one's start date.
    ///
    /// <para>Before this, every call was a bare INSERT and
    /// <see cref="TeacherRate.Close"/> was dead code — never invoked anywhere
    /// in the application. Two open rows for the same combination left
    /// PayrollRateResolver's <c>OrderByDescending(Specificity)
    /// .FirstOrDefault()</c> choosing between equally-specific candidates with
    /// nothing to break the tie, so which figure a teacher was actually paid
    /// after a raise depended on the order the database happened to return
    /// rows in. That is a money bug, not an untidiness.</para>
    ///
    /// <para>Only rates that are still OPEN (EffectiveTo is null) are closed.
    /// An already-closed historical rate is left exactly as it is, and no
    /// verified delivery, payroll line or payment is read or written here —
    /// past pay stays computed at the figure that was in force on the day.</para>
    /// </summary>
    public async Task<CreateTeacherRateResult> CreateRateAsync(long teacherId, long? courseId, int? levelId,
        int? ageGroupId, Money rate, RateUnit unit, LocalDate effectiveFrom, long createdByUserId,
        CancellationToken cancellationToken)
    {
        // Constructed first so a negative amount is still rejected before
        // anything else is read or written (see A_negative_rate_is_rejected).
        var teacherRate = new TeacherRate(teacherId, courseId, levelId, ageGroupId, rate, unit, effectiveFrom, createdByUserId);

        // The nullable dimensions are matched in memory rather than in the
        // WHERE clause: a null here means "any", and `column == null` is not
        // something to leave to a provider's null-semantics rewriting when the
        // answer decides who gets closed. One teacher's rates are a handful of
        // rows - PayrollRateResolver materializes the same set for the same
        // reason.
        var openRates = await _db.TeacherRates
            .Where(r => r.TeacherId == teacherId && r.EffectiveTo == null)
            .ToListAsync(cancellationToken);

        var sameCombination = openRates
            .Where(r => r.CourseId == courseId && r.LevelId == levelId && r.AgeGroupId == ageGroupId)
            .ToList();

        // Refuse rather than write something that cannot be closed. Both cases
        // would otherwise leave two open rates for one combination.
        var blocking = sameCombination.FirstOrDefault(r => r.EffectiveFrom >= effectiveFrom);
        if (blocking is not null)
        {
            return new CreateTeacherRateResult(
                blocking.EffectiveFrom == effectiveFrom
                    ? CreateTeacherRateOutcome.DuplicateStartDate
                    : CreateTeacherRateOutcome.StartsBeforeExistingRate,
                ExistingEffectiveFrom: blocking.EffectiveFrom);
        }

        foreach (var existing in sameCombination)
        {
            existing.Close(effectiveFrom);
        }

        _db.TeacherRates.Add(teacherRate);

        // One SaveChanges: the close and the insert land together or not at
        // all, so there is no instant at which this teacher has two open rates
        // or none.
        await _db.SaveChangesAsync(cancellationToken);
        return new CreateTeacherRateResult(CreateTeacherRateOutcome.Created, teacherRate.Id);
    }
}
