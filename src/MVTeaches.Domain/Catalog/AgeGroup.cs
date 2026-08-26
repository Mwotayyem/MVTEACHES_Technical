namespace MVTeaches.Domain.Catalog;

/// <summary>
/// Technical Study §12.1 (D-04). Seeded with exactly three rows (Kids 5-12,
/// Teens 13-17, Adults 18+) but the boundaries themselves are data, never
/// hardcoded thresholds in application code — see IAgeGroupResolver.
/// </summary>
public class AgeGroup
{
    public int Id { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public int MinAge { get; private set; }

    /// <summary>NULL = no upper bound (Adults, D-04).</summary>
    public int? MaxAge { get; private set; }

    /// <summary>Drives guardianship-mandatory rules, privacy rules (§32), and
    /// notification routing — not a cosmetic flag.</summary>
    public bool IsMinor { get; private set; }

    private AgeGroup() { }

    public AgeGroup(int id, string code, int minAge, int? maxAge, bool isMinor)
    {
        if (maxAge is not null && maxAge < minAge)
        {
            throw new ArgumentException("MaxAge cannot be less than MinAge.", nameof(maxAge));
        }

        Id = id;
        Code = code;
        MinAge = minAge;
        MaxAge = maxAge;
        IsMinor = isMinor;
    }
}
