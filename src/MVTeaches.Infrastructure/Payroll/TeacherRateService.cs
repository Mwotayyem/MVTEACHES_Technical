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

    public async Task<CreateTeacherRateResult> CreateRateAsync(long teacherId, long? courseId, int? levelId,
        int? ageGroupId, Money rate, RateUnit unit, LocalDate effectiveFrom, long createdByUserId,
        CancellationToken cancellationToken)
    {
        var teacherRate = new TeacherRate(teacherId, courseId, levelId, ageGroupId, rate, unit, effectiveFrom, createdByUserId);
        _db.TeacherRates.Add(teacherRate);
        await _db.SaveChangesAsync(cancellationToken);
        return new CreateTeacherRateResult(teacherRate.Id);
    }
}
