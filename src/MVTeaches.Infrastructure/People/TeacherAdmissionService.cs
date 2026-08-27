using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.People;
using MVTeaches.Domain.People;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;

namespace MVTeaches.Infrastructure.People;

/// <inheritdoc cref="ITeacherAdmissionService"/>
public class TeacherAdmissionService : ITeacherAdmissionService
{
    private readonly MvTeachesDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;

    public TeacherAdmissionService(MvTeachesDbContext db, UserManager<ApplicationUser> userManager)
    {
        _db = db;
        _userManager = userManager;
    }

    public async Task<RegisterTeacherResult> RegisterTeacherAsync(string email, string password, string fullName,
        string timeZoneId, CancellationToken cancellationToken)
    {
        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
        var createResult = await _userManager.CreateAsync(user, password);
        if (!createResult.Succeeded)
        {
            return new RegisterTeacherResult(RegisterTeacherOutcome.LoginFailed,
                Errors: createResult.Errors.Select(e => e.Description).ToList());
        }

        await _userManager.AddToRoleAsync(user, RoleNames.Teacher);

        var teacher = new Teacher(user.Id, fullName, timeZoneId);
        _db.Teachers.Add(teacher);
        await _db.SaveChangesAsync(cancellationToken);

        return new RegisterTeacherResult(RegisterTeacherOutcome.Registered, teacher.Id);
    }

    public async Task DeactivateAsync(long teacherId, CancellationToken cancellationToken)
    {
        var teacher = await _db.Teachers.FirstOrDefaultAsync(t => t.Id == teacherId, cancellationToken)
            ?? throw new InvalidOperationException("Teacher not found.");
        teacher.Deactivate();
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task ReactivateAsync(long teacherId, CancellationToken cancellationToken)
    {
        var teacher = await _db.Teachers.FirstOrDefaultAsync(t => t.Id == teacherId, cancellationToken)
            ?? throw new InvalidOperationException("Teacher not found.");
        teacher.Reactivate();
        await _db.SaveChangesAsync(cancellationToken);
    }
}
