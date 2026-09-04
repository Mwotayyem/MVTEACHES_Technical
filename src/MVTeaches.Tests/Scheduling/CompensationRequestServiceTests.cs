using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.Attendance;
using MVTeaches.Application.Scheduling;
using MVTeaches.Domain.Attendance;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Common;
using MVTeaches.Domain.Ledger;
using MVTeaches.Domain.Notifications;
using MVTeaches.Domain.People;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Domain.Subscriptions;
using MVTeaches.Infrastructure.Attendance;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using MVTeaches.Infrastructure.Scheduling;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace MVTeaches.Tests.Scheduling;

/// <summary>
/// Owner correction (student self-service booking, 2026-08-28): a student
/// requests their own replacement for a session finalized as a no-show; an
/// admin approves (reusing IEnrollmentService.ApproveReplacementLessonAsync
/// verbatim — no duplicated granting logic) or rejects. The notification
/// outbox item must exist ONLY after a successful approval, never at
/// request-submission time and never on rejection.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class CompensationRequestServiceTests
{
    private readonly TestDatabaseFixture _fixture;
    private static long _idSeed = 90_000_000;

    public CompensationRequestServiceTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static long NextId() => Interlocked.Increment(ref _idSeed);

    private static string TwoLetterCode(long seed)
    {
        var n = (int)(seed % 676);
        return string.Concat((char)('A' + n / 26), (char)('A' + n % 26));
    }

    private static int? _sharedCountryId;

    private async Task<int> GetOrSeedCountryAsync(MvTeachesDbContext db)
    {
        if (_sharedCountryId is { } existing)
        {
            return existing;
        }

        // Retry-safe: the 2-letter code space (676 combinations) is shared
        // across every test class in this run via the same NextId()-derived
        // TwoLetterCode pattern, so a residue collision with some OTHER
        // class's own seed range is a real, previously-hit flake (see this
        // session's history) rather than a theoretical one. Catching the
        // real unique-violation and retrying with a fresh NextId() is the
        // same self-correcting pattern this codebase's own production code
        // uses everywhere else, and it removes the need to hand-verify no
        // other test class's arithmetic happens to collide.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var countryId = (int)NextId();
            db.Countries.Add(new Country(countryId, TwoLetterCode(countryId), "دولة", "Country", "JOD", "+962", "Asia/Amman"));
            try
            {
                await db.SaveChangesAsync();
                _sharedCountryId = countryId;
                return countryId;
            }
            catch (Microsoft.EntityFrameworkCore.DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
            {
                db.ChangeTracker.Clear();
            }
        }

        throw new InvalidOperationException("Could not find a free 2-letter country code after 10 attempts.");
    }

    private static async Task<long> CreateUserAsync(MvTeachesDbContext db, string label)
    {
        var user = new ApplicationUser
        {
            UserName = $"{label}-{Guid.NewGuid():N}",
            NormalizedUserName = $"{label}-{Guid.NewGuid():N}".ToUpperInvariant(),
            Email = $"{Guid.NewGuid():N}@test.mvteaches.local",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private record Fixture(long StudentId, long StudentUserId, int LevelId, long CourseId, int AgeGroupId, int CountryId, long TeacherId);

    private async Task<Fixture> SeedStudentAsync(MvTeachesDbContext db)
    {
        var countryId = await GetOrSeedCountryAsync(db);
        var courseId = NextId();
        var levelId = (int)NextId();
        var ageGroupId = (int)NextId();
        var studentUserId = await CreateUserAsync(db, "student");
        var teacherUserId = await CreateUserAsync(db, "teacher");

        db.Courses.Add(new Course("C" + courseId, "دورة", "Course"));
        db.Levels.Add(new Level(levelId, "L" + levelId, "مستوى", "Level", levelId));
        db.AgeGroups.Add(new AgeGroup(ageGroupId, "A" + ageGroupId, 5, 60, true));
        var teacher = new Teacher(teacherUserId, "Teacher", "Asia/Amman");
        db.Teachers.Add(teacher);
        var student = new Student(countryId, "Ahmad", new LocalDate(2000, 1, 1), studentUserId);
        db.Students.Add(student);
        await db.SaveChangesAsync();

        return new Fixture(student.Id, studentUserId, levelId, courseId, ageGroupId, countryId, teacher.Id);
    }

    private ClassSession NewSession(Fixture fx, Instant start, int durationMinutes = 60, SessionType sessionType = SessionType.Group) =>
        new(fx.CountryId, null, fx.CourseId, fx.LevelId, fx.AgeGroupId, fx.TeacherId, start, start.Plus(Duration.FromMinutes(durationMinutes)),
            "Asia/Amman", "10:00", sessionType, start.Minus(Duration.FromDays(1)));

    /// <summary>Seeds a session already ended with no Join and no attendance
    /// row yet, then finalizes it via the real SessionFinalizationService —
    /// the exact mechanism that produces a genuine no-show in production,
    /// not a hand-rolled AttendanceRecord.</summary>
    private async Task<long> SeedConfirmedNoShowAsync(MvTeachesDbContext db, Fixture fx, Instant now)
    {
        var session = NewSession(fx, now.Minus(Duration.FromMinutes(90)));
        db.ClassSessions.Add(session);
        await db.SaveChangesAsync();
        db.SessionEnrollments.Add(new SessionEnrollment(session.Id, fx.StudentId, fx.AgeGroupId, fx.StudentUserId, now.Minus(Duration.FromDays(1))));
        await db.SaveChangesAsync();

        await new SessionFinalizationService(db, new FakeClock(now)).FinalizeEndedSessionsAsync(CancellationToken.None);
        return session.Id;
    }

    private ICompensationRequestService CreateService(MvTeachesDbContext db, Instant now) =>
        new CompensationRequestService(db, new EnrollmentService(db, new FakeClock(now)), new FakeClock(now));

    [Fact]
    public async Task Requesting_a_replacement_for_a_session_that_is_not_a_no_show_is_rejected()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var fx = await SeedStudentAsync(db);
        var session = NewSession(fx, now.Plus(Duration.FromDays(1))); // never happened yet
        db.ClassSessions.Add(session);
        await db.SaveChangesAsync();

        var result = await CreateService(db, now).RequestReplacementAsync(fx.StudentId, session.Id, "reason", fx.StudentUserId, CancellationToken.None);

        Assert.Equal(SubmitCompensationRequestOutcome.NotANoShow, result.Outcome);
    }

    [Fact]
    public async Task A_student_cannot_request_a_replacement_using_another_students_account()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var fx = await SeedStudentAsync(db);
        var sessionId = await SeedConfirmedNoShowAsync(db, fx, now);
        var attackerUserId = await CreateUserAsync(db, "attacker");

        var result = await CreateService(db, now).RequestReplacementAsync(fx.StudentId, sessionId, "reason", attackerUserId, CancellationToken.None);

        Assert.Equal(SubmitCompensationRequestOutcome.Unauthorized, result.Outcome);
        Assert.False(await db.CompensationRequests.AnyAsync(r => r.OriginalSessionId == sessionId));
    }

    [Fact]
    public async Task Requesting_a_replacement_after_a_confirmed_no_show_succeeds()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var fx = await SeedStudentAsync(db);
        var sessionId = await SeedConfirmedNoShowAsync(db, fx, now);

        var result = await CreateService(db, now).RequestReplacementAsync(fx.StudentId, sessionId, "traffic", fx.StudentUserId, CancellationToken.None);

        Assert.Equal(SubmitCompensationRequestOutcome.Submitted, result.Outcome);
        Assert.NotNull(result.RequestId);

        // No notification exists yet — only submitted, not approved.
        Assert.Equal(0, await db.NotificationOutboxItems.CountAsync(n => n.RecipientUserId == fx.StudentUserId));
    }

    [Fact]
    public async Task A_duplicate_pending_request_for_the_same_no_show_is_rejected()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var fx = await SeedStudentAsync(db);
        var sessionId = await SeedConfirmedNoShowAsync(db, fx, now);
        var service = CreateService(db, now);

        var first = await service.RequestReplacementAsync(fx.StudentId, sessionId, "first", fx.StudentUserId, CancellationToken.None);
        var second = await service.RequestReplacementAsync(fx.StudentId, sessionId, "second", fx.StudentUserId, CancellationToken.None);

        Assert.Equal(SubmitCompensationRequestOutcome.Submitted, first.Outcome);
        Assert.Equal(SubmitCompensationRequestOutcome.DuplicateRequest, second.Outcome);

        await using var verify = _fixture.CreateContext();
        Assert.Equal(1, await verify.CompensationRequests.CountAsync(r => r.OriginalSessionId == sessionId));
    }

    [Fact]
    public async Task Approving_a_request_grants_a_free_replacement_and_creates_a_notification_only_now()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var fx = await SeedStudentAsync(db);
        var originalSessionId = await SeedConfirmedNoShowAsync(db, fx, now);

        var service = CreateService(db, now);
        var request = await service.RequestReplacementAsync(fx.StudentId, originalSessionId, "reason", fx.StudentUserId, CancellationToken.None);
        Assert.Equal(0, await db.NotificationOutboxItems.CountAsync(n => n.RecipientUserId == fx.StudentUserId)); // still nothing before approval

        var replacement = NewSession(fx, now.Plus(Duration.FromDays(3)));
        db.ClassSessions.Add(replacement);
        await db.SaveChangesAsync();

        var approve = await service.ApproveAsync(request.RequestId!.Value, replacement.Id, approvedByUserId: NextId(), CancellationToken.None);
        Assert.Equal(ResolveCompensationRequestOutcome.Approved, approve.Outcome);

        await using var verify = _fixture.CreateContext();
        var requestRow = await verify.CompensationRequests.SingleAsync(r => r.Id == request.RequestId);
        Assert.Equal(CompensationRequestStatus.Approved, requestRow.Status);
        Assert.Equal(replacement.Id, requestRow.ReplacementSessionId);

        Assert.True(await verify.SessionEnrollments.AnyAsync(
            e => e.SessionId == replacement.Id && e.StudentId == fx.StudentId && e.CompensatesForSessionId == originalSessionId));

        // The notification now exists — only after approval, exactly once,
        // on the WhatsApp channel, addressed to the student's own account.
        var notification = await verify.NotificationOutboxItems.SingleAsync(n => n.RecipientUserId == fx.StudentUserId);
        Assert.Equal(NotificationEvent.ReplacementLessonApproved, notification.Event);
        Assert.Equal(NotificationChannel.WhatsApp, notification.Channel);
        Assert.Equal(fx.StudentUserId, notification.RecipientUserId);
        Assert.Contains("Ahmad", notification.PayloadJson);

        // Free Join: the replacement's own Join must not draw a second consumption.
        var atReplacementTime = replacement.StartsAtUtc;
        var join = await new JoinAttendanceService(verify, new FakeClock(atReplacementTime))
            .JoinAsync(new JoinAttendanceRequest(replacement.Id, fx.StudentId, fx.StudentUserId), CancellationToken.None);
        Assert.Equal(JoinOutcome.Recorded, join.Outcome);
        Assert.Equal(0, await verify.EntitlementLedgerEntries.CountAsync(l => l.SessionId == replacement.Id));
    }

    /// <summary>Owner decision 2026-08-30 rule 9 supersedes this test's
    /// original name/assertion ("and no notification") — a rejection now
    /// fires its own CompensationRejected notification, distinct from
    /// ReplacementLessonApproved's own approval-only event.</summary>
    [Fact]
    public async Task Rejecting_a_request_creates_no_replacement_but_does_notify_the_student()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var fx = await SeedStudentAsync(db);
        var originalSessionId = await SeedConfirmedNoShowAsync(db, fx, now);

        var service = CreateService(db, now);
        var request = await service.RequestReplacementAsync(fx.StudentId, originalSessionId, "reason", fx.StudentUserId, CancellationToken.None);

        var reject = await service.RejectAsync(request.RequestId!.Value, "not eligible", rejectedByUserId: NextId(), CancellationToken.None);
        Assert.Equal(ResolveCompensationRequestOutcome.Rejected, reject.Outcome);

        await using var verify = _fixture.CreateContext();
        var requestRow = await verify.CompensationRequests.SingleAsync(r => r.Id == request.RequestId);
        Assert.Equal(CompensationRequestStatus.Rejected, requestRow.Status);
        Assert.Equal("not eligible", requestRow.RejectionReason);
        Assert.Equal(1, await verify.NotificationOutboxItems.CountAsync(
            n => n.RecipientUserId == fx.StudentUserId && n.Event == MVTeaches.Domain.Notifications.NotificationEvent.CompensationRejected));
        Assert.False(await verify.SessionEnrollments.AnyAsync(e => e.CompensatesForSessionId == originalSessionId));
    }

    [Fact]
    public async Task Approving_a_replacement_of_a_different_level_is_rejected()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var fx = await SeedStudentAsync(db);
        var originalSessionId = await SeedConfirmedNoShowAsync(db, fx, now);

        var otherLevelId = (int)NextId();
        db.Levels.Add(new Level(otherLevelId, "L" + otherLevelId, "آخر", "Other", otherLevelId));
        await db.SaveChangesAsync();
        var wrongLevelSession = new ClassSession(fx.CountryId, null, fx.CourseId, otherLevelId, fx.AgeGroupId, fx.TeacherId,
            now.Plus(Duration.FromDays(3)), now.Plus(Duration.FromDays(3)).Plus(Duration.FromMinutes(60)),
            "Asia/Amman", "10:00", SessionType.Group, now);
        db.ClassSessions.Add(wrongLevelSession);
        await db.SaveChangesAsync();

        var service = CreateService(db, now);
        var request = await service.RequestReplacementAsync(fx.StudentId, originalSessionId, "reason", fx.StudentUserId, CancellationToken.None);

        var approve = await service.ApproveAsync(request.RequestId!.Value, wrongLevelSession.Id, approvedByUserId: NextId(), CancellationToken.None);

        Assert.Equal(ResolveCompensationRequestOutcome.ReplacementSessionLevelMismatch, approve.Outcome);
        Assert.Equal(0, await db.NotificationOutboxItems.CountAsync(n => n.RecipientUserId == fx.StudentUserId));
    }

    /// <summary>Owner report 2026-09-05: "لا يختار حصة من دورة أو مستوى غلط".
    /// The admin screen has always filtered its candidate list on course, level
    /// and lesson type together — but the service checked only the level, so
    /// that filter was the entire guard. A posted id for another course's
    /// session at the same level would have enrolled the student in a subject
    /// they were never placed in.</summary>
    [Fact]
    public async Task Approving_a_replacement_in_a_different_course_is_rejected()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var fx = await SeedStudentAsync(db);
        var originalSessionId = await SeedConfirmedNoShowAsync(db, fx, now);

        // Same level, same teacher, same time — a DIFFERENT course. Nothing but
        // the course distinguishes it, so nothing but the course check can
        // refuse it.
        var otherCourse = new MVTeaches.Domain.Catalog.Course($"COMP-{NextId()}", "دورة أخرى", "Other Course");
        db.Courses.Add(otherCourse);
        await db.SaveChangesAsync();

        var wrongCourseSession = new ClassSession(fx.CountryId, null, otherCourse.Id, fx.LevelId, fx.AgeGroupId, fx.TeacherId,
            now.Plus(Duration.FromDays(4)), now.Plus(Duration.FromDays(4)).Plus(Duration.FromMinutes(60)),
            "Asia/Amman", "10:00", SessionType.Group, now);
        db.ClassSessions.Add(wrongCourseSession);
        await db.SaveChangesAsync();

        var service = CreateService(db, now);
        var request = await service.RequestReplacementAsync(fx.StudentId, originalSessionId, "reason", fx.StudentUserId, CancellationToken.None);

        var approve = await service.ApproveAsync(request.RequestId!.Value, wrongCourseSession.Id, approvedByUserId: NextId(), CancellationToken.None);

        Assert.Equal(ResolveCompensationRequestOutcome.ReplacementSessionCourseMismatch, approve.Outcome);

        // Refused means refused: no enrollment, no notification, and the
        // request is still waiting for a real replacement.
        Assert.Equal(0, await db.SessionEnrollments.CountAsync(e => e.SessionId == wrongCourseSession.Id));
        Assert.Equal(0, await db.NotificationOutboxItems.CountAsync(n => n.RecipientUserId == fx.StudentUserId));
        var stillPending = await db.CompensationRequests.SingleAsync(r => r.Id == request.RequestId!.Value);
        Assert.Equal(CompensationRequestStatus.Pending, stillPending.Status);
    }

    /// <summary>The same rule for lesson TYPE: a Group session is not a
    /// replacement for a missed Private one, whatever the course and level
    /// say.</summary>
    [Fact]
    public async Task Approving_a_replacement_of_a_different_lesson_type_is_rejected()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var fx = await SeedStudentAsync(db);
        var originalSessionId = await SeedConfirmedNoShowAsync(db, fx, now);

        var privateSession = new ClassSession(fx.CountryId, null, fx.CourseId, fx.LevelId, fx.AgeGroupId, fx.TeacherId,
            now.Plus(Duration.FromDays(5)), now.Plus(Duration.FromDays(5)).Plus(Duration.FromMinutes(60)),
            "Asia/Amman", "11:00", SessionType.Private, now);
        db.ClassSessions.Add(privateSession);
        await db.SaveChangesAsync();

        var service = CreateService(db, now);
        var request = await service.RequestReplacementAsync(fx.StudentId, originalSessionId, "reason", fx.StudentUserId, CancellationToken.None);

        var approve = await service.ApproveAsync(request.RequestId!.Value, privateSession.Id, approvedByUserId: NextId(), CancellationToken.None);

        Assert.Equal(ResolveCompensationRequestOutcome.ReplacementSessionCourseMismatch, approve.Outcome);
        Assert.Equal(0, await db.SessionEnrollments.CountAsync(e => e.SessionId == privateSession.Id));
    }

    [Fact]
    public async Task Approving_an_already_resolved_request_is_rejected()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        await using var db = _fixture.CreateContext();
        var fx = await SeedStudentAsync(db);
        var originalSessionId = await SeedConfirmedNoShowAsync(db, fx, now);

        var service = CreateService(db, now);
        var request = await service.RequestReplacementAsync(fx.StudentId, originalSessionId, "reason", fx.StudentUserId, CancellationToken.None);
        await service.RejectAsync(request.RequestId!.Value, "no", rejectedByUserId: NextId(), CancellationToken.None);

        var replacement = NewSession(fx, now.Plus(Duration.FromDays(3)));
        db.ClassSessions.Add(replacement);
        await db.SaveChangesAsync();

        var approve = await service.ApproveAsync(request.RequestId!.Value, replacement.Id, approvedByUserId: NextId(), CancellationToken.None);
        Assert.Equal(ResolveCompensationRequestOutcome.RequestNotPending, approve.Outcome);
    }
}
