using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Localization;
using MVTeaches.Web.Resources;

namespace MVTeaches.Web.Identity;

/// <summary>
/// Owner instruction (Part 2, deeper localization pass): ASP.NET Core
/// Identity's own <see cref="IdentityErrorDescriber"/> produces English-only
/// messages ("Passwords must have at least one non alphanumeric character.",
/// "Email 'x' is already taken.", etc.) that surface verbatim in
/// RegisterGuardianOutcome/RegisterStudentOutcome/RegisterTeacherOutcome's
/// <c>Errors</c> collection on Admin/Students, Admin/Teachers, and
/// Admin/AssistedRegistration whenever account creation fails. This overrides
/// every error an admin can realistically hit through this app's own
/// admin-entered registration forms with the same <c>IStringLocalizer</c> +
/// resx mechanism used everywhere else, so an Arabic-reading admin sees an
/// Arabic reason. Uses the same <see cref="SharedResource"/> resx files as
/// the rest of the Web project — no separate resource set.
/// </summary>
public sealed class LocalizedIdentityErrorDescriber : IdentityErrorDescriber
{
    private readonly IStringLocalizer<SharedResource> _localizer;

    public LocalizedIdentityErrorDescriber(IStringLocalizer<SharedResource> localizer)
    {
        _localizer = localizer;
    }

    public override IdentityError DefaultError() => new()
    {
        Code = nameof(DefaultError),
        Description = _localizer["An unexpected error occurred."],
    };

    public override IdentityError ConcurrencyFailure() => new()
    {
        Code = nameof(ConcurrencyFailure),
        Description = _localizer["Optimistic concurrency failure, object has been modified."],
    };

    public override IdentityError PasswordMismatch() => new()
    {
        Code = nameof(PasswordMismatch),
        Description = _localizer["Incorrect password."],
    };

    public override IdentityError InvalidToken() => new()
    {
        Code = nameof(InvalidToken),
        Description = _localizer["Invalid token."],
    };

    public override IdentityError LoginAlreadyAssociated() => new()
    {
        Code = nameof(LoginAlreadyAssociated),
        Description = _localizer["A user with this login already exists."],
    };

    public override IdentityError InvalidUserName(string? userName) => new()
    {
        Code = nameof(InvalidUserName),
        Description = _localizer["Email '{0}' is invalid — only letters, digits, and the characters -._@+ are allowed.", userName ?? string.Empty],
    };

    public override IdentityError InvalidEmail(string? email) => new()
    {
        Code = nameof(InvalidEmail),
        Description = _localizer["Email '{0}' is invalid.", email ?? string.Empty],
    };

    public override IdentityError DuplicateUserName(string userName) => new()
    {
        Code = nameof(DuplicateUserName),
        Description = _localizer["Email '{0}' is already taken.", userName],
    };

    public override IdentityError DuplicateEmail(string email) => new()
    {
        Code = nameof(DuplicateEmail),
        Description = _localizer["Email '{0}' is already taken.", email],
    };

    public override IdentityError InvalidRoleName(string? role) => new()
    {
        Code = nameof(InvalidRoleName),
        Description = _localizer["Role name '{0}' is invalid.", role ?? string.Empty],
    };

    public override IdentityError DuplicateRoleName(string role) => new()
    {
        Code = nameof(DuplicateRoleName),
        Description = _localizer["Role name '{0}' is already taken.", role],
    };

    public override IdentityError UserAlreadyInRole(string role) => new()
    {
        Code = nameof(UserAlreadyInRole),
        Description = _localizer["User already in role '{0}'.", role],
    };

    public override IdentityError UserNotInRole(string role) => new()
    {
        Code = nameof(UserNotInRole),
        Description = _localizer["User is not in role '{0}'.", role],
    };

    public override IdentityError PasswordTooShort(int length) => new()
    {
        Code = nameof(PasswordTooShort),
        Description = _localizer["Passwords must be at least {0} characters.", length],
    };

    public override IdentityError PasswordRequiresNonAlphanumeric() => new()
    {
        Code = nameof(PasswordRequiresNonAlphanumeric),
        Description = _localizer["Passwords must have at least one non-alphanumeric character."],
    };

    public override IdentityError PasswordRequiresDigit() => new()
    {
        Code = nameof(PasswordRequiresDigit),
        Description = _localizer["Passwords must have at least one digit ('0'-'9')."],
    };

    public override IdentityError PasswordRequiresLower() => new()
    {
        Code = nameof(PasswordRequiresLower),
        Description = _localizer["Passwords must have at least one lowercase letter ('a'-'z')."],
    };

    public override IdentityError PasswordRequiresUpper() => new()
    {
        Code = nameof(PasswordRequiresUpper),
        Description = _localizer["Passwords must have at least one uppercase letter ('A'-'Z')."],
    };

    public override IdentityError PasswordRequiresUniqueChars(int uniqueChars) => new()
    {
        Code = nameof(PasswordRequiresUniqueChars),
        Description = _localizer["Passwords must use at least {0} different characters.", uniqueChars],
    };

    public override IdentityError UserLockoutNotEnabled() => new()
    {
        Code = nameof(UserLockoutNotEnabled),
        Description = _localizer["Lockout is not enabled for this user."],
    };

    public override IdentityError RecoveryCodeRedemptionFailed() => new()
    {
        Code = nameof(RecoveryCodeRedemptionFailed),
        Description = _localizer["Recovery code redemption failed."],
    };
}
