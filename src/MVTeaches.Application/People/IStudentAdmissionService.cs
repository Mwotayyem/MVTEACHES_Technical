using MVTeaches.Domain.People;
using NodaTime;

namespace MVTeaches.Application.People;

public enum RegisterGuardianOutcome { Registered, LoginFailed }
public record RegisterGuardianResult(RegisterGuardianOutcome Outcome, long? GuardianId = null, IReadOnlyList<string>? Errors = null);

public enum RegisterStudentOutcome { Registered, LoginFailed }
public record RegisterStudentResult(RegisterStudentOutcome Outcome, long? StudentId = null, IReadOnlyList<string>? Errors = null);

public enum LinkGuardianOutcome
{
    Linked,

    /// <summary>This exact (guardian, student) pair is already linked — a safe,
    /// idempotent no-op rather than a duplicate-row error.</summary>
    AlreadyLinked,

    /// <summary>§7.2's ux_guardianship_primary: a student may have only one
    /// primary guardian at a time — requesting a second one is a genuine
    /// conflict the admin must resolve (e.g. un-primary the existing one first),
    /// not something to silently accept.</summary>
    PrimaryConflict,
}

public record LinkGuardianResult(LinkGuardianOutcome Outcome);

/// <summary>
/// §7/§8/§10 — the admin-facing side of onboarding a family. Real
/// Guardian/Student self-registration is documented as phone+OTP via
/// WhatsApp (§7), which is genuinely blocked pending WhatsApp configuration
/// (see docs/deployment/STATUS.md) — this service is the honest interim
/// path: an admin enters the data directly, exactly as staff already do over
/// the phone today. It drives the SAME domain state machines (Student,
/// Guardian, Guardianship, StudentLevel) any future self-registration flow
/// would call — nothing here is a shortcut around those invariants.
/// </summary>
public interface IStudentAdmissionService
{
    /// <summary>Creates a real ASP.NET Core Identity login (email+password) plus
    /// the Guardian record and Guardian role membership — a guardian always has
    /// a login (§7.1); there is no "guardian with no account" case.</summary>
    Task<RegisterGuardianResult> RegisterGuardianAsync(string email, string password, string fullName, CancellationToken cancellationToken);

    /// <summary>Creates a Student row (PendingVerification, §8.1) and, only if
    /// both a login email AND password are supplied, an email+password login
    /// for the student themselves. A child with no independent login (D-02/D-03)
    /// is the default — pass null/null for that case. Does not link a guardian;
    /// call <see cref="LinkGuardianAsync"/> separately.</summary>
    Task<RegisterStudentResult> RegisterStudentAsync(int countryId, string fullName, LocalDate dateOfBirth,
        string? loginEmail, string? loginPassword, CancellationToken cancellationToken);

    Task<LinkGuardianResult> LinkGuardianAsync(long guardianId, long studentId, GuardianRelationship relationship,
        bool isPrimary, long linkedByUserId, CancellationToken cancellationToken);

    /// <summary>The admin manually confirming what a WhatsApp OTP would confirm
    /// automatically once configured (§8.1's PendingVerification → PendingLevel).
    /// A safe no-op if the student isn't in PendingVerification any more.</summary>
    Task VerifyStudentAsync(long studentId, CancellationToken cancellationToken);

    /// <summary>§10.3 — records a new current StudentLevel row (superseding any
    /// previous one) as an explicit, reasoned AdminOverride, since no placement
    /// interview flow exists yet. Advances the student PendingLevel → Active
    /// (§8.1) the first time a level is assigned; a later re-assignment (a
    /// promotion) leaves an already-Active student's status untouched.</summary>
    Task AssignLevelAsync(long studentId, int levelId, long assignedByUserId, string reason, CancellationToken cancellationToken);
}
