namespace MVTeaches.Domain.People;

public enum GuardianRelationship
{
    Parent,
    LegalGuardian,
    Other,
}

/// <summary>
/// Link table between guardians and students (§7.2). A join table rather than a
/// direct <c>students.guardian_id</c> column, specifically to support separated
/// parents / a parent-plus-grandparent case at zero cost (Q-06).
/// Composite key (GuardianId, StudentId); exactly one row per student may have
/// <see cref="IsPrimary"/> = true — enforced by a partial unique index in
/// Infrastructure (ux_guardianship_primary), not by application code alone.
/// </summary>
public class Guardianship
{
    public long GuardianId { get; private set; }
    public Guardian? Guardian { get; private set; }

    public long StudentId { get; private set; }
    public People.Student? Student { get; private set; }

    public GuardianRelationship Relationship { get; private set; }

    public bool IsPrimary { get; private set; }

    public bool CanPay { get; private set; } = true;

    public DateTimeOffset LinkedAtUtc { get; private set; }

    public long? LinkedByUserId { get; private set; }

    private Guardianship() { }

    public Guardianship(long guardianId, long studentId, GuardianRelationship relationship, bool isPrimary, long? linkedByUserId)
    {
        GuardianId = guardianId;
        StudentId = studentId;
        Relationship = relationship;
        IsPrimary = isPrimary;
        CanPay = true;
        LinkedAtUtc = DateTimeOffset.UtcNow;
        LinkedByUserId = linkedByUserId;
    }
}
