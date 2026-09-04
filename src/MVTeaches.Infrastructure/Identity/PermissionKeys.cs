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
/// screens (Payments, Payroll, Subscriptions). Stage 2A (2026-09-03, Review
/// Required — Authorization) added Students/StudentDetails/AssistedRegistration
/// and the Student Notes written-record feature inside StudentDetails. Stage
/// 2B (2026-09-03, same day, same classification) added
/// Teachers/Schedules/RescheduleSessions. Stage 2C (2026-09-03, same day,
/// same classification) added Compensation/PlacementTests/Certificates. Stage
/// 2D (2026-09-03, same day, same classification) adds
/// Dashboard/FinancialReport/Posters — see each key's own remarks below. This
/// closes the admin-permissions rollout: every admin screen now has granular
/// keys, and no admin page still falls back to the bare
/// Admin/SystemAdmin-role check.
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

    /// <summary>Owner decision 2026-09-05: managing discount codes is its own
    /// key, deliberately NOT folded into SubscriptionsManage. Selling a family
    /// a package and deciding what the centre's prices may be discounted to are
    /// different jobs, and the second one is the one that quietly costs money -
    /// so an admin can be given the first without the second.</summary>
    public const string PromoCodesManage = "Admin.PromoCodes.Manage";

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

    /// <summary>
    /// Stage 2B (2026-09-03, Review Required — Authorization): covers
    /// /Admin/Teachers' GET (the teacher list, pay rates in force, and each
    /// teacher's allowed levels). Also covers reading the "who is not ready
    /// for online sessions" flag surfaced on both this page and
    /// /Admin/Schedules — that flag is derived read-only data about
    /// teachers, not scheduling, so it travels with TeachersView wherever it
    /// is shown.
    /// </summary>
    public const string TeachersView = "Admin.Teachers.View";

    /// <summary>Covers every teacher-data-mutating handler on
    /// Teachers.cshtml.cs: registering a teacher, setting a pay rate,
    /// granting or revoking an allowed level, and deactivating/reactivating
    /// a teacher account. Pay ("how much") and levels ("what they may
    /// teach") are two different concerns per that page's own guide text,
    /// but the owner's Stage 2B scope names only View and Manage for
    /// Teachers, with no finer split.</summary>
    public const string TeachersManage = "Admin.Teachers.Manage";

    /// <summary>Covers /Admin/Schedules' GET (weekly classes, upcoming
    /// sessions, and each session's roster/student list shown in its "see
    /// the students" modal — all read-only) and /Admin/RescheduleSessions'
    /// GET (both step-by-step forms are visible, but see
    /// <see cref="SchedulesManage"/> for why submitting either is refused
    /// without it).</summary>
    public const string SchedulesView = "Admin.Schedules.View";

    /// <summary>Covers every schedule/session-mutating handler across both
    /// pages: creating a weekly class, enrolling a student into one,
    /// cancelling a session, reassigning a session's teacher, pausing or
    /// resuming a weekly class (Schedules.cshtml.cs) — and, on
    /// RescheduleSessions.cshtml.cs, moving an unattended lesson and
    /// approving a replacement (make-up) lesson, since both are schedule/
    /// enrollment writes exactly like the ones on Schedules.cshtml.cs, just
    /// reached from a different page.</summary>
    public const string SchedulesManage = "Admin.Schedules.Manage";

    /// <summary>
    /// Stage 2C (2026-09-03, Review Required — Authorization): covers
    /// /Admin/CompensationRequests' GET — the queue of student-submitted
    /// replacement requests, with each pending request's candidate
    /// replacement sessions and recently-resolved history, all read-only.
    /// </summary>
    public const string CompensationView = "Admin.Compensation.View";

    /// <summary>Covers CompensationRequests.cshtml.cs's two mutating
    /// handlers: approving a replacement request (which claims a seat on the
    /// chosen session) and rejecting one.</summary>
    public const string CompensationManage = "Admin.Compensation.Manage";

    /// <summary>
    /// Stage 2C (2026-09-03, Review Required — Authorization): covers
    /// /Admin/PlacementTests' GET — the list of test versions, an opened
    /// version's questions/choices and score ranges, and the pending retake
    /// requests queue, all read-only.
    /// </summary>
    public const string PlacementTestsView = "Admin.PlacementTests.View";

    /// <summary>Covers every mutating handler on PlacementTests.cshtml.cs:
    /// creating a draft version, adding/removing a question, adding/removing
    /// a score range, publishing a version, activating a published version,
    /// and approving or rejecting a retake request.</summary>
    public const string PlacementTestsManage = "Admin.PlacementTests.Manage";

    /// <summary>
    /// Stage 2C (2026-09-03, Review Required — Authorization): covers
    /// /Admin/Certificates' GET — each student's live progress toward a
    /// certificate and the list of certificates already issued, all
    /// read-only. Progress itself is never snapshotted (D-30/D-51); this key
    /// only gates who may look at it.
    /// </summary>
    public const string CertificatesView = "Admin.Certificates.View";

    /// <summary>Covers Certificates.cshtml.cs's two mutating handlers:
    /// issuing a certificate and revoking one.</summary>
    public const string CertificatesManage = "Admin.Certificates.Manage";

    /// <summary>
    /// Stage 2D (2026-09-03, Review Required — Authorization): covers
    /// /Admin/Dashboard's GET — every live count and figure on that page.
    /// The page has no mutating handler at all (it only ever reads), so
    /// there is no Dashboard.Manage key — View is the whole of it.
    /// </summary>
    public const string DashboardView = "Admin.Dashboard.View";

    /// <summary>
    /// Stage 2D (2026-09-03, Review Required — Authorization): covers
    /// /Admin/FinancialReport's GET — the revenue/payroll/profit figures,
    /// the month-over-month comparison, and the running-costs list, all
    /// read-only.
    /// </summary>
    public const string FinancialReportView = "Admin.FinancialReport.View";

    /// <summary>Covers FinancialReport.cshtml.cs's one mutating handler:
    /// recording a running cost (OnPostRecordExpenseAsync). Reading the
    /// report itself never requires this.</summary>
    public const string FinancialReportManage = "Admin.FinancialReport.Manage";

    /// <summary>
    /// Stage 2D (2026-09-03, Review Required — Authorization): covers
    /// /Admin/Posters' GET — the list of offer posters and the live preview
    /// of what a student would see, all read-only.
    /// </summary>
    public const string PostersView = "Admin.Posters.View";

    /// <summary>Covers Posters.cshtml.cs's two mutating handlers: creating
    /// or editing a poster (OnPostSaveAsync, which also covers uploading a
    /// replacement image) and showing/hiding one (OnPostToggleAsync).</summary>
    public const string PostersManage = "Admin.Posters.Manage";

    /// <summary>Every key, in the order shown on /Admin/AdminUsers — the
    /// single list both the permission-policy registration loop in
    /// Program.cs and the AdminUsers editing screen iterate, so a key can
    /// never be wired into one and forgotten in the other.</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        PaymentsView, PaymentsConfirm, PaymentsManage,
        PayrollView, PayrollApprove,
        SubscriptionsView, SubscriptionsManage,
        PromoCodesManage,
        StudentsView, StudentsManage,
        StudentNotesView, StudentNotesManage,
        TeachersView, TeachersManage,
        SchedulesView, SchedulesManage,
        CompensationView, CompensationManage,
        PlacementTestsView, PlacementTestsManage,
        CertificatesView, CertificatesManage,
        DashboardView,
        FinancialReportView, FinancialReportManage,
        PostersView, PostersManage,
    };
}
