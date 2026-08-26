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
}

public enum NotificationChannel
{
    WhatsApp,
    Email,
    InApp,
}
