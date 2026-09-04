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
    /// not something to silently accept.
    /// <para>Since the owner's 2026-09-04 one-guardian rule below rejects ANY
    /// second guardian before the insert is attempted, this outcome is no
    /// longer reachable through this service. Both the member and the partial
    /// unique index behind it are deliberately kept: the index is still the
    /// database's own last line of defence for the seeders and for any future
    /// path (a guardian-replacement flow) that relaxes the rule above it.</para></summary>
    PrimaryConflict,

    /// <summary>Owner decision 2026-09-04 (one responsible guardian per student
    /// in the MVP): this student is already linked to a guardian, so a second,
    /// different guardian may not be added. The Guardianship table remains a
    /// many-to-many join — nothing about the schema changed, and existing rows
    /// with more than one guardian are left exactly as they are — this is a
    /// deliberate MVP narrowing of who may be ADDED from here on, so that
    /// "who is responsible for this student" always has one unambiguous
    /// answer. No replace-the-guardian path exists yet: un-linking is not
    /// offered, so this is intentionally a dead end the admin must raise
    /// rather than route around.</summary>
    StudentAlreadyHasGuardian,
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
    /// a login (§7.1); there is no "guardian with no account" case.
    /// <para>Owner decision 2026-09-04: <paramref name="phoneNumber"/> is
    /// mandatory. A guardian is the person the centre calls when a child does
    /// not appear for a paid session, and 0 of the 19 accounts in staging had a
    /// number — the centre had no way to reach anybody. It is stored on the
    /// Identity user's own PhoneNumber column, which already exists, so this
    /// needs no schema change.</para></summary>
    Task<RegisterGuardianResult> RegisterGuardianAsync(string email, string password, string fullName,
        string phoneNumber, CancellationToken cancellationToken);

    /// <summary>Creates a Student row (PendingVerification, §8.1) and, only if
    /// both a login email AND password are supplied, an email+password login
    /// for the student themselves. A child with no independent login (D-02/D-03)
    /// is the default — pass null/null for that case. Does not link a guardian;
    /// call <see cref="LinkGuardianAsync"/> separately.
    /// <para>Owner decision 2026-09-04: <paramref name="phoneNumber"/> is stored
    /// on the Student row itself (Student.PhoneNumber), and additionally on the
    /// student's Identity user when a login is being created. Storing it on the
    /// Student row is what makes a number capturable for a child with NO login
    /// — the common case, and previously the one the centre could not record at
    /// all. Null is always accepted: for a child under a guardian, the number
    /// the centre actually calls is the guardian's, and this one is simply
    /// recorded when the family has one to give.</para></summary>
    Task<RegisterStudentResult> RegisterStudentAsync(int countryId, string fullName, LocalDate dateOfBirth,
        string? loginEmail, string? loginPassword, string? phoneNumber, CancellationToken cancellationToken);

    /// <summary>Links a guardian to a student. Owner decision 2026-09-04: in the
    /// MVP a student has exactly ONE responsible guardian, so this refuses a
    /// second, different guardian
    /// (<see cref="LinkGuardianOutcome.StudentAlreadyHasGuardian"/>) while
    /// re-linking the SAME pair stays the safe no-op it always was. The check
    /// lives here rather than on the admin pages precisely because both of them
    /// (/Admin/Students and /Admin/AssistedRegistration) call this one method —
    /// a rule enforced on a page is a rule the other page does not have.</summary>
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
