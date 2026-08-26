namespace MVTeaches.Domain.Catalog;

/// <summary>
/// Technical Study §13.1 — a first-class entity because teacher pay varies by
/// course (Conversation vs IELTS, D-27). MVP seeds exactly one row
/// (General English, D-41) but the entity must exist for payroll to work at all.
/// </summary>
public class Course
{
    public long Id { get; private set; }

    public string Code { get; private set; } = string.Empty;
    public string NameAr { get; private set; } = string.Empty;
    public string NameEn { get; private set; } = string.Empty;

    public bool IsLeveled { get; private set; } = true;
    public bool GrantsCertificate { get; private set; } = true;
    public bool IsActive { get; private set; } = true;

    private Course() { }

    public Course(string code, string nameAr, string nameEn, bool isLeveled = true, bool grantsCertificate = true)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Code is required.", nameof(code));
        }

        Code = code;
        NameAr = nameAr;
        NameEn = nameEn;
        IsLeveled = isLeveled;
        GrantsCertificate = grantsCertificate;
    }
}
