using NodaTime;

namespace MVTeaches.Domain.People;

/// <summary>
/// A student is always its own independent entity (D-02) — siblings never share
/// a record. <see cref="UserId"/> is nullable: a 5–12 child has no login account
/// at all (D-02/D-03, D-83's guardian-presses-Join rule depends on this).
/// </summary>
public class Student
{
    public long Id { get; private set; }

    /// <summary>NULL for a child with no independent login (age group Kids).
    /// Optional for Teens (Q-07, still open). Always set for Adults.</summary>
    public long? UserId { get; private set; }

    public int CountryId { get; private set; }

    public string FullName { get; private set; } = string.Empty;

    /// <summary>Source of truth for age-group derivation. The age-group
    /// *boundaries* themselves live in the AgeGroup reference table, never
    /// hardcoded here (D-65) — see IAgeGroupResolver in the Application layer.</summary>
    public LocalDate DateOfBirth { get; private set; }

    public StudentStatus Status { get; private set; }

    /// <summary>Owner decision 2026-09-04: the student's own contact number,
    /// stored HERE rather than only on an Identity user, because the common
    /// case — a child with no login at all — has no Identity row to put it on.
    /// Until this column existed, a number could only be captured for someone
    /// who signed in, which left exactly the children the centre most needs to
    /// reach unreachable.
    /// <para>Nullable on purpose, and stays nullable: every student already in
    /// the database predates this column, and none of them may be broken by
    /// its arrival. "Required" is a rule the REGISTRATION screens apply to new
    /// adult students — see IStudentAdmissionService — not a database
    /// constraint applied retroactively to old rows.</para>
    /// <para>For a child under a guardian, the guardian's number is the one
    /// the centre actually calls; this one is stored when the family gives it
    /// (an older child's own mobile) and is simply left null when they do
    /// not.</para></summary>
    public string? PhoneNumber { get; private set; }

    private Student() { }

    public Student(int countryId, string fullName, LocalDate dateOfBirth, long? userId = null,
        string? phoneNumber = null)
    {
        if (string.IsNullOrWhiteSpace(fullName))
        {
            throw new ArgumentException("Full name is required.", nameof(fullName));
        }

        CountryId = countryId;
        FullName = fullName;
        DateOfBirth = dateOfBirth;
        UserId = userId;
        PhoneNumber = string.IsNullOrWhiteSpace(phoneNumber) ? null : phoneNumber.Trim();
        Status = StudentStatus.PendingVerification;
    }

    /// <summary>Records or corrects the student's own number after registration
    /// — an admin fixing a typo, or a family supplying one they did not have at
    /// sign-up. Blank clears it back to "not known", which is a legitimate
    /// state rather than an error.</summary>
    public void SetPhoneNumber(string? phoneNumber) =>
        PhoneNumber = string.IsNullOrWhiteSpace(phoneNumber) ? null : phoneNumber.Trim();

    public void MarkVerified() => Status = StudentStatus.PendingLevel;

    public void MarkLevelAssigned() => Status = StudentStatus.Active;

    /// <summary>D-14: an outstanding balance blocks attendance (blocks pressing Join).</summary>
    public void BlockForPayment() => Status = StudentStatus.PaymentBlocked;

    public void ClearPaymentBlock() => Status = StudentStatus.Active;

    public void MarkExpired() => Status = StudentStatus.Expired;

    public void Suspend() => Status = StudentStatus.Suspended;

    /// <summary>Grants the child a login later in life (e.g. turning 18) — a single
    /// column update, by design (Technical Study §7.1).</summary>
    public void LinkUser(long userId) => UserId = userId;

    public bool CanPressJoin => Status is StudentStatus.Active;
}
