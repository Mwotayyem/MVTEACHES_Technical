namespace MVTeaches.Domain.Catalog;

/// <summary>
/// Technical Study §11 — data, not an enum, so levels can be reordered/renamed
/// from the admin panel without a code change.
///
/// Deliberately has NO certificate-hours column: that threshold is a single
/// global setting (settings.CertificateRequiredHours, D-65/§19.5) read live for
/// every level equally — never stored per level and never snapshotted per
/// student. An earlier draft of the schema had a per-level column here; it
/// directly contradicted D-65 and was removed (see the study's §11 note dated
/// 2026-08-26). Do not reintroduce it.
/// </summary>
public class Level
{
    public int Id { get; private set; }

    /// <summary>A1..C2.</summary>
    public string Code { get; private set; } = string.Empty;

    public string NameAr { get; private set; } = string.Empty;
    public string NameEn { get; private set; } = string.Empty;
    public int SortOrder { get; private set; }

    public bool IsActive { get; private set; } = true;

    private Level() { }

    public Level(int id, string code, string nameAr, string nameEn, int sortOrder)
    {
        Id = id;
        Code = code;
        NameAr = nameAr;
        NameEn = nameEn;
        SortOrder = sortOrder;
    }
}
