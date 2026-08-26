namespace MVTeaches.Domain.People;

/// <summary>
/// A guardian account (Technical Study §7.1, D-01). Always has a 1:1 login
/// (<see cref="UserId"/>) — a guardian is, by definition, someone who can log in.
/// </summary>
public class Guardian
{
    public long Id { get; private set; }

    /// <summary>1:1 with the Identity user who logs in as this guardian.</summary>
    public long UserId { get; private set; }

    public string FullName { get; private set; } = string.Empty;

    public ICollection<Guardianship> Guardianships { get; private set; } = new List<Guardianship>();

    private Guardian() { }

    public Guardian(long userId, string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName))
        {
            throw new ArgumentException("Full name is required.", nameof(fullName));
        }

        UserId = userId;
        FullName = fullName;
    }

    public void Rename(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName))
        {
            throw new ArgumentException("Full name is required.", nameof(fullName));
        }

        FullName = fullName;
    }
}
