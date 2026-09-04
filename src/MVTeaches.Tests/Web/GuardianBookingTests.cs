using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.Common;
using MVTeaches.Domain.Ledger;
using MVTeaches.Domain.People;
using MVTeaches.Domain.Placement;
using MVTeaches.Domain.Scheduling;
using MVTeaches.Domain.Subscriptions;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;
using Xunit;

namespace MVTeaches.Tests.Web;

/// <summary>
/// Owner decision 2026-09-04, from a real Local Staging run: after paying, a
/// guardian saw no lesson and no Join button for their child. Five things were
/// true at once, and this class covers the one that no amount of fixing the
/// others would have solved — <b>there was no way for a guardian to book at
/// all</b>. Booking lived on the student's own screen, behind the student's own
/// login, and a child registered by their guardian has no login by design.
///
/// <para>The whole journey is exercised over real HTTP: the lesson is offered,
/// booked, then appears in the guardian's own list, and Join shows up only once
/// the lesson has actually started.</para>
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class GuardianBookingTests : IClassFixture<AuthorizationTests.Factory>
{
    private readonly AuthorizationTests.Factory _factory;
    private const string Password = "CorrectHorse123!";
    private static long _idSeed = 66_000_000;

    public GuardianBookingTests(TestDatabaseFixture fixture, AuthorizationTests.Factory factory)
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__MvTeaches", fixture.ConnectionString);
        _factory = factory;
    }

    private static long NextId() => Interlocked.Increment(ref _idSeed);

    private static readonly Regex AntiforgeryTokenPattern = new(
        "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");

    private HttpClient CreateClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path);
        var token = AntiforgeryTokenPattern.Match(html).Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token), $"Could not find the antiforgery token on {path}.");
        return token;
    }

    private record Family(long GuardianUserId, string GuardianEmail, long ChildId, string ChildName, long SessionId);

    /// <summary>The exact shape guardian self-registration produces, plus what
    /// a paid-up family has: a child with NO login, a level in a course, an
    /// Active subscription with real balance in the ledger, and one future
    /// lesson at that course and level.</summary>
    private async Task<Family> SeedPaidFamilyWithALessonAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();

        if (!await roleManager.RoleExistsAsync(RoleNames.Guardian))
        {
            await roleManager.CreateAsync(new ApplicationRole(RoleNames.Guardian));
        }

        var guardianEmail = $"gbook-{Guid.NewGuid():N}@test.mvteaches.local";
        var guardianUser = new ApplicationUser { UserName = guardianEmail, Email = guardianEmail, EmailConfirmed = true };
        var created = await userManager.CreateAsync(guardianUser, Password);
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
        await userManager.AddToRoleAsync(guardianUser, RoleNames.Guardian);

        var teacherUser = new ApplicationUser
        {
            UserName = $"gbook-teacher-{Guid.NewGuid():N}",
            Email = $"gbook-teacher-{Guid.NewGuid():N}@test.mvteaches.local",
        };
        db.Users.Add(teacherUser);

        var countryId = await db.Countries.OrderBy(c => c.Id).Select(c => c.Id).FirstAsync();
        var course = new Course($"GBOOK-{NextId()}", "دورة", "Course");
        db.Courses.Add(course);

        var levelId = (int)NextId();
        db.Levels.Add(new Level(levelId, $"GB{levelId}", "مستوى", "Level", levelId));

        var ageGroupId = (int)NextId();
        db.AgeGroups.Add(new AgeGroup(ageGroupId, $"GB{ageGroupId}", 5, 60, isMinor: true));

        // No userId: this child cannot sign in, and never could.
        var childName = $"Guardian Booked Child {Guid.NewGuid():N}"[..40];
        var child = new Student(countryId, childName, new LocalDate(2012, 6, 1), userId: null);
        db.Students.Add(child);
        await db.SaveChangesAsync();

        var teacher = new Teacher(teacherUser.Id, "Guardian Booking Teacher", "Asia/Amman");
        db.Teachers.Add(teacher);

        var guardian = new Guardian(guardianUser.Id, "Guardian Booking Parent");
        db.Guardians.Add(guardian);
        await db.SaveChangesAsync();

        db.Guardianships.Add(new Guardianship(guardian.Id, child.Id, GuardianRelationship.Parent,
            isPrimary: true, guardianUser.Id));
        db.StudentLevels.Add(new StudentLevel(child.Id, course.Id, levelId, guardianUser.Id, AssignedByRole.Admin,
            LevelAssignmentSource.AdminOverride, null, "seed", SystemClock.Instance.GetCurrentInstant()));

        var now = SystemClock.Instance.GetCurrentInstant();
        var subscription = new Subscription(child.Id, countryId, course.Id, levelId, SessionType.Group,
            new Money(50m, "JOD"), null, 10, 600, new LocalDate(2026, 1, 1), 365,
            SubscriptionOrigin.GuardianPurchase, guardianUser.Id, null);
        subscription.Activate();
        db.Subscriptions.Add(subscription);
        await db.SaveChangesAsync();

        db.EntitlementLedgerEntries.Add(EntitlementLedgerEntry.ForPurchase(
            child.Id, subscription.Id, course.Id, levelId, SessionType.Group, 600, NextId(), guardianUser.Id,
            now.Minus(Duration.FromDays(1))));

        var start = now.Plus(Duration.FromDays(2));
        var session = new ClassSession(countryId, null, course.Id, levelId, ageGroupId, teacher.Id,
            start, start.Plus(Duration.FromMinutes(60)), "Asia/Amman", "10:00", SessionType.Group,
            now.Minus(Duration.FromDays(1)));
        db.ClassSessions.Add(session);
        await db.SaveChangesAsync();

        return new Family(guardianUser.Id, guardianEmail, child.Id, childName, session.Id);
    }

    private async Task<HttpClient> SignedInGuardianAsync(string email)
    {
        var client = CreateClient();
        var token = await GetAntiforgeryTokenAsync(client, "/Account/Login");
        var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Input.Email"] = email,
            ["Input.Password"] = Password,
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    /// <summary>The whole missing journey, end to end: the lesson is offered to
    /// the guardian, booking it takes a seat for a child who has no account of
    /// their own, and it then appears in the guardian's own session list.</summary>
    [Fact]
    public async Task A_guardian_books_a_lesson_for_a_child_with_no_login_and_then_sees_it()
    {
        var family = await SeedPaidFamilyWithALessonAsync();
        var client = await SignedInGuardianAsync(family.GuardianEmail);

        // Offered: the lesson shows up as bookable before anything is pressed.
        var beforeHtml = await client.GetStringAsync("/Guardian/MyChildren");
        Assert.Contains("handler=Book", beforeHtml);
        Assert.Contains(family.ChildName, beforeHtml);

        var token = AntiforgeryTokenPattern.Match(beforeHtml).Groups[1].Value;
        var response = await client.PostAsync("/Guardian/MyChildren?handler=Book", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["sessionId"] = family.SessionId.ToString(),
            ["studentId"] = family.ChildId.ToString(),
        }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();

        var enrollment = await db.SessionEnrollments.SingleAsync(
            e => e.SessionId == family.SessionId && e.StudentId == family.ChildId);
        Assert.Equal(EnrollmentState.Active, enrollment.State);

        var seated = await db.ClassSessions.AsNoTracking().FirstAsync(s => s.Id == family.SessionId);
        Assert.Equal(1, seated.SeatsTaken);

        // Still no login for the child — booking created no account.
        Assert.Null(await db.Students.Where(s => s.Id == family.ChildId).Select(s => s.UserId).FirstAsync());

        // And the guardian now sees it in their own sessions list.
        var afterHtml = await client.GetStringAsync("/Guardian/MyChildren");
        Assert.Contains("Guardian Booking Teacher", afterHtml);
    }

    /// <summary>The Join window, which is the half the guardian was actually
    /// stuck on. A booked lesson that has not started offers no Join; the same
    /// lesson, once started, does. The lesson's start is moved back in the
    /// database rather than waiting two days for it.</summary>
    [Fact]
    public async Task Join_appears_only_once_the_booked_lesson_has_started()
    {
        var family = await SeedPaidFamilyWithALessonAsync();
        var client = await SignedInGuardianAsync(family.GuardianEmail);

        var bookHtml = await client.GetStringAsync("/Guardian/MyChildren");
        var token = AntiforgeryTokenPattern.Match(bookHtml).Groups[1].Value;
        var booked = await client.PostAsync("/Guardian/MyChildren?handler=Book", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["sessionId"] = family.SessionId.ToString(),
            ["studentId"] = family.ChildId.ToString(),
        }));
        Assert.Equal(HttpStatusCode.OK, booked.StatusCode);

        // Two days away: booked, but there is nothing to join yet.
        var notYetHtml = await client.GetStringAsync("/Guardian/MyChildren");
        Assert.DoesNotContain("handler=Join", notYetHtml);

        // Move the lesson to ten minutes ago. Test data only — this is the one
        // thing a test cannot do by asking the application, short of waiting.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
            var startedAt = SystemClock.Instance.GetCurrentInstant().Minus(Duration.FromMinutes(10));
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE class_sessions SET starts_at_utc = {startedAt.ToDateTimeOffset()}, ends_at_utc = {startedAt.Plus(Duration.FromMinutes(60)).ToDateTimeOffset()} WHERE \"Id\" = {family.SessionId}");
        }

        var startedHtml = await client.GetStringAsync("/Guardian/MyChildren");
        Assert.Contains("handler=Join", startedHtml);
    }

    /// <summary>A guardian is offered only lessons their own child is placed
    /// in. A lesson in another course, at the same level, must not appear —
    /// matching on the level alone is what used to offer every subject at
    /// once.</summary>
    [Fact]
    public async Task A_lesson_in_a_course_the_child_is_not_placed_in_is_never_offered()
    {
        var family = await SeedPaidFamilyWithALessonAsync();

        long otherSessionId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MvTeachesDbContext>();
            var mine = await db.ClassSessions.AsNoTracking().FirstAsync(s => s.Id == family.SessionId);

            // Same level, same age group, same teacher, DIFFERENT course — the
            // one dimension under test. An hour later, because the database's
            // own no_teacher_overlap exclusion constraint refuses to let one
            // teacher be in two places at once, and rightly so.
            var otherCourse = new Course($"GBOOK-OTHER-{NextId()}", "دورة", "Course");
            db.Courses.Add(otherCourse);
            await db.SaveChangesAsync();

            var otherStart = mine.StartsAtUtc.Plus(Duration.FromHours(3));
            var other = new ClassSession(mine.CountryId, null, otherCourse.Id, mine.LevelId, mine.AgeGroupId,
                mine.TeacherId, otherStart, otherStart.Plus(Duration.FromMinutes(60)), "Asia/Amman", "13:00", SessionType.Group,
                SystemClock.Instance.GetCurrentInstant().Minus(Duration.FromDays(1)));
            db.ClassSessions.Add(other);
            await db.SaveChangesAsync();
            otherSessionId = other.Id;
        }

        var client = await SignedInGuardianAsync(family.GuardianEmail);
        var html = await client.GetStringAsync("/Guardian/MyChildren");

        // Matched on the booking form's own hidden field, not on the bare id:
        // a session id of "2" also appears as an unrelated <option value="2">
        // in the add-a-child country picker on this same page.
        Assert.Contains($"name=\"sessionId\" value=\"{family.SessionId}\"", html);
        Assert.DoesNotContain($"name=\"sessionId\" value=\"{otherSessionId}\"", html);
    }
}
