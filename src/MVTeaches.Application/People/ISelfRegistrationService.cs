using NodaTime;

namespace MVTeaches.Application.People;

public enum SelfRegisterOutcome
{
    Registered,

    /// <summary>Identity refused the account — a duplicate email, or a password
    /// that fails the configured policy. The reasons are returned verbatim so
    /// the person is told what to fix rather than "something went wrong".</summary>
    LoginFailed,

    /// <summary>The chosen country is not one the centre operates in. Checked
    /// server-side because a country id arriving in a request body is not a
    /// promise that the country exists or is active.</summary>
    CountryNotAvailable,

    /// <summary>A phone number is mandatory for a self-registration — see the
    /// interface remarks.</summary>
    PhoneRequired,
}

public record SelfRegisterResult(
    SelfRegisterOutcome Outcome,
    long? GuardianId = null,
    long? StudentId = null,
    IReadOnlyList<string>? Errors = null);

public enum AddOwnChildOutcome
{
    Added,

    /// <summary>The acting account is not a guardian — either it has no
    /// Guardian row at all, or it is some other kind of user entirely. Refused
    /// here rather than trusted from the page's [Authorize] attribute alone.</summary>
    NotAGuardian,

    CountryNotAvailable,
}

public record AddOwnChildResult(AddOwnChildOutcome Outcome, long? StudentId = null);

/// <summary>
/// Owner decision 2026-09-04: families can create their own accounts, instead
/// of every registration going through an admin typing it in from a phone call.
///
/// <para><b>What a new account can and cannot do.</b> A self-registered student
/// starts with no level, no package and no sessions — exactly as an
/// admin-registered one does. Nothing here grants anything: it creates the
/// person and stops. Buying and booking stay gated on having a level in the
/// relevant course, which is enforced by SubscriptionService and
/// StudentBookingService and is not weakened by this path.</para>
///
/// <para><b>On verification.</b> §7 documents phone + OTP over WhatsApp, and
/// that remains genuinely unbuilt — there is no WhatsApp provider configured
/// and this service deliberately does not pretend otherwise. A self-registered
/// student is created in PendingVerification, the same status an
/// admin-registered one gets, so the centre still confirms the family before
/// they become Active. That is an honest interim state, not a substitute OTP:
/// registration must not be blocked on an integration that does not exist, and
/// it must not claim a verification that never happened.</para>
///
/// <para><b>Phone numbers are mandatory here</b> in both flows, without
/// exception. An admin registering a family is on the phone with them already;
/// somebody signing themselves up at midnight is not, and a self-registration
/// with no way to reach the person is a record the centre cannot act on.</para>
/// </summary>
public interface ISelfRegistrationService
{
    /// <summary>Creates a Guardian account for somebody signing themselves up.
    /// The guardian has no children yet — they add them with
    /// <see cref="AddOwnChildAsync"/> once signed in.</summary>
    Task<SelfRegisterResult> RegisterGuardianAsync(string email, string password, string fullName,
        string phoneNumber, int countryId, CancellationToken cancellationToken);

    /// <summary>Creates a Student account for an adult signing themselves up:
    /// their own login, their own record, and no guardian. Being unlinked is
    /// what lets them buy for themselves — a student WITH a guardian is
    /// deliberately blocked from purchasing (owner decision 2026-09-04), and
    /// this path does not, and must not, create a guardian link.</summary>
    Task<SelfRegisterResult> RegisterAdultStudentAsync(string email, string password, string fullName,
        LocalDate dateOfBirth, string phoneNumber, int countryId, CancellationToken cancellationToken);

    /// <summary>Adds a child under the signed-in guardian's own account, and
    /// links them in one step.
    ///
    /// <para>Each child is an independent Student with their own id, their own
    /// level per course, their own packages and their own entitlement balance.
    /// Nothing about this method shares anything between siblings — that
    /// separation is a property of the schema (every subscription and every
    /// ledger entry carries one studentId), not something this method has to
    /// maintain by being careful.</para>
    ///
    /// <para><paramref name="actingUserId"/> is resolved to a Guardian row
    /// server-side; a page's [Authorize(Roles = Guardian)] says the caller
    /// holds the role, not which guardian they are, and a guardian id taken
    /// from a request body would let anyone add children to anyone.</para></summary>
    Task<AddOwnChildResult> AddOwnChildAsync(long actingUserId, string fullName, LocalDate dateOfBirth,
        string? phoneNumber, int countryId, CancellationToken cancellationToken);
}
