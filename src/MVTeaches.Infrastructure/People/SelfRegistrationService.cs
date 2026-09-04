using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.People;
using MVTeaches.Domain.People;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;

namespace MVTeaches.Infrastructure.People;

/// <inheritdoc cref="ISelfRegistrationService"/>
public class SelfRegistrationService : ISelfRegistrationService
{
    private readonly MvTeachesDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;

    /// <summary>Reused rather than reimplemented: every account this service
    /// creates goes through the SAME domain path an admin's registration does,
    /// including the one-guardian rule on linking. A second, parallel way to
    /// create a family would be a second place for those rules to drift.</summary>
    private readonly IStudentAdmissionService _admissions;

    public SelfRegistrationService(MvTeachesDbContext db, UserManager<ApplicationUser> userManager,
        IStudentAdmissionService admissions)
    {
        _db = db;
        _userManager = userManager;
        _admissions = admissions;
    }

    public async Task<SelfRegisterResult> RegisterGuardianAsync(string email, string password, string fullName,
        string phoneNumber, int countryId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber))
        {
            return new SelfRegisterResult(SelfRegisterOutcome.PhoneRequired);
        }

        if (!await IsCountryAvailableAsync(countryId, cancellationToken))
        {
            return new SelfRegisterResult(SelfRegisterOutcome.CountryNotAvailable);
        }

        var registered = await _admissions.RegisterGuardianAsync(email, password, fullName, phoneNumber, cancellationToken);
        if (registered.Outcome != RegisterGuardianOutcome.Registered)
        {
            return new SelfRegisterResult(SelfRegisterOutcome.LoginFailed, Errors: registered.Errors);
        }

        // The guardian's own country, recorded on their Identity user the same
        // way an admin-registered student's is. It is what their children's
        // records default to, and what decides the currency they are quoted in.
        var guardian = await _db.Guardians.FirstAsync(g => g.Id == registered.GuardianId!.Value, cancellationToken);
        var user = await _db.Users.FirstAsync(u => u.Id == guardian.UserId, cancellationToken);
        user.CountryId = countryId;
        await _db.SaveChangesAsync(cancellationToken);

        return new SelfRegisterResult(SelfRegisterOutcome.Registered, GuardianId: registered.GuardianId);
    }

    public async Task<SelfRegisterResult> RegisterAdultStudentAsync(string email, string password, string fullName,
        LocalDate dateOfBirth, string phoneNumber, int countryId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber))
        {
            return new SelfRegisterResult(SelfRegisterOutcome.PhoneRequired);
        }

        if (!await IsCountryAvailableAsync(countryId, cancellationToken))
        {
            return new SelfRegisterResult(SelfRegisterOutcome.CountryNotAvailable);
        }

        // A login IS created here — that is what "an adult signing themselves
        // up" means — and NO guardian is linked, which is what leaves them free
        // to buy their own packages. The student lands in PendingVerification
        // with no level, so they can sign in and see where they stand but
        // cannot yet buy or book. See the interface remarks on verification.
        var registered = await _admissions.RegisterStudentAsync(countryId, fullName, dateOfBirth,
            loginEmail: email, loginPassword: password, phoneNumber, cancellationToken);

        return registered.Outcome == RegisterStudentOutcome.Registered
            ? new SelfRegisterResult(SelfRegisterOutcome.Registered, StudentId: registered.StudentId)
            : new SelfRegisterResult(SelfRegisterOutcome.LoginFailed, Errors: registered.Errors);
    }

    public async Task<AddOwnChildResult> AddOwnChildAsync(long actingUserId, string fullName, LocalDate dateOfBirth,
        string? phoneNumber, int countryId, CancellationToken cancellationToken)
    {
        // Which guardian this is, resolved from the signed-in account — never
        // accepted as a parameter. A guardian id in a request body would let
        // any guardian add children to any other guardian's family.
        var guardian = await _db.Guardians.FirstOrDefaultAsync(g => g.UserId == actingUserId, cancellationToken);
        if (guardian is null)
        {
            return new AddOwnChildResult(AddOwnChildOutcome.NotAGuardian);
        }

        if (!await IsCountryAvailableAsync(countryId, cancellationToken))
        {
            return new AddOwnChildResult(AddOwnChildOutcome.CountryNotAvailable);
        }

        // No login for the child: this is D-02/D-03's ordinary case, a child
        // reached through their guardian's account. The phone is optional for
        // exactly the same reason it is optional on the admin form — the number
        // the centre calls about this child is the guardian's, and this one is
        // recorded only if the family has one to give.
        var student = await _admissions.RegisterStudentAsync(countryId, fullName, dateOfBirth,
            loginEmail: null, loginPassword: null, phoneNumber, cancellationToken);

        // A brand-new student cannot already have a guardian, so this cannot
        // hit the one-guardian rule — but it is still routed through the shared
        // LinkGuardianAsync rather than inserting the row here, so that every
        // rule that path enforces today or gains later applies to this one too.
        await _admissions.LinkGuardianAsync(guardian.Id, student.StudentId!.Value, GuardianRelationship.Parent,
            isPrimary: true, linkedByUserId: actingUserId, cancellationToken);

        return new AddOwnChildResult(AddOwnChildOutcome.Added, student.StudentId);
    }

    /// <summary>A country id arriving in a request body is not a promise that
    /// the country exists or that the centre operates there.</summary>
    private Task<bool> IsCountryAvailableAsync(int countryId, CancellationToken cancellationToken) =>
        _db.Countries.AnyAsync(c => c.Id == countryId && c.IsActive, cancellationToken);
}
