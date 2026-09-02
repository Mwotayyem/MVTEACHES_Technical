namespace MVTeaches.Infrastructure.Identity;

/// <summary>
/// Security review 2026-09-02 (Review Required — Authorization), Stage 1 of
/// the admin-permissions rollout: until now every account in <see cref="RoleNames.Admin"/>
/// or <see cref="RoleNames.SystemAdmin"/> saw and could do exactly the same
/// things everywhere — the two roles were never actually distinguished
/// anywhere in the app (see the release-readiness audit finding on
/// <c>AdminOnlyDashboardAuthorizationFilter</c>). This is the first real
/// distinction: SystemAdmin becomes an unconditional Owner (see
/// <see cref="PermissionAuthorizationHandler"/> — it never even looks at a
/// claim), while a plain Admin starts with NOTHING beyond being able to sign
/// in to the Admin area, and must be granted each of these keys individually
/// by a SystemAdmin from <c>/Admin/AdminUsers</c>.
///
/// Deliberately scoped to the three financially-sensitive screens named in
/// this rollout's Stage 1 (Payments, Payroll, Subscriptions) — see the prior
/// permissions-audit report for the full 26-key design across every admin
/// screen. Every other admin page (Students, Teachers, Schedules,
/// Compensation, Certificates, PlacementTests, Posters, Dashboard) is
/// UNCHANGED by this pass and keeps allowing any Admin/SystemAdmin account,
/// exactly as before.
///
/// Each key is stored as an <see cref="System.Security.Claims.Claim"/> with
/// <see cref="ClaimType"/> as its type and one of these strings as its
/// value, in the ALREADY-EXISTING AspNetUserClaims table (this codebase's
/// MvTeachesDbContext already derives from IdentityDbContext&lt;..., long&gt;,
/// so the table has existed since the very first migration) — granting or
/// revoking a permission is therefore a plain claim add/remove, never a
/// schema change.
/// </summary>
public static class PermissionKeys
{
    /// <summary>The claim type every permission grant is stored under. A
    /// dedicated, namespaced type (rather than a bare role-like string) so a
    /// permission claim can never be confused with some other claim type
    /// Identity or a future feature might add to the same user.</summary>
    public const string ClaimType = "mvteaches:permission";

    public const string PaymentsView = "Admin.Payments.View";
    public const string PaymentsConfirm = "Admin.Payments.Confirm";

    /// <summary>Covers /Admin/PaymentMethods (which bank/CliQ/cash details a
    /// payer is shown) — a configuration action, deliberately distinct from
    /// PaymentsConfirm's day-to-day "money arrived, activate the package"
    /// decision. Also covers viewing that same configuration screen: unlike
    /// Payments (a long operational list, worth a separate View), this is a
    /// small settings screen with nothing meaningful to view without also
    /// being able to change it.</summary>
    public const string PaymentsManage = "Admin.Payments.Manage";

    public const string PayrollView = "Admin.Payroll.View";

    /// <summary>Covers every payroll-mutating action on /Admin/Payroll
    /// (verify/reject a declared delivery, open/aggregate/move-to-review/
    /// approve/mark-paid/close a period) — the owner's own Stage 1 scope
    /// names only View and Approve for payroll, with no finer split.</summary>
    public const string PayrollApprove = "Admin.Payroll.Approve";

    public const string SubscriptionsView = "Admin.Subscriptions.View";

    /// <summary>Covers every subscriptions-mutating action on
    /// /Admin/Subscriptions (create a pricing plan, purchase a subscription
    /// for a student, grant one for free).</summary>
    public const string SubscriptionsManage = "Admin.Subscriptions.Manage";

    /// <summary>Every Stage 1 key, in the order shown on /Admin/AdminUsers —
    /// the single list both the permission-policy registration loop in
    /// Program.cs and the AdminUsers editing screen iterate, so a key can
    /// never be wired into one and forgotten in the other.</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        PaymentsView, PaymentsConfirm, PaymentsManage,
        PayrollView, PayrollApprove,
        SubscriptionsView, SubscriptionsManage,
    };
}
