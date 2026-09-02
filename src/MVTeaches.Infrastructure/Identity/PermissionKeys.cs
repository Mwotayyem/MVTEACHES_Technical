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
/// Stage 1 was deliberately scoped to the three financially-sensitive
/// screens (Payments, Payroll, Subscriptions). Stage 2 (2026-09-03, Review
/// Required — Authorization) adds Students/StudentDetails/AssistedRegistration
/// and the Student Notes written-record feature inside StudentDetails — see
/// each key's own remarks below. Every other admin page (Teachers, Schedules,
/// Compensation, Certificates, PlacementTests, Posters, Dashboard) remains
/// UNCHANGED and keeps allowing any Admin/SystemAdmin account, exactly as
/// before — see the prior permissions-audit report for the full design
/// across every admin screen.
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

    /// <summary>
    /// Stage 2 (2026-09-03, Review Required — Authorization): the register
    /// and the student profile. Covers /Admin/Students (list + GET) and
    /// /Admin/StudentDetails (profile + GET) — a plain Admin with only this
    /// key can read the whole register and every student's file but cannot
    /// change anything on either page.
    /// </summary>
    public const string StudentsView = "Admin.Students.View";

    /// <summary>Covers every student-data-mutating action reachable from
    /// Students.cshtml.cs (registering a guardian or a student, linking a
    /// guardian, confirming registration details, assigning a level) — and
    /// the whole of /Admin/AssistedRegistration, whose every handler
    /// (including the draft-package purchase and manual-payment/transfer
    /// steps embedded in that one guided flow) exists only to carry a new
    /// family through registration, so that page requires this key for its
    /// GET as well as every POST, rather than a separate View tier of its
    /// own.</summary>
    public const string StudentsManage = "Admin.Students.Manage";

    /// <summary>Gates only the WRITTEN notes an admin explicitly types about
    /// a student (the <c>StudentNote</c> entity, shown as "Notes" on the
    /// profile's Written record tab) — never the system-derived history
    /// (level-change/package/compensation/payment reasons) on that same tab,
    /// which is ordinary student data already covered by
    /// <see cref="StudentsView"/>. Without this key, an Admin cannot see that
    /// a written note exists at all, not merely its contents.</summary>
    public const string StudentNotesView = "Admin.StudentNotes.View";

    /// <summary>Covers adding a written note (today the only operation that
    /// exists — StudentNotes are append-only, with no edit or delete
    /// anywhere in the app; this key is named Manage rather than "Add" only
    /// so it needs no rename if an edit/delete is ever added later).</summary>
    public const string StudentNotesManage = "Admin.StudentNotes.Manage";

    /// <summary>Every key, in the order shown on /Admin/AdminUsers — the
    /// single list both the permission-policy registration loop in
    /// Program.cs and the AdminUsers editing screen iterate, so a key can
    /// never be wired into one and forgotten in the other.</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        PaymentsView, PaymentsConfirm, PaymentsManage,
        PayrollView, PayrollApprove,
        SubscriptionsView, SubscriptionsManage,
        StudentsView, StudentsManage,
        StudentNotesView, StudentNotesManage,
    };
}
