namespace MVTeaches.Infrastructure.Files;

/// <summary>
/// Owner decision 2026-08-30 (receipt uploads): "أنواع صور مسموحة محددة
/// وحجم محدود قابل للضبط" — an admin-configurable size limit, never hardcoded
/// in a way that requires a redeploy to change (D-65's own "business
/// constants are admin-configurable" convention, extended here).
/// </summary>
public class FileStorageOptions
{
    public const string SectionName = "FileStorage";

    /// <summary>Where uploaded files live — OUTSIDE wwwroot always, on the
    /// same reasoning as DataProtectionKeysPath: a persistent volume in
    /// production, a local folder next to the binaries for local dev.
    /// Empty here = a folder next to the binaries.</summary>
    public string? StoragePath { get; set; }

    public long MaxSizeBytes { get; set; } = 5 * 1024 * 1024; // 5 MB, a safe default for a photographed/scanned receipt
}
