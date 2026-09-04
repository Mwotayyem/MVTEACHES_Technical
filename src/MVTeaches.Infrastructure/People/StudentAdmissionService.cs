using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.People;
using MVTeaches.Domain.People;
using MVTeaches.Domain.Placement;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;
using Npgsql;

namespace MVTeaches.Infrastructure.People;

/// <inheritdoc cref="IStudentAdmissionService"/>
public class StudentAdmissionService : IStudentAdmissionService
{
    private const string PostgresUniqueViolationSqlState = "23505";

    private readonly MvTeachesDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IClock _clock;

    public StudentAdmissionService(MvTeachesDbContext db, UserManager<ApplicationUser> userManager, IClock clock)
    {
        _db = db;
        _userManager = userManager;
        _clock = clock;
    }

    public async Task<RegisterGuardianResult> RegisterGuardianAsync(string email, string password, string fullName,
        string phoneNumber, CancellationToken cancellationToken)
    {
        // Owner decision 2026-09-04: PhoneNumber is Identity's own existing
        // column — set at creation, so no schema change and no second write.
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            PhoneNumber = phoneNumber.Trim(),
        };
        var createResult = await _userManager.CreateAsync(user, password);
        if (!createResult.Succeeded)
        {
            return new RegisterGuardianResult(RegisterGuardianOutcome.LoginFailed,
                Errors: createResult.Errors.Select(e => e.Description).ToList());
        }

        await _userManager.AddToRoleAsync(user, RoleNames.Guardian);

        var guardian = new Guardian(user.Id, fullName);
        _db.Guardians.Add(guardian);
        await _db.SaveChangesAsync(cancellationToken);

        return new RegisterGuardianResult(RegisterGuardianOutcome.Registered, guardian.Id);
    }

    public async Task<RegisterStudentResult> RegisterStudentAsync(int countryId, string fullName, LocalDate dateOfBirth,
        string? loginEmail, string? loginPassword, string? phoneNumber, CancellationToken cancellationToken)
    {
        long? userId = null;
        if (!string.IsNullOrWhiteSpace(loginEmail) && !string.IsNullOrWhiteSpace(loginPassword))
        {
            var user = new ApplicationUser
            {
                UserName = loginEmail,
                Email = loginEmail,
                EmailConfirmed = true,
                CountryId = countryId,
                PhoneNumber = string.IsNullOrWhiteSpace(phoneNumber) ? null : phoneNumber.Trim(),
            };
            var createResult = await _userManager.CreateAsync(user, loginPassword);
            if (!createResult.Succeeded)
            {
                return new RegisterStudentResult(RegisterStudentOutcome.LoginFailed,
                    Errors: createResult.Errors.Select(e => e.Description).ToList());
            }

            await _userManager.AddToRoleAsync(user, RoleNames.Student);
            userId = user.Id;
        }

        // Owner decision 2026-09-04 (student phone, stage 1): the number is now
        // stored on the Student row itself as well, which is what finally makes
        // it capturable for a child with no login. When a login WAS created it
        // is deliberately written in both places: Identity needs it on the user
        // for anything account-related, and the Student row is what every
        // "who do we call about this student" query reads, whether or not that
        // student ever signs in.
        var student = new Student(countryId, fullName, dateOfBirth, userId, phoneNumber);
        _db.Students.Add(student);
        await _db.SaveChangesAsync(cancellationToken);

        return new RegisterStudentResult(RegisterStudentOutcome.Registered, student.Id);
    }

    public async Task<LinkGuardianResult> LinkGuardianAsync(long guardianId, long studentId, GuardianRelationship relationship,
        bool isPrimary, long linkedByUserId, CancellationToken cancellationToken)
    {
        var alreadyLinked = await _db.Guardianships.AnyAsync(
            g => g.GuardianId == guardianId && g.StudentId == studentId, cancellationToken);
        if (alreadyLinked)
        {
            return new LinkGuardianResult(LinkGuardianOutcome.AlreadyLinked);
        }

        // Owner decision 2026-09-04: one responsible guardian per student in the
        // MVP. Checked AFTER the same-pair case above, so re-submitting an
        // existing link still reports the harmless AlreadyLinked rather than
        // this — only a genuinely DIFFERENT guardian is rejected.
        //
        // This deliberately reads existing rows and refuses to add to them; it
        // never edits or deletes one. Local Staging already holds three students
        // with two guardians each, and they stay exactly as they are — the rule
        // governs what may be added from here on, which is why it needed no
        // schema change and no data cleanup.
        var hasAnotherGuardian = await _db.Guardianships.AnyAsync(
            g => g.StudentId == studentId && g.GuardianId != guardianId, cancellationToken);
        if (hasAnotherGuardian)
        {
            return new LinkGuardianResult(LinkGuardianOutcome.StudentAlreadyHasGuardian);
        }

        _db.Guardianships.Add(new Guardianship(guardianId, studentId, relationship, isPrimary, linkedByUserId));

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresUniqueViolationSqlState })
        {
            // The composite-PK "already linked" case was already ruled out above,
            // so a unique violation here can only be ux_guardianship_primary — a
            // real conflict the admin must resolve, not a benign duplicate.
            _db.ChangeTracker.Clear();
            return new LinkGuardianResult(LinkGuardianOutcome.PrimaryConflict);
        }

        return new LinkGuardianResult(LinkGuardianOutcome.Linked);
    }

    public async Task VerifyStudentAsync(long studentId, CancellationToken cancellationToken)
    {
        var student = await _db.Students.FirstOrDefaultAsync(s => s.Id == studentId, cancellationToken)
            ?? throw new InvalidOperationException("Student not found.");

        if (student.Status != StudentStatus.PendingVerification)
        {
            return; // Already past this step — a safe no-op, not an error.
        }

        student.MarkVerified();
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task AssignLevelAsync(long studentId, int levelId, long assignedByUserId, string reason, CancellationToken cancellationToken)
    {
        var student = await _db.Students.FirstOrDefaultAsync(s => s.Id == studentId, cancellationToken)
            ?? throw new InvalidOperationException("Student not found.");

        var now = _clock.GetCurrentInstant();

        var previousCurrent = await _db.StudentLevels
            .Where(l => l.StudentId == studentId && l.IsCurrent)
            .ToListAsync(cancellationToken);
        foreach (var previous in previousCurrent)
        {
            previous.Supersede();
        }

        _db.StudentLevels.Add(new StudentLevel(studentId, levelId, assignedByUserId, AssignedByRole.Admin,
            LevelAssignmentSource.AdminOverride, placementInterviewId: null, reason, now));

        if (student.Status == StudentStatus.PendingLevel)
        {
            student.MarkLevelAssigned(); // §8.1: PendingLevel → Active, first assignment only.
        }

        await _db.SaveChangesAsync(cancellationToken);
    }
}
