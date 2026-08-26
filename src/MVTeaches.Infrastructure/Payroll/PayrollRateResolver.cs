using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Payroll;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;

namespace MVTeaches.Infrastructure.Payroll;

/// <summary>See IPayrollRateResolver's remarks — §9.2/D-27's most-specific-wins rule.</summary>
public class PayrollRateResolver : IPayrollRateResolver
{
    private readonly MvTeachesDbContext _db;

    public PayrollRateResolver(MvTeachesDbContext db) => _db = db;

    public async Task<ResolvedRate?> ResolveAsync(long teacherId, long courseId, int levelId, int ageGroupId,
        LocalDate onDate, CancellationToken cancellationToken)
    {
        // A null field on the rate row is a wildcard for that dimension; a
        // non-null field must match exactly. Specificity (how many dimensions
        // are pinned) is a computed, unmapped property (see TeacherRateConfiguration's
        // b.Ignore), so the winner is picked in memory after materializing the
        // — necessarily small, per-teacher — set of candidates.
        var candidates = await _db.TeacherRates
            .Where(r => r.TeacherId == teacherId
                        && (r.CourseId == null || r.CourseId == courseId)
                        && (r.LevelId == null || r.LevelId == levelId)
                        && (r.AgeGroupId == null || r.AgeGroupId == ageGroupId)
                        && r.EffectiveFrom <= onDate
                        && (r.EffectiveTo == null || onDate < r.EffectiveTo))
            .ToListAsync(cancellationToken);

        var best = candidates.OrderByDescending(r => r.Specificity).FirstOrDefault();
        return best is null ? null : new ResolvedRate(best.Id, best.Rate, best.Unit);
    }
}
