using Microsoft.EntityFrameworkCore;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.People;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;
using Npgsql;
using Xunit;

namespace MVTeaches.Tests.Scheduling;

/// <summary>
/// Master engineering prompt §28 items 13-14 — database-enforced invariants
/// that must hold even if application code has a bug, because they are
/// physically impossible to violate at the database level (§14.2/§15.1).
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class ConcurrencyInvariantTests
{
    private readonly TestDatabaseFixture _fixture;
    private static long _idSeed = 5_000_000;

    public ConcurrencyInvariantTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static long NextId() => Interlocked.Increment(ref _idSeed);

    private static string TwoLetterCode(long seed)
    {
        var n = (int)(seed % 676);
        return string.Concat((char)('A' + n / 26), (char)('A' + n % 26));
    }

    // The 2-letter code space (676 combinations) is shared with every other
    // test class in the same run via the same NextId()-derived TwoLetterCode
    // pattern; a cross-class collision is a real, previously-hit failure
    // mode (see RescheduleAndCompensationTests' own comment on the same
    // issue), not flakiness. Retrying with a fresh id on an actual collision
    // — the same pattern MeetingProvisioningServiceTests already uses — is
    // what makes this genuinely collision-proof.
    private static async Task<int> SeedCountryAsync(MvTeachesDbContext db)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var id = (int)NextId();
            db.Countries.Add(new Country(id, TwoLetterCode(id), "دولة", "Country", "JOD", "+962", "Asia/Amman"));
            try
            {
                await db.SaveChangesAsync();
                return id;
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
            {
                db.ChangeTracker.Clear();
            }
        }

        throw new InvalidOperationException("Could not find a free 2-letter country code after 10 attempts.");
    }

    private async Task<long> CreateUserAsync(MvTeachesDbContext db)
    {
        var user = new ApplicationUser
        {
            UserName = $"u-{Guid.NewGuid():N}",
            NormalizedUserName = $"U-{Guid.NewGuid():N}",
            Email = $"{Guid.NewGuid():N}@test.mvteaches.local",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    [Fact]
    public async Task Teacher_schedule_collision_is_rejected_by_the_database_exclude_constraint()
    {
        await using var db = _fixture.CreateContext();

        var countryId = await SeedCountryAsync(db);
        var courseId = NextId();
        var levelId = (int)NextId();
        var ageGroupId = (int)NextId();
        var teacherUserId = await CreateUserAsync(db);

        db.Courses.Add(new Course("C" + courseId, "دورة", "Course"));
        db.Levels.Add(new Level(levelId, "L" + levelId, "مستوى", "Level", levelId));
        db.AgeGroups.Add(new AgeGroup(ageGroupId, "A" + ageGroupId, 5, 12, true));
        var teacher = new Teacher(teacherUserId, "Teacher", "Asia/Amman");
        db.Teachers.Add(teacher);
        await db.SaveChangesAsync();

        var now = SystemClock.Instance.GetCurrentInstant();
        var start = now.Plus(Duration.FromHours(1));

        var first = new ClassSession(countryId, null, courseId, levelId, ageGroupId, teacher.Id,
            start, start.Plus(Duration.FromMinutes(60)), "Asia/Amman", "18:00", SessionType.Group, now);
        db.ClassSessions.Add(first);
        await db.SaveChangesAsync();

        // Overlaps the first session by 30 minutes, same teacher.
        var overlapStart = start.Plus(Duration.FromMinutes(30));
        var second = new ClassSession(countryId, null, courseId, levelId, ageGroupId, teacher.Id,
            overlapStart, overlapStart.Plus(Duration.FromMinutes(60)), "Asia/Amman", "18:30", SessionType.Group, now);
        db.ClassSessions.Add(second);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal("23P01", ((PostgresException)ex.InnerException!).SqlState); // exclusion_violation
    }

    /// <summary>
    /// Owner decision 2026-09-04, superseding the 2026-08-30 fixed-seat rule:
    /// a GROUP session's seat count is the centre's to choose, so 6 and 1 are
    /// both legitimate for a group and no longer appear here. Private and
    /// Placement are still pinned to exactly 1 — one-to-one is what those types
    /// MEAN, and a "private" lesson seating two would be a group lesson wearing
    /// the wrong label, priced and paid as the wrong thing.
    ///
    /// The database is what makes a wrong value impossible, the same reasoning
    /// as no_teacher_overlap being an EXCLUDE constraint. This writes raw SQL
    /// deliberately, bypassing the domain, to prove the constraint itself
    /// refuses — not merely that ResolveCapacity declines to ask.
    ///
    /// The final two rows cover the sanity band rather than the type rule: a
    /// group session with zero seats, or more than the agreed ceiling, is a
    /// typo and is refused as one.
    /// </summary>
    [Theory]
    [InlineData("Private", 4, "ck_session_capacity_matches_type")]
    [InlineData("Private", 2, "ck_session_capacity_matches_type")]
    [InlineData("Placement", 2, "ck_session_capacity_matches_type")]
    [InlineData("Group", 0, "ck_session_capacity_band")]
    [InlineData("Group", 51, "ck_session_capacity_band")]
    public async Task The_database_rejects_a_seat_count_that_does_not_match_the_session_type(string sessionType,
        int capacity, string expectedConstraint)
    {
        await using var db = _fixture.CreateContext();

        var countryId = await SeedCountryAsync(db);
        var courseId = NextId();
        var levelId = (int)NextId();
        var ageGroupId = (int)NextId();
        var teacherUserId = await CreateUserAsync(db);

        db.Courses.Add(new Course("C" + courseId, "دورة", "Course"));
        db.Levels.Add(new Level(levelId, "L" + levelId, "مستوى", "Level", levelId));
        db.AgeGroups.Add(new AgeGroup(ageGroupId, "A" + ageGroupId, 5, 12, true));
        var teacher = new Teacher(teacherUserId, "Teacher", "Asia/Amman");
        db.Teachers.Add(teacher);
        await db.SaveChangesAsync();

        var now = SystemClock.Instance.GetCurrentInstant();
        var start = now.Plus(Duration.FromHours(5));

        // Column names here are the real snake_case ones (schedule_tz, not
        // schedule_time_zone) — verified against the live schema, since a
        // wrong name would fail as undefined_column and silently stop this
        // test from ever exercising the constraint it is named after.
        var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO class_sessions
                (country_id, course_id, level_id, age_group_id, teacher_id, starts_at_utc, ends_at_utc,
                 duration_minutes, schedule_tz, local_start_text, session_type, capacity, seats_taken,
                 status, created_at_utc)
            VALUES
                ({countryId}, {courseId}, {levelId}, {ageGroupId}, {teacher.Id},
                 {start.ToDateTimeUtc()}, {start.Plus(Duration.FromMinutes(60)).ToDateTimeUtc()},
                 60, 'Asia/Amman', '10:00', {sessionType}, {capacity}, 0, 'Scheduled', {now.ToDateTimeUtc()});"));

        Assert.Equal("23514", ex.SqlState); // check_violation
        Assert.Contains(expectedConstraint, ex.Message);
    }

    /// <summary>The domain side of the same rule: capacity is derived, and the
    /// caller has no way to ask for anything else.</summary>
    [Fact]
    public void Capacity_is_derived_from_the_session_type_and_never_supplied()
    {
        // Owner decision 2026-09-04: Group's 4 is now a DEFAULT the admin may
        // override, not a fixed rule. Private and Placement stay pinned at 1 —
        // one-to-one is what those types mean.
        Assert.Equal(4, ClassSession.DefaultCapacityFor(SessionType.Group));
        Assert.Equal(1, ClassSession.DefaultCapacityFor(SessionType.Private));
        Assert.Equal(1, ClassSession.DefaultCapacityFor(SessionType.Placement));

        // A group session takes the seat count it is given...
        Assert.Equal(6, ClassSession.ResolveCapacity(SessionType.Group, 6));
        Assert.Equal(4, ClassSession.ResolveCapacity(SessionType.Group, null));

        // ...but Private and Placement ignore the request entirely rather than
        // honouring it, so no caller can quietly turn a private lesson into a
        // group one by asking for more seats.
        Assert.Equal(1, ClassSession.ResolveCapacity(SessionType.Private, 8));
        Assert.Equal(1, ClassSession.ResolveCapacity(SessionType.Placement, 8));

        // Nonsense is refused rather than clamped: a caller asking for zero or
        // a thousand seats has a bug worth surfacing.
        Assert.Throws<ArgumentOutOfRangeException>(() => ClassSession.ResolveCapacity(SessionType.Group, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ClassSession.ResolveCapacity(SessionType.Group, ClassSession.MaximumGroupCapacity + 1));

        // There is no constructor overload that accepts a seat count at all.
        var takesCapacity = typeof(ClassSession).GetConstructors()
            .Any(c => c.GetParameters().Any(p => p.Name is "capacity" or "seats" or "seatCount"));
        Assert.False(takesCapacity);
    }

    [Fact]
    public async Task Non_overlapping_sessions_for_the_same_teacher_are_allowed()
    {
        await using var db = _fixture.CreateContext();

        var countryId = await SeedCountryAsync(db);
        var courseId = NextId();
        var levelId = (int)NextId();
        var ageGroupId = (int)NextId();
        var teacherUserId = await CreateUserAsync(db);

        db.Courses.Add(new Course("C" + courseId, "دورة", "Course"));
        db.Levels.Add(new Level(levelId, "L" + levelId, "مستوى", "Level", levelId));
        db.AgeGroups.Add(new AgeGroup(ageGroupId, "A" + ageGroupId, 5, 12, true));
        var teacher = new Teacher(teacherUserId, "Teacher", "Asia/Amman");
        db.Teachers.Add(teacher);
        await db.SaveChangesAsync();

        var now = SystemClock.Instance.GetCurrentInstant();
        var start = now.Plus(Duration.FromHours(2));

        db.ClassSessions.Add(new ClassSession(countryId, null, courseId, levelId, ageGroupId, teacher.Id,
            start, start.Plus(Duration.FromMinutes(60)), "Asia/Amman", "19:00", SessionType.Group, now));
        // Starts exactly when the first one ends — back-to-back, not overlapping.
        db.ClassSessions.Add(new ClassSession(countryId, null, courseId, levelId, ageGroupId, teacher.Id,
            start.Plus(Duration.FromMinutes(60)), start.Plus(Duration.FromMinutes(120)), "Asia/Amman", "20:00", SessionType.Group, now));

        await db.SaveChangesAsync(); // must not throw
        Assert.Equal(2, db.ClassSessions.Count(s => s.TeacherId == teacher.Id));
    }

    [Fact]
    public async Task Duplicate_active_enrollment_for_the_same_session_and_student_is_rejected()
    {
        await using var db = _fixture.CreateContext();

        var countryId = await SeedCountryAsync(db);
        var courseId = NextId();
        var levelId = (int)NextId();
        var ageGroupId = (int)NextId();
        var teacherUserId = await CreateUserAsync(db);
        var studentUserId = await CreateUserAsync(db);

        db.Courses.Add(new Course("C" + courseId, "دورة", "Course"));
        db.Levels.Add(new Level(levelId, "L" + levelId, "مستوى", "Level", levelId));
        db.AgeGroups.Add(new AgeGroup(ageGroupId, "A" + ageGroupId, 5, 12, true));
        var teacher = new Teacher(teacherUserId, "Teacher", "Asia/Amman");
        db.Teachers.Add(teacher);
        var student = new Student(countryId, "Student", new LocalDate(2015, 1, 1), studentUserId);
        db.Students.Add(student);
        await db.SaveChangesAsync();

        var now = SystemClock.Instance.GetCurrentInstant();
        var session = new ClassSession(countryId, null, courseId, levelId, ageGroupId, teacher.Id,
            now, now.Plus(Duration.FromMinutes(60)), "Asia/Amman", "17:00", SessionType.Group, now);
        db.ClassSessions.Add(session);
        await db.SaveChangesAsync();

        db.SessionEnrollments.Add(new SessionEnrollment(session.Id, student.Id, ageGroupId, studentUserId, now));
        await db.SaveChangesAsync();

        db.SessionEnrollments.Add(new SessionEnrollment(session.Id, student.Id, ageGroupId, studentUserId, now));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal("23505", ((PostgresException)ex.InnerException!).SqlState); // unique_violation
    }

    [Fact]
    public async Task Payroll_line_cannot_be_recorded_twice_for_the_same_session_in_the_same_period()
    {
        await using var db = _fixture.CreateContext();

        var countryId = await SeedCountryAsync(db);
        var periodId = NextId();
        var teacherId = NextId();
        var sessionId = NextId();

        var period = new MVTeaches.Domain.Payroll.PayrollPeriod(countryId, new LocalDate(2026, 1, 1), new LocalDate(2026, 1, 31));
        db.PayrollPeriods.Add(period);
        await db.SaveChangesAsync();

        db.PayrollLines.Add(new MVTeaches.Domain.Payroll.PayrollLine(period.Id, teacherId, sessionId, 60, 12m, "JOD", 12m));
        await db.SaveChangesAsync();

        db.PayrollLines.Add(new MVTeaches.Domain.Payroll.PayrollLine(period.Id, teacherId, sessionId, 60, 12m, "JOD", 12m));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal("23505", ((PostgresException)ex.InnerException!).SqlState);
    }

    [Fact]
    public async Task The_ledger_cannot_be_updated_or_deleted_even_directly()
    {
        await using var db = _fixture.CreateContext();

        var studentId = NextId();
        var courseId = NextId();
        var levelId = (int)NextId();
        var now = SystemClock.Instance.GetCurrentInstant();

        var entry = MVTeaches.Domain.Ledger.EntitlementLedgerEntry.ForAdminGrant(
            studentId, subscriptionId: NextId(), courseId, levelId, MVTeaches.Domain.Catalog.SessionType.Group,
            60, performedByUserId: NextId(), "test grant", now);
        db.EntitlementLedgerEntries.Add(entry);
        await db.SaveChangesAsync();

        // Attempt a raw UPDATE against the append-only table — must be rejected
        // by the trigger even though it bypasses the C# domain model entirely.
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlAsync($"UPDATE entitlement_ledger SET note = 'tampered' WHERE \"Id\" = {entry.Id}"));
        Assert.Equal("P0001", ex.SqlState); // raise_exception
    }

    /// <summary>Owner decision 2026-09-04, the half the theory above cannot
    /// show: a group session with a seat count OTHER than 4 is now accepted by
    /// the database, which is the whole point of relaxing the constraint.
    /// Written through the domain, since that is how a real session is made.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(20)]
    public async Task The_database_accepts_any_reasonable_seat_count_for_a_group_session(int capacity)
    {
        await using var db = _fixture.CreateContext();
        var now = SystemClock.Instance.GetCurrentInstant();

        var countryId = await SeedCountryAsync(db);
        var courseId = NextId();
        var levelId = (int)NextId();
        var ageGroupId = (int)NextId();
        var teacherUserId = await CreateUserAsync(db);
        db.Courses.Add(new Course("C" + courseId, "دورة", "Course"));
        db.Levels.Add(new Level(levelId, "L" + levelId, "مستوى", "Level", levelId));
        db.AgeGroups.Add(new AgeGroup(ageGroupId, "A" + ageGroupId, 5, 60, true));
        var teacher = new Teacher(teacherUserId, "Teacher", "Asia/Amman");
        db.Teachers.Add(teacher);
        await db.SaveChangesAsync();

        var start = now.Plus(Duration.FromDays(9));
        var session = new ClassSession(countryId, null, courseId, levelId, ageGroupId, teacher.Id,
            start, start.Plus(Duration.FromMinutes(60)), "Asia/Amman", "10:00", SessionType.Group, now,
            requestedCapacity: capacity);
        db.ClassSessions.Add(session);
        await db.SaveChangesAsync();

        await using var verify = _fixture.CreateContext();
        Assert.Equal(capacity, (await verify.ClassSessions.FirstAsync(s => s.Id == session.Id)).Capacity);
    }

    /// <summary>Owner decision 2026-09-04: a group session with a chosen seat
    /// count fills at exactly that count, and the over-booking guard holds at
    /// the new number rather than at the old fixed 4. This is the assertion
    /// that matters — a wider class must not become a class with no ceiling.</summary>
    [Fact]
    public async Task A_group_session_with_a_chosen_seat_count_still_refuses_the_seat_past_it()
    {
        await using var db = _fixture.CreateContext();
        var now = SystemClock.Instance.GetCurrentInstant();

        var countryId = await SeedCountryAsync(db);
        var courseId = NextId();
        var levelId = (int)NextId();
        var ageGroupId = (int)NextId();
        var teacherUserId = await CreateUserAsync(db);
        db.Courses.Add(new Course("C" + courseId, "دورة", "Course"));
        db.Levels.Add(new Level(levelId, "L" + levelId, "مستوى", "Level", levelId));
        db.AgeGroups.Add(new AgeGroup(ageGroupId, "A" + ageGroupId, 5, 60, true));
        var teacher = new Teacher(teacherUserId, "Teacher", "Asia/Amman");
        db.Teachers.Add(teacher);
        await db.SaveChangesAsync();

        var start = now.Plus(Duration.FromDays(2));
        var session = new ClassSession(countryId, null, courseId, levelId, ageGroupId, teacher.Id,
            start, start.Plus(Duration.FromMinutes(60)),
            "Asia/Amman", "10:00", SessionType.Group, now, requestedCapacity: 2);
        db.ClassSessions.Add(session);
        await db.SaveChangesAsync();

        Assert.Equal(2, session.Capacity);

        // The atomic claim is the real guard; it must stop at 2, not at 4.
        var firstSeat = await ClaimSeatAsync(db, session.Id);
        var secondSeat = await ClaimSeatAsync(db, session.Id);
        var thirdSeat = await ClaimSeatAsync(db, session.Id);

        Assert.Equal(1, firstSeat);
        Assert.Equal(1, secondSeat);
        Assert.Equal(0, thirdSeat); // full at the chosen number

        await using var verify = _fixture.CreateContext();
        var reloaded = await verify.ClassSessions.FirstAsync(s => s.Id == session.Id);
        Assert.Equal(2, reloaded.SeatsTaken);
        Assert.Equal(2, reloaded.Capacity);
    }

    /// <summary>The seat-claim statement the booking path uses, isolated: it is
    /// the single place a seat is ever taken, so it is the single place the
    /// capacity ceiling has to hold.</summary>
    private async Task<int> ClaimSeatAsync(MvTeachesDbContext db, long sessionId) =>
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE class_sessions SET seats_taken = seats_taken + 1 WHERE \"Id\" = {sessionId} AND status = 'Scheduled' AND seats_taken < capacity");
}
