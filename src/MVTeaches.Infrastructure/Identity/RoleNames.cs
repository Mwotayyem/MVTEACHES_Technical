namespace MVTeaches.Infrastructure.Identity;

/// <summary>
/// Technical Study §6 (roles/permissions matrix) and D-28/D-01/D-02. A closed
/// set — do not add a role without a corresponding row in the study's
/// permissions matrix; an undocumented role is exactly the kind of "new
/// architecture" the master engineering prompt warns against.
/// </summary>
public static class RoleNames
{
    /// <summary>Full administrative control (§6/§34, D-34: 20+ areas under admin control).</summary>
    public const string Admin = "Admin";

    /// <summary>§18.3 rule 2: elevated over Admin for one specific reason — a
    /// full-system operator role for irreversible actions (migration rollback,
    /// payroll period force-close). Distinct from Admin per D-58 rule 2
    /// ("الترحيل حصريًا لـ SystemAdmin").</summary>
    public const string SystemAdmin = "SystemAdmin";

    public const string Teacher = "Teacher";
    public const string Guardian = "Guardian";
    public const string Student = "Student";

    public static readonly IReadOnlyList<string> All = new[] { Admin, SystemAdmin, Teacher, Guardian, Student };
}
