namespace MVTeaches.Domain.Notifications;

/// <summary>
/// Technical Study §30.1 — the CLOSED events matrix, transcribed exactly.
/// Do NOT add a member without an approved decision. In particular:
///   - "Attendance not recorded after 24h" is deliberately ABSENT — D-83 removed
///     it (self-service Join has nothing to forget).
///   - "Course completed" and "New student starts tomorrow" are deliberately
///     ABSENT — the owner explicitly declined to add them (2026-08-25 session).
/// </summary>
public enum NotificationEvent
{
    RegistrationOtp,
    AccountConfirmed,
    LevelAssigned,
    SubscriptionConfirmed,
    AdminCreatedSubscription,
    OutstandingBalance,
    OutstandingBalanceReminder,
    SessionReminderStudent,
    SessionReminderTeacher,
    TeacherPrep15Min,
    ZoomLink5Min,
    SessionCancelledOrMoved,
    HomeworkPosted,
    HomeworkGraded,
    CertificateIssued,
    SubscriptionEndingSoon,
    SubscriptionExpiredWithBalanceAdminAlert,
    MakeUpExpiringSoon,
    StudentWithoutLevelAdminDigest,
    PaymentPendingApprovalAdminAlert,
    AnnouncementPosted,

    /// <summary>Owner-approved 2026-08-28 addition (student self-service
    /// booking + compensation-request correction): fired ONLY after an admin
    /// successfully confirms a specific replacement session for a student's
    /// compensation request — never at request-submission time, never
    /// speculatively. Distinct from SessionCancelledOrMoved, which is about
    /// the ORIGINAL session changing, not a replacement being granted.</summary>
    ReplacementLessonApproved,

    /// <summary>Owner decision 2026-08-30 rule 9 addition: fired when a
    /// student successfully books a session through the self-service booking
    /// flow (StudentBookingService.BookSessionAsync) — distinct from
    /// SubscriptionConfirmed (a purchase) and from the Join-time attendance
    /// event, since booking itself moves no entitlement minutes (D-36) and
    /// deserves its own confirmation regardless.</summary>
    BookingConfirmed,

    /// <summary>Owner decision 2026-08-30 rule 9 addition: the rejection half
    /// of the compensation-request cycle ReplacementLessonApproved already
    /// covers for approval — fired only when an admin rejects a student's
    /// compensation request, carrying the mandatory rejection reason.</summary>
    CompensationRejected,

    /// <summary>Owner decision 2026-08-30 (manual payment methods, Section 8):
    /// fired when an admin rejects a Pending payment (PaymentService.RejectAsync)
    /// — the payer's own "needs correction" signal, carrying the mandatory
    /// rejection reason but never the bank details or the receipt image
    /// itself. Distinct from SubscriptionConfirmed, which only ever fires on
    /// success.</summary>
    PaymentNeedsCorrection,
}

public enum NotificationChannel
{
    WhatsApp,
    Email,
    InApp,
}
