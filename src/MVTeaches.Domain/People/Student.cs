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

    private Student() { }

    public Student(int countryId, string fullName, LocalDate dateOfBirth, long? userId = null)
    {
        if (string.IsNullOrWhiteSpace(fullName))
        {
            throw new ArgumentException("Full name is required.", nameof(fullName));
        }

        CountryId = countryId;
        FullName = fullName;
        DateOfBirth = dateOfBirth;
        UserId = userId;
        Status = StudentStatus.PendingVerification;
    }

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
