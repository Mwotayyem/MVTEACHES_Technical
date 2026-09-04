using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using MVTeaches.Application.Attendance;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using MVTeaches.Web.Resources;
using NodaTime;

namespace MVTeaches.Web.Pages.Guardian;

/// <summary>
/// D-83's actual front door — the single highest-risk, most heavily-tested
/// piece of backend in this repository (IJoinAttendanceService, 9 dedicated
/// tests plus a real concurrency race test) had no UI anywhere until this
/// page: a guardian pressing Join for a child with no independent login is
/// explicitly the primary case D-83 was designed around (D-02/D-03), and it
/// could previously only be exercised by calling the service directly from
/// a test. This page calls IJoinAttendanceService and nothing else — every
/// rule (entitlement check, enrollment check, guardian-authorization check,
/// idempotent double-press) is already enforced there.
/// </summary>
[Authorize(Roles = RoleNames.Guardian)]
public class MyChildrenModel : PageModel
{
    private readonly MvTeachesDbContext _db;
    private readonly IJoinAttendanceService _join;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IClock _clock;
    private readonly IStringLocalizer<SharedResource> _localizer;

    /// <summary>Owner decision 2026-09-04: a guardian adds their own children
    /// here rather than phoning the centre to have it typed in for them.</summary>
    private readonly MVTeaches.Application.People.ISelfRegistrationService _selfRegistration;

    /// <summary>Owner decision 2026-09-04: a guardian books their child's
    /// lessons. Same service the student's own screen uses — every level,
    /// age-group, package-capacity and seat rule lives there and is asked the
    /// same questions whoever pressed the button.</summary>
    private readonly MVTeaches.Application.Scheduling.IStudentBookingService _booking;

    public MyChildrenModel(MvTeachesDbContext db, IJoinAttendanceService join, UserManager<ApplicationUser> userManager, IClock clock,
        IStringLocalizer<SharedResource> localizer, MVTeaches.Application.People.ISelfRegistrationService selfRegistration,
        MVTeaches.Application.Scheduling.IStudentBookingService booking)
    {
        _db = db;
        _join = join;
        _userManager = userManager;
        _clock = clock;
        _localizer = localizer;
        _selfRegistration = selfRegistration;
        _booking = booking;
    }

    /// <summary>Owner decision 2026-09-04: the guardian's own add-a-child form.
    /// Deliberately small — a name, a birth date, a country. No level, no
    /// package and no login: a child gets those from the centre, and offering
    /// them here would be offering something this page cannot honour.</summary>
    public class NewChildInput
    {
        [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Enter the full name.")]
        public string FullName { get; set; } = string.Empty;

        /// <summary>Nullable so an untouched picker fails [Required] rather
        /// than passing as 0001-01-01 — the binding trap already documented on
        /// the admin registration forms.</summary>
        [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Enter the date of birth.")]
        public DateOnly? DateOfBirth { get; set; }

        [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Choose a country.")]
        public int? CountryId { get; set; }

        /// <summary>Optional: the number the centre actually calls about this
        /// child is the guardian's own. Recorded only if the family has one.</summary>
        [System.ComponentModel.DataAnnotations.Phone(ErrorMessage = "This is not a valid phone number.")]
        public string? PhoneNumber { get; set; }
    }

    [BindProperty]
    public NewChildInput NewChild { get; set; } = new();

    public IReadOnlyList<MVTeaches.Domain.Catalog.Country> Countries { get; set; } =
        Array.Empty<MVTeaches.Domain.Catalog.Country>();

    public bool IsArabic => System.Globalization.CultureInfo.CurrentUICulture
        .TwoLetterISOLanguageName.Equals("ar", StringComparison.OrdinalIgnoreCase);

    public string DisplayCountry(MVTeaches.Domain.Catalog.Country country) =>
        IsArabic ? country.NameAr : country.NameEn;

    /// <summary>Owner decision 2026-09-04. Every rule lives in
    /// ISelfRegistrationService.AddOwnChildAsync — which guardian this is comes
    /// from the signed-in account, never from the form, so no guardian can add
    /// a child to somebody else's family.</summary>
    public async Task<IActionResult> OnPostAddChildAsync()
    {
        ModelState.Clear();
        if (!TryValidateModel(NewChild, nameof(NewChild)))
        {
            await LoadAsync();
            return Page();
        }

        var actingUserId = long.Parse(_userManager.GetUserId(User)!);
        var dob = NewChild.DateOfBirth!.Value;
        var result = await _selfRegistration.AddOwnChildAsync(actingUserId, NewChild.FullName,
            new LocalDate(dob.Year, dob.Month, dob.Day), NewChild.PhoneNumber, NewChild.CountryId!.Value,
            HttpContext.RequestAborted);

        StatusMessage = result.Outcome == MVTeaches.Application.People.AddOwnChildOutcome.Added
            ? _localizer["{0} was added. The centre will set their level — until then you cannot buy a package or book a lesson for them.", NewChild.FullName].Value
            : null;
        ErrorMessage = result.Outcome switch
        {
            MVTeaches.Application.People.AddOwnChildOutcome.NotAGuardian =>
                _localizer["This account is not linked to a guardian record yet — please contact the centre."].Value,
            MVTeaches.Application.People.AddOwnChildOutcome.CountryNotAvailable =>
                _localizer["The centre does not currently operate in that country."].Value,
            _ => null,
        };

        if (result.Outcome == MVTeaches.Application.People.AddOwnChildOutcome.Added)
        {
            NewChild = new NewChildInput(); // a cleared form, ready for the next child
        }

        await LoadAsync();
        return Page();
    }

    /// <summary>Owner decision 2026-09-04, two changes to this row.
    /// <para>Time zone: <paramref name="StudentTimeZone"/> is the child's own
    /// country's DefaultTimeZone, and is what the guardian is shown. Nothing
    /// about STORAGE changed — the session is still stamped in UTC and still
    /// carries its own ScheduleTimeZone, which is kept here and shown beside
    /// the local time whenever the two differ, so a parent phoning the centre
    /// about "the 5 o'clock lesson" and the centre reading its own schedule
    /// are still talking about the same moment.</para>
    /// <para>Join context: a guardian with several children was being asked to
    /// press one of several identical-looking Join buttons. TeacherName is
    /// carried for that reason — the child's name, the teacher, the time, and
    /// the level together make each row identifiable at a glance. None of it
    /// touches what Join DOES; every press still names exactly one
    /// studentId and one sessionId.</para></summary>
    public record ChildSessionRow(long StudentId, string StudentName, long SessionId, Instant StartsAtUtc,
        string ScheduleTimeZone, string StudentTimeZone, string CourseName, string LevelCode,
        string TeacherName, ClassSessionStatus SessionStatus, bool AlreadyPresent, bool CanJoin);

    public IReadOnlyList<ChildSessionRow> UpcomingSessions { get; set; } = Array.Empty<ChildSessionRow>();

    /// <summary>Owner decision 2026-09-04: a lesson this child could be booked
    /// into. Narrowed to what actually applies to them — a (course, level) pair
    /// they currently hold, still Scheduled, still in the future — and carrying
    /// enough to tell one from another without opening anything.
    /// <para><paramref name="AlreadyBooked"/> and a full session are shown
    /// rather than hidden: a lesson that quietly disappears from the list reads
    /// as a mistake, and "your child is already in this one" is the answer the
    /// guardian was looking for.</para></summary>
    public record BookableSessionRow(long StudentId, string StudentName, long SessionId, Instant StartsAtUtc,
        string ScheduleTimeZone, string StudentTimeZone, string CourseName, string LevelCode, string TeacherName,
        int DurationMinutes, int SeatsTaken, int Capacity, bool AlreadyBooked);

    public IReadOnlyList<BookableSessionRow> BookableSessions { get; set; } = Array.Empty<BookableSessionRow>();

    /// <summary>Who this guardian's children are, independently of whether any
    /// of them happens to have a session this week — the previous page showed
    /// nothing at all in that (very common) case.</summary>
    public record ChildRow(long StudentId, string FullName, string? LevelCode,
        MVTeaches.Domain.People.StudentStatus Status, int UpcomingSessionCount);

    public IReadOnlyList<ChildRow> Children { get; set; } = Array.Empty<ChildRow>();

    /// <summary>True only when this Guardian-role account has no linked
    /// Guardian row yet — an admin data-entry gap (see /Admin/Students),
    /// not something this page can fix itself.</summary>
    public bool NoGuardianProfileLinked { get; set; }

    public string? StatusMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostJoinAsync(long sessionId, long studentId)
    {
        var actingUserId = long.Parse(_userManager.GetUserId(User)!);
        var result = await _join.JoinAsync(new JoinAttendanceRequest(sessionId, studentId, actingUserId), HttpContext.RequestAborted);

        StatusMessage = result.Outcome switch
        {
            JoinOutcome.Recorded => _localizer["Attendance recorded — the session's full duration has been drawn from the subscription."].Value,
            JoinOutcome.AlreadyRecorded => _localizer["Already marked present for this session."].Value,
            _ => null,
        };
        ErrorMessage = result.Outcome switch
        {
            JoinOutcome.Unauthorized => _localizer["This child is not enrolled in that session, or this account is not their guardian."].Value,
            JoinOutcome.SessionNotFound => _localizer["Session not found."].Value,
            JoinOutcome.SessionNotYetJoinable => _localizer["This session hasn't started yet."].Value,
            JoinOutcome.InsufficientBalance => _localizer["No subscription has enough remaining balance to cover this session."].Value,
            _ => null,
        };

        await LoadAsync();
        return Page();
    }

    /// <summary>Owner decision 2026-09-04. A child registered by their
    /// guardian has no login by design, so before this there was no path at all
    /// to a seat: the student screen requires the student's own account, and
    /// the admin enrolment screen only reaches sessions belonging to a weekly
    /// class. A family could buy a package and never be able to use it.
    ///
    /// <para>This page decides nothing. IStudentBookingService re-resolves the
    /// guardian link, the child's level in that session's own course, their age
    /// group, what their packages can still cover, and claims the seat
    /// atomically — a made-up studentId in this POST is refused there, not
    /// here.</para></summary>
    public async Task<IActionResult> OnPostBookAsync(long sessionId, long studentId)
    {
        var actingUserId = long.Parse(_userManager.GetUserId(User)!);
        var result = await _booking.BookSessionAsync(studentId, sessionId, actingUserId, HttpContext.RequestAborted);

        StatusMessage = result.Outcome == MVTeaches.Application.Scheduling.BookSessionOutcome.Booked
            ? _localizer["Booked. The lesson now appears below, and Join opens when it starts."].Value
            : null;
        ErrorMessage = result.Outcome switch
        {
            MVTeaches.Application.Scheduling.BookSessionOutcome.Unauthorized =>
                _localizer["This account is not registered as that child's guardian."].Value,
            MVTeaches.Application.Scheduling.BookSessionOutcome.SessionNotFound =>
                _localizer["Session not found."].Value,
            MVTeaches.Application.Scheduling.BookSessionOutcome.NoCurrentLevelAssigned =>
                _localizer["This child has no level in that course yet — the centre assigns one first."].Value,
            MVTeaches.Application.Scheduling.BookSessionOutcome.SessionLevelMismatch =>
                _localizer["That lesson is not at this child's level."].Value,
            MVTeaches.Application.Scheduling.BookSessionOutcome.SessionNotBookable =>
                _localizer["That lesson has already started or is no longer open for booking."].Value,
            MVTeaches.Application.Scheduling.BookSessionOutcome.AlreadyBooked =>
                _localizer["This child is already booked into that lesson."].Value,
            MVTeaches.Application.Scheduling.BookSessionOutcome.SessionFull =>
                _localizer["That lesson is full."].Value,
            MVTeaches.Application.Scheduling.BookSessionOutcome.PackageLimitExceeded =>
                _localizer["This child's package does not have enough hours left for another lesson — including the ones already booked."].Value,
            // Owner decision 2026-09-04: age groups are not being changed, so
            // this says plainly what happened instead of failing silently.
            MVTeaches.Application.Scheduling.BookSessionOutcome.NoApplicableAgeGroup =>
                _localizer["This child's age does not fall into any age group the centre teaches, so no lesson can be booked for them yet. Please contact the centre."].Value,
            _ => null,
        };

        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        Countries = await _db.Countries.Where(c => c.IsActive).OrderBy(c => c.NameEn).ToListAsync();
        var userId = long.Parse(_userManager.GetUserId(User)!);
        var guardian = await _db.Guardians.FirstOrDefaultAsync(g => g.UserId == userId);
        if (guardian is null)
        {
            NoGuardianProfileLinked = true;
            UpcomingSessions = Array.Empty<ChildSessionRow>();
            return;
        }

        var childIds = await _db.Guardianships
            .Where(g => g.GuardianId == guardian.Id)
            .Select(g => g.StudentId)
            .ToListAsync();
        var childNames = await _db.Students
            .Where(s => childIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.FullName);

        var now = _clock.GetCurrentInstant();
        var windowStart = now.Minus(Duration.FromDays(1));
        var windowEnd = now.Plus(Duration.FromDays(7));

        var enrollments = await _db.SessionEnrollments
            .Where(e => childIds.Contains(e.StudentId) && e.State == EnrollmentState.Active)
            .ToListAsync();
        var sessionIds = enrollments.Select(e => e.SessionId).Distinct().ToList();

        var sessions = await _db.ClassSessions
            .Where(s => sessionIds.Contains(s.Id) && s.StartsAtUtc >= windowStart && s.StartsAtUtc <= windowEnd)
            .OrderBy(s => s.StartsAtUtc)
            .ToListAsync();

        var attendance = await _db.AttendanceRecords
            .Where(a => sessionIds.Contains(a.SessionId) && childIds.Contains(a.StudentId))
            .ToListAsync();
        var presentPairs = attendance.Select(a => (a.SessionId, a.StudentId)).ToHashSet();

        var courseNames = await _db.Courses.ToDictionaryAsync(c => c.Id, c => c.NameEn);
        var levelCodes = await _db.Levels.ToDictionaryAsync(l => l.Id, l => l.Code);
        var enrollmentsByStudentAndSession = enrollments.ToDictionary(e => (e.SessionId, e.StudentId), e => e);

        // Owner decision 2026-09-04 — see ChildSessionRow. Each child's own
        // country zone, resolved once for the whole page rather than per row.
        var studentTimeZones = await _db.Students
            .Where(s => childIds.Contains(s.Id))
            .Join(_db.Countries, s => s.CountryId, c => c.Id, (s, c) => new { s.Id, c.DefaultTimeZone })
            .ToDictionaryAsync(x => x.Id, x => x.DefaultTimeZone);

        var teacherIds = sessions.Select(s => s.TeacherId).Distinct().ToList();
        var teacherNames = await _db.Teachers
            .Where(t => teacherIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.FullName);

        var rows = new List<ChildSessionRow>();
        foreach (var session in sessions)
        {
            foreach (var studentId in childIds)
            {
                if (!enrollmentsByStudentAndSession.ContainsKey((session.Id, studentId)))
                {
                    continue;
                }

                var alreadyPresent = presentPairs.Contains((session.Id, studentId));
                var canJoin = !alreadyPresent && now >= session.StartsAtUtc && session.Status == ClassSessionStatus.Scheduled;
                rows.Add(new ChildSessionRow(studentId, childNames.GetValueOrDefault(studentId, string.Empty),
                    session.Id, session.StartsAtUtc, session.ScheduleTimeZone,
                    // Falls back to the session's own zone when a country row
                    // somehow carries no zone — never to a silent UTC.
                    studentTimeZones.GetValueOrDefault(studentId) is { Length: > 0 } zone ? zone : session.ScheduleTimeZone,
                    courseNames.GetValueOrDefault(session.CourseId, "?"), levelCodes.GetValueOrDefault(session.LevelId, "?"),
                    teacherNames.GetValueOrDefault(session.TeacherId, "?"),
                    session.Status, alreadyPresent, canJoin));
            }
        }

        UpcomingSessions = rows;

        // Owner decision 2026-09-04 (multi-course levels): a child holds one
        // current level PER COURSE, so this can no longer be keyed by student
        // alone — ToDictionary threw the moment a child studied two subjects.
        // The label names the course beside each level for the same reason.
        var currentLevels = await _db.StudentLevels
            .Where(l => childIds.Contains(l.StudentId) && l.IsCurrent)
            .ToListAsync();
        var levelLabelByChild = currentLevels
            .GroupBy(l => l.StudentId)
            .ToDictionary(g => g.Key, g => string.Join(" · ", g
                .Select(l => $"{courseNames.GetValueOrDefault(l.CourseId, "?")} {levelCodes.GetValueOrDefault(l.LevelId, "?")}")
                .OrderBy(text => text)));
        var children = await _db.Students
            .Where(s => childIds.Contains(s.Id))
            .OrderBy(s => s.FullName)
            .ToListAsync();

        await LoadBookableSessionsAsync(childIds, currentLevels, childNames, studentTimeZones, teacherNames,
            courseNames, levelCodes, enrollments, now);

        Children = children.Select(child => new ChildRow(
            child.Id,
            child.FullName,
            levelLabelByChild.GetValueOrDefault(child.Id),
            child.Status,
            rows.Count(r => r.StudentId == child.Id))).ToList();
    }

    /// <summary>Owner decision 2026-09-04: what each child could actually be
    /// booked into, and nothing else. Filtered on the (course, level) PAIRS the
    /// child currently holds — matching on the level alone would offer every
    /// other course's lessons at that level, in subjects they were never placed
    /// in. The two Contains() clauses only narrow the query; the pair check is
    /// what decides.
    ///
    /// <para>Whether the child's package can cover it is deliberately NOT
    /// filtered here: IStudentBookingService answers that at the moment of
    /// booking, against every other not-yet-consumed booking, and a list that
    /// tried to predict it would be a second opinion that drifts. A lesson they
    /// cannot afford is offered and then refused with a reason, rather than
    /// silently missing.</para></summary>
    private async Task LoadBookableSessionsAsync(
        IReadOnlyList<long> childIds,
        IReadOnlyList<MVTeaches.Domain.Placement.StudentLevel> currentLevels,
        Dictionary<long, string> childNames,
        Dictionary<long, string> studentTimeZones,
        Dictionary<long, string> teacherNames,
        Dictionary<long, string> courseNames,
        Dictionary<int, string> levelCodes,
        IReadOnlyList<SessionEnrollment> enrollments,
        Instant now)
    {
        if (currentLevels.Count == 0)
        {
            return;
        }

        var windowEnd = now.Plus(Duration.FromDays(30));
        var courseIds = currentLevels.Select(l => l.CourseId).Distinct().ToList();
        var levelIds = currentLevels.Select(l => l.LevelId).Distinct().ToList();

        var candidates = await _db.ClassSessions
            .Where(s => courseIds.Contains(s.CourseId) && levelIds.Contains(s.LevelId)
                        && s.Status == ClassSessionStatus.Scheduled
                        && s.StartsAtUtc > now && s.StartsAtUtc <= windowEnd)
            .OrderBy(s => s.StartsAtUtc)
            .Take(200)
            .ToListAsync();

        if (candidates.Count == 0)
        {
            return;
        }

        var missingTeacherIds = candidates.Select(s => s.TeacherId).Distinct()
            .Where(id => !teacherNames.ContainsKey(id)).ToList();
        if (missingTeacherIds.Count > 0)
        {
            var extra = await _db.Teachers.Where(te => missingTeacherIds.Contains(te.Id))
                .ToDictionaryAsync(te => te.Id, te => te.FullName);
            foreach (var pair in extra)
            {
                teacherNames[pair.Key] = pair.Value;
            }
        }

        var booked = enrollments
            .Where(e => e.State == EnrollmentState.Active)
            .Select(e => (e.SessionId, e.StudentId))
            .ToHashSet();

        var rows = new List<BookableSessionRow>();
        foreach (var level in currentLevels.OrderBy(l => l.StudentId))
        {
            foreach (var session in candidates.Where(s => s.CourseId == level.CourseId && s.LevelId == level.LevelId))
            {
                rows.Add(new BookableSessionRow(
                    level.StudentId,
                    childNames.GetValueOrDefault(level.StudentId, string.Empty),
                    session.Id,
                    session.StartsAtUtc,
                    session.ScheduleTimeZone,
                    studentTimeZones.GetValueOrDefault(level.StudentId) is { Length: > 0 } zone ? zone : session.ScheduleTimeZone,
                    courseNames.GetValueOrDefault(session.CourseId, "?"),
                    levelCodes.GetValueOrDefault(session.LevelId, "?"),
                    teacherNames.GetValueOrDefault(session.TeacherId, "?"),
                    session.DurationMinutes,
                    session.SeatsTaken,
                    session.Capacity,
                    booked.Contains((session.Id, level.StudentId))));
            }
        }

        BookableSessions = rows.OrderBy(r => r.StartsAtUtc).ThenBy(r => r.StudentName).ToList();
    }
}
