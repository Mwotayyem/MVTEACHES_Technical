namespace MVTeaches.Domain.People;

/// <summary>Technical Study §9.1. Created only by an Admin (D-28) — no self-registration.</summary>
public class Teacher
{
    public long Id { get; private set; }

    /// <summary>1:1 with the Identity user.</summary>
    public long UserId { get; private set; }

    public string FullName { get; private set; } = string.Empty;

    /// <summary>IANA time zone id (e.g. "Europe/London") — mandatory (§14.4 rule 5).
    /// Never a Windows time zone name, never <see cref="TimeZoneInfo"/>.</summary>
    public string TimeZoneId { get; private set; } = string.Empty;

    public bool IsActive { get; private set; } = true;

    private Teacher() { }

    public Teacher(long userId, string fullName, string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(fullName))
        {
            throw new ArgumentException("Full name is required.", nameof(fullName));
        }

        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            throw new ArgumentException("An IANA time zone id is mandatory for teachers.", nameof(timeZoneId));
        }

        UserId = userId;
        FullName = fullName;
        TimeZoneId = timeZoneId;
    }

    public void ChangeTimeZone(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            throw new ArgumentException("An IANA time zone id is mandatory for teachers.", nameof(timeZoneId));
        }

        TimeZoneId = timeZoneId;
    }

    public void Deactivate() => IsActive = false;

    public void Reactivate() => IsActive = true;
}
