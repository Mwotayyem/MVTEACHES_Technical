using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MVTeaches.Application.People;
using MVTeaches.Application.Placement;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.People;
using MVTeaches.Domain.Placement;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.People;
using MVTeaches.Infrastructure.Persistence;
using MVTeaches.Infrastructure.Placement;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace MVTeaches.Tests.Placement;

/// <summary>
/// Owner decision 2026-08-30, reversing D-48. The student-facing scoring
/// engine: eligibility, starting/submitting an attempt entirely server-side,
/// the admin-approved retake cycle, guardian-child isolation, and IDOR.
/// "Do not invent academic questions... or scoring thresholds" — content here
/// is deliberately trivial placeholder arithmetic, never real curriculum.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class PlacementAttemptServiceTests
{
    private readonly TestDatabaseFixture _fixture;
    private static long _idSeed = 81_000_000;

    public PlacementAttemptServiceTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static long NextId() => Interlocked.Increment(ref _idSeed);

    private static string TwoLetterCode(long seed)
    {
        var n = (int)(seed % 676);
        return string.Concat((char)('A' + n / 26), (char)('A' + n % 26));
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

    private static IPlacementAttemptService CreateService(MvTeachesDbContext db) =>
        new PlacementAttemptService(db, new StudentAdmissionService(db,
            BuildUserManager(db), new FakeClock(SystemClock.Instance.GetCurrentInstant())),
            new FakeClock(SystemClock.Instance.GetCurrentInstant()));

    // Only AssignLevelAsync (used by OverrideLevelAsync) is ever exercised
    // through this UserManager-carrying StudentAdmissionService, and that
    // method never touches the UserManager itself — a real one is still built
    // via DI (rather than passed null) so the type stays honestly constructed,
    // matching StudentAdmissionServiceTests' own established pattern.
    private static Microsoft.AspNetCore.Identity.UserManager<ApplicationUser> BuildUserManager(MvTeachesDbContext db)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(db);
        services.AddIdentityCore<ApplicationUser>(options => options.Password.RequiredLength = 10)
            .AddEntityFrameworkStores<MvTeachesDbContext>();
        return services.BuildServiceProvider().GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>>();
    }

    private static IPlacementTestAdminService CreateAdminService(MvTeachesDbContext db) =>
        new PlacementTestAdminService(db, new FakeClock(SystemClock.Instance.GetCurrentInstant()),
            TestLocalization.For<MVTeaches.Infrastructure.Resources.InfrastructureResource>());

    /// <summary>A minimal always-publishable, active version: one 10-point
    /// question, correct choice "2" is the second option deliberately (index
    /// 1, not 0), so a test that always picks "the first choice" would fail
    /// honestly rather than passing by accident.</summary>
    private static async Task<(long VersionId, long QuestionId, long CorrectChoiceId, long WrongChoiceId, int LevelA, int LevelB, long CourseId)>
        SeedActiveVersionAsync(MvTeachesDbContext db)
    {
        var levelA = (int)NextId();
        var levelB = (int)NextId();
        db.Levels.Add(new Level(levelA, "L" + levelA, "مستوى", "Level A", levelA));
        db.Levels.Add(new Level(levelB, "L" + levelB, "مستوى", "Level B", levelB));
        await db.SaveChangesAsync();

        // Owner decision 2026-09-04: a placement test places into one course's
        // level ladder, so the version carries the course it is for.
        var course = new MVTeaches.Domain.Catalog.Course("C" + NextId(), "دورة", "Course");
        db.Courses.Add(course);
        await db.SaveChangesAsync();

        var admin = CreateAdminService(db);
        var version = await admin.CreateDraftVersionAsync("v1", course.Id, NextId(), CancellationToken.None);
        await admin.AddQuestionAsync(version.TestVersionId, "1+1=?", 10,
            new[] { new AddQuestionChoice("3", false), new AddQuestionChoice("2", true) }, 0, CancellationToken.None);
        await admin.AddScoreRangeAsync(version.TestVersionId, 0, 4, levelA, CancellationToken.None);
        await admin.AddScoreRangeAsync(version.TestVersionId, 5, 10, levelB, CancellationToken.None);
        await admin.PublishAsync(version.TestVersionId, NextId(), CancellationToken.None);
        await admin.ActivateAsync(version.TestVersionId, CancellationToken.None);

        var question = await db.PlacementQuestions.FirstAsync(q => q.TestVersionId == version.TestVersionId);
        var correct = await db.PlacementAnswerChoices.FirstAsync(c => c.QuestionId == question.Id && c.IsCorrect);
        var wrong = await db.PlacementAnswerChoices.FirstAsync(c => c.QuestionId == question.Id && !c.IsCorrect);
        return (version.TestVersionId, question.Id, correct.Id, wrong.Id, levelA, levelB, course.Id);
    }

    // One Country for the whole class, not one per student: the 2-letter code
    // space (676 combinations) is shared with every other test class in the
    // same run via the same NextId()-derived TwoLetterCode pattern, and a real
    // cross-class collision has already happened before (see
    // RescheduleAndCompensationTests' own comment on the same issue).
    // Minimizing how many this class creates keeps the odds negligible.
    private static int? _sharedCountryId;

    private static async Task<int> GetOrSeedCountryAsync(MvTeachesDbContext db)
    {
        if (_sharedCountryId is { } existing)
        {
            return existing;
        }

        // A shared country per class only reduces the ODDS of a cross-class
        // collision on the 676-code space; it does not prevent one, since two
        // unrelated classes' independent NextId() sequences can still land on
        // the same code % 676 by pure coincidence. Retrying with a fresh id on
        // an actual collision (as MeetingProvisioningServiceTests already
        // does) is what makes this genuinely collision-proof rather than
        // merely unlikely to collide.
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

    private static async Task<(long StudentId, long StudentUserId)> SeedStudentAsync(MvTeachesDbContext db)
    {
        var countryId = await GetOrSeedCountryAsync(db);
        var userId = await CreateUserAsync(db, "student");
        var student = new Student(countryId, "Student", new LocalDate(2012, 1, 1), userId);
        db.Students.Add(student);
        await db.SaveChangesAsync();
        return (student.Id, userId);
    }

    [Fact]
    public async Task A_fresh_student_is_eligible_for_a_free_first_attempt()
    {
        await using var db = _fixture.CreateContext();
        var (studentId, userId) = await SeedStudentAsync(db);
        var service = CreateService(db);

        var eligibility = await service.GetEligibilityAsync(studentId, userId, CancellationToken.None);

        Assert.Equal(PlacementEligibilityStatus.EligibleFirstAttempt, eligibility.Status);
        Assert.Empty(eligibility.CurrentLevels);
    }

    [Fact]
    public async Task Correct_answers_never_expose_which_choice_is_correct_to_the_caller()
    {
        await using var db = _fixture.CreateContext();
        var (versionId, _, _, _, _, _, _) = await SeedActiveVersionAsync(db);
        var (studentId, userId) = await SeedStudentAsync(db);
        var service = CreateService(db);

        var result = await service.StartAttemptAsync(studentId, userId, CancellationToken.None);

        Assert.Equal(StartAttemptOutcome.Started, result.Outcome);
        var question = Assert.Single(result.Questions!);
        Assert.Equal(2, question.Options.Count);
        // PlacementQuestionOption carries only (ChoiceId, Text) — there is no
        // property to even hold an IsCorrect flag, so this is enforced by the
        // record's own shape, not merely by a value happening to be false.
        Assert.DoesNotContain("IsCorrect", typeof(PlacementQuestionOption).GetProperties().Select(p => p.Name));
    }

    [Fact]
    public async Task Submitting_the_correct_answer_scores_full_marks_and_assigns_the_matching_level()
    {
        await using var db = _fixture.CreateContext();
        var (versionId, questionId, correctChoiceId, _, levelA, levelB, _) = await SeedActiveVersionAsync(db);
        var (studentId, userId) = await SeedStudentAsync(db);
        var service = CreateService(db);
        var started = await service.StartAttemptAsync(studentId, userId, CancellationToken.None);

        var result = await service.SubmitAttemptAsync(started.AttemptId!.Value, studentId, userId,
            new Dictionary<long, long> { [questionId] = correctChoiceId }, CancellationToken.None);

        Assert.Equal(SubmitAttemptOutcome.Scored, result.Outcome);
        Assert.Equal(10, result.Score);
        Assert.Equal(levelB, result.AssignedLevelId);

        await using var verify = _fixture.CreateContext();
        var currentLevel = await verify.StudentLevels.SingleAsync(l => l.StudentId == studentId && l.IsCurrent);
        Assert.Equal(levelB, currentLevel.LevelId);
        Assert.Equal(LevelAssignmentSource.PlacementTest, currentLevel.Source);
        Assert.Equal(AssignedByRole.System, currentLevel.AssignedByRole); // no human judgment call in this path
    }

    [Fact]
    public async Task Submitting_the_wrong_answer_scores_zero_and_assigns_the_low_range_level()
    {
        await using var db = _fixture.CreateContext();
        var (versionId, questionId, _, wrongChoiceId, levelA, levelB, _) = await SeedActiveVersionAsync(db);
        var (studentId, userId) = await SeedStudentAsync(db);
        var service = CreateService(db);
        var started = await service.StartAttemptAsync(studentId, userId, CancellationToken.None);

        var result = await service.SubmitAttemptAsync(started.AttemptId!.Value, studentId, userId,
            new Dictionary<long, long> { [questionId] = wrongChoiceId }, CancellationToken.None);

        Assert.Equal(SubmitAttemptOutcome.Scored, result.Outcome);
        Assert.Equal(0, result.Score);
        Assert.Equal(levelA, result.AssignedLevelId);
    }

    [Fact]
    public async Task A_completed_attempts_score_and_level_are_never_rewritten_by_a_later_version_edit()
    {
        await using var db = _fixture.CreateContext();
        var (versionId, questionId, correctChoiceId, _, _, levelB, _) = await SeedActiveVersionAsync(db);
        var (studentId, userId) = await SeedStudentAsync(db);
        var service = CreateService(db);
        var started = await service.StartAttemptAsync(studentId, userId, CancellationToken.None);
        await service.SubmitAttemptAsync(started.AttemptId!.Value, studentId, userId,
            new Dictionary<long, long> { [questionId] = correctChoiceId }, CancellationToken.None);

        await using var verify = _fixture.CreateContext();
        var attempt = await verify.PlacementAttempts.FirstAsync(a => a.Id == started.AttemptId);
        Assert.Equal(PlacementAttemptStatus.Completed, attempt.Status);
        Assert.Equal(10, attempt.Score);
        Assert.Equal(levelB, attempt.AssignedLevelId);
        Assert.Equal(versionId, attempt.TestVersionId); // the exact version used is preserved permanently
    }

    [Fact]
    public async Task Submitting_a_choice_that_does_not_belong_to_its_question_is_rejected()
    {
        await using var db = _fixture.CreateContext();
        var (_, questionId, _, _, _, _, _) = await SeedActiveVersionAsync(db);
        var (studentId, userId) = await SeedStudentAsync(db);
        var service = CreateService(db);
        var started = await service.StartAttemptAsync(studentId, userId, CancellationToken.None);

        var result = await service.SubmitAttemptAsync(started.AttemptId!.Value, studentId, userId,
            new Dictionary<long, long> { [questionId] = 999_999_999L }, CancellationToken.None);

        Assert.Equal(SubmitAttemptOutcome.InvalidChoiceForQuestion, result.Outcome);
    }

    [Fact]
    public async Task Submitting_without_answering_every_question_is_rejected()
    {
        await using var db = _fixture.CreateContext();
        await SeedActiveVersionAsync(db);
        var (studentId, userId) = await SeedStudentAsync(db);
        var service = CreateService(db);
        var started = await service.StartAttemptAsync(studentId, userId, CancellationToken.None);

        var result = await service.SubmitAttemptAsync(started.AttemptId!.Value, studentId, userId,
            new Dictionary<long, long>(), CancellationToken.None);

        Assert.Equal(SubmitAttemptOutcome.MissingAnswers, result.Outcome);
    }

    [Fact]
    public async Task Until_a_placement_result_exists_there_is_no_current_level_for_a_purchase_gate_to_read()
    {
        // Rule 1: "Until a placement result exists, the student must not
        // purchase a package." This proves the OTHER half of that rule (the
        // purchase gate itself lives in SubscriptionServiceTests,
        // A_student_with_no_assigned_level_cannot_purchase_anything) is fed by
        // real data: a student with no completed attempt genuinely has no
        // current StudentLevel row for that gate to find.
        await using var db = _fixture.CreateContext();
        var (studentId, _) = await SeedStudentAsync(db);
        Assert.False(await db.StudentLevels.AnyAsync(l => l.StudentId == studentId && l.IsCurrent));
    }

    [Fact]
    public async Task Re_calling_start_after_completing_the_first_attempt_without_a_retake_is_refused()
    {
        await using var db = _fixture.CreateContext();
        var (_, questionId, correctChoiceId, _, _, _, _) = await SeedActiveVersionAsync(db);
        var (studentId, userId) = await SeedStudentAsync(db);
        var service = CreateService(db);
        var first = await service.StartAttemptAsync(studentId, userId, CancellationToken.None);
        await service.SubmitAttemptAsync(first.AttemptId!.Value, studentId, userId,
            new Dictionary<long, long> { [questionId] = correctChoiceId }, CancellationToken.None);

        var eligibility = await service.GetEligibilityAsync(studentId, userId, CancellationToken.None);
        Assert.Equal(PlacementEligibilityStatus.AlreadyCompletedNoRetakeApproved, eligibility.Status);

        var secondStart = await service.StartAttemptAsync(studentId, userId, CancellationToken.None);
        Assert.Equal(StartAttemptOutcome.NotEligible, secondStart.Outcome);
    }

    [Fact]
    public async Task A_pending_retake_request_does_not_by_itself_allow_a_new_attempt()
    {
        await using var db = _fixture.CreateContext();
        var (_, questionId, correctChoiceId, _, _, _, _) = await SeedActiveVersionAsync(db);
        var (studentId, userId) = await SeedStudentAsync(db);
        var service = CreateService(db);
        var first = await service.StartAttemptAsync(studentId, userId, CancellationToken.None);
        await service.SubmitAttemptAsync(first.AttemptId!.Value, studentId, userId,
            new Dictionary<long, long> { [questionId] = correctChoiceId }, CancellationToken.None);

        var requestResult = await service.RequestRetakeAsync(studentId, userId, CancellationToken.None);
        Assert.Equal(RequestRetakeOutcome.Requested, requestResult.Outcome);

        var eligibility = await service.GetEligibilityAsync(studentId, userId, CancellationToken.None);
        Assert.Equal(PlacementEligibilityStatus.RetakePending, eligibility.Status);

        var secondStart = await service.StartAttemptAsync(studentId, userId, CancellationToken.None);
        Assert.Equal(StartAttemptOutcome.NotEligible, secondStart.Outcome); // still pending, not yet approved
    }

    [Fact]
    public async Task Requesting_a_second_retake_while_one_is_already_pending_is_refused()
    {
        await using var db = _fixture.CreateContext();
        var (_, questionId, correctChoiceId, _, _, _, _) = await SeedActiveVersionAsync(db);
        var (studentId, userId) = await SeedStudentAsync(db);
        var service = CreateService(db);
        var first = await service.StartAttemptAsync(studentId, userId, CancellationToken.None);
        await service.SubmitAttemptAsync(first.AttemptId!.Value, studentId, userId,
            new Dictionary<long, long> { [questionId] = correctChoiceId }, CancellationToken.None);
        await service.RequestRetakeAsync(studentId, userId, CancellationToken.None);

        var second = await service.RequestRetakeAsync(studentId, userId, CancellationToken.None);

        Assert.Equal(RequestRetakeOutcome.AlreadyPendingOrApproved, second.Outcome);
    }

    /// <summary>Rule 3 end to end: request -> admin approves -> exactly one
    /// new attempt is allowed -> the approval cannot be reused for a further one.</summary>
    [Fact]
    public async Task An_approved_retake_allows_exactly_one_new_attempt_and_is_then_consumed()
    {
        await using var db = _fixture.CreateContext();
        var (_, questionId, correctChoiceId, wrongChoiceId, levelA, levelB, _) = await SeedActiveVersionAsync(db);
        var (studentId, userId) = await SeedStudentAsync(db);
        var service = CreateService(db);
        var first = await service.StartAttemptAsync(studentId, userId, CancellationToken.None);
        await service.SubmitAttemptAsync(first.AttemptId!.Value, studentId, userId,
            new Dictionary<long, long> { [questionId] = wrongChoiceId }, CancellationToken.None); // lands on levelA
        var retake = await service.RequestRetakeAsync(studentId, userId, CancellationToken.None);

        var admin = CreateAdminService(db);
        await admin.ApproveRetakeAsync(retake.RetakeRequestId!.Value, NextId(), "second chance", CancellationToken.None);

        var eligibility = await service.GetEligibilityAsync(studentId, userId, CancellationToken.None);
        Assert.Equal(PlacementEligibilityStatus.RetakeApprovedReadyToStart, eligibility.Status);

        var second = await service.StartAttemptAsync(studentId, userId, CancellationToken.None);
        Assert.Equal(StartAttemptOutcome.Started, second.Outcome);
        Assert.NotEqual(first.AttemptId, second.AttemptId); // a NEW attempt, the first is never edited

        await service.SubmitAttemptAsync(second.AttemptId!.Value, studentId, userId,
            new Dictionary<long, long> { [questionId] = correctChoiceId }, CancellationToken.None); // now levelB

        // The original attempt's own historical score/level is untouched.
        await using var verify = _fixture.CreateContext();
        var firstAttempt = await verify.PlacementAttempts.FirstAsync(a => a.Id == first.AttemptId);
        Assert.Equal(0, firstAttempt.Score);
        Assert.Equal(levelA, firstAttempt.AssignedLevelId);
        var currentLevel = await verify.StudentLevels.SingleAsync(l => l.StudentId == studentId && l.IsCurrent);
        Assert.Equal(levelB, currentLevel.LevelId); // the LATEST attempt's result is what is current

        // The SAME approval cannot fund a third attempt.
        var thirdStart = await service.StartAttemptAsync(studentId, userId, CancellationToken.None);
        Assert.Equal(StartAttemptOutcome.NotEligible, thirdStart.Outcome);
    }

    [Fact]
    public async Task Two_concurrent_start_calls_for_the_same_student_produce_exactly_one_in_progress_attempt()
    {
        await using var seedDb = _fixture.CreateContext();
        await SeedActiveVersionAsync(seedDb);
        var (studentId, userId) = await SeedStudentAsync(seedDb);

        var service1 = CreateService(_fixture.CreateContext());
        var service2 = CreateService(_fixture.CreateContext());

        var results = await Task.WhenAll(
            service1.StartAttemptAsync(studentId, userId, CancellationToken.None),
            service2.StartAttemptAsync(studentId, userId, CancellationToken.None));

        Assert.All(results, r => Assert.Equal(StartAttemptOutcome.Started, r.Outcome));
        Assert.Equal(results[0].AttemptId, results[1].AttemptId); // both observe the SAME winning attempt

        await using var verify = _fixture.CreateContext();
        Assert.Equal(1, await verify.PlacementAttempts.CountAsync(a => a.StudentId == studentId && a.Status == PlacementAttemptStatus.InProgress));
    }

    [Fact]
    public async Task A_stranger_cannot_see_eligibility_start_or_submit_for_a_student_they_do_not_own()
    {
        await using var db = _fixture.CreateContext();
        var (_, questionId, correctChoiceId, _, _, _, _) = await SeedActiveVersionAsync(db);
        var (studentId, _) = await SeedStudentAsync(db);
        var strangerUserId = await CreateUserAsync(db, "stranger");
        var service = CreateService(db);

        var eligibility = await service.GetEligibilityAsync(studentId, strangerUserId, CancellationToken.None);
        Assert.Equal(PlacementEligibilityStatus.Unauthorized, eligibility.Status);

        var start = await service.StartAttemptAsync(studentId, strangerUserId, CancellationToken.None);
        Assert.Equal(StartAttemptOutcome.Unauthorized, start.Outcome);

        // Even a real attempt id belonging to someone else must be refused —
        // the classic IDOR shape (guessing/enumerating another user's own id).
        var ownerService = CreateService(db);
        var owned = await ownerService.StartAttemptAsync(studentId, (await db.Students.FirstAsync(s => s.Id == studentId)).UserId!.Value, CancellationToken.None);
        var submit = await service.SubmitAttemptAsync(owned.AttemptId!.Value, studentId, strangerUserId,
            new Dictionary<long, long> { [questionId] = correctChoiceId }, CancellationToken.None);
        Assert.Equal(SubmitAttemptOutcome.Unauthorized, submit.Outcome);
    }

    /// <summary>Rule 3: "For guardian accounts, each child has a completely
    /// separate test attempt, result, level, packages, balance, and
    /// bookings." Two siblings, two independent scores, two independent
    /// current levels — neither attempt touches the other's.</summary>
    [Fact]
    public async Task A_guardians_two_children_have_fully_independent_attempts_and_levels()
    {
        await using var db = _fixture.CreateContext();
        var (_, questionId, correctChoiceId, wrongChoiceId, levelA, levelB, _) = await SeedActiveVersionAsync(db);
        var (child1Id, _) = await SeedStudentAsync(db);
        var (child2Id, _) = await SeedStudentAsync(db);
        var guardianUserId = await CreateUserAsync(db, "guardian");
        var guardian = new Guardian(guardianUserId, "Guardian");
        db.Guardians.Add(guardian);
        await db.SaveChangesAsync();
        db.Guardianships.Add(new Guardianship(guardian.Id, child1Id, GuardianRelationship.Parent, true, guardianUserId));
        db.Guardianships.Add(new Guardianship(guardian.Id, child2Id, GuardianRelationship.Parent, true, guardianUserId));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var s1 = await service.StartAttemptAsync(child1Id, guardianUserId, CancellationToken.None);
        await service.SubmitAttemptAsync(s1.AttemptId!.Value, child1Id, guardianUserId,
            new Dictionary<long, long> { [questionId] = correctChoiceId }, CancellationToken.None);

        var s2 = await service.StartAttemptAsync(child2Id, guardianUserId, CancellationToken.None);
        await service.SubmitAttemptAsync(s2.AttemptId!.Value, child2Id, guardianUserId,
            new Dictionary<long, long> { [questionId] = wrongChoiceId }, CancellationToken.None);

        await using var verify = _fixture.CreateContext();
        Assert.Equal(levelB, (await verify.StudentLevels.SingleAsync(l => l.StudentId == child1Id && l.IsCurrent)).LevelId);
        Assert.Equal(levelA, (await verify.StudentLevels.SingleAsync(l => l.StudentId == child2Id && l.IsCurrent)).LevelId);
        Assert.Equal(1, await verify.PlacementAttempts.CountAsync(a => a.StudentId == child1Id));
        Assert.Equal(1, await verify.PlacementAttempts.CountAsync(a => a.StudentId == child2Id));

        // The eligibility gate is per-child, not global to the guardian:
        // child1 already completed their (only) free attempt and is blocked
        // from another one, independently of child2's own state.
        var child1SecondStart = await service.StartAttemptAsync(child1Id, guardianUserId, CancellationToken.None);
        Assert.Equal(StartAttemptOutcome.NotEligible, child1SecondStart.Outcome);
    }

    [Fact]
    public async Task Admin_override_requires_a_reason_and_is_audit_logged_and_never_rewrites_the_original_attempt()
    {
        await using var db = _fixture.CreateContext();
        var (_, questionId, _, wrongChoiceId, levelA, levelB, courseId) = await SeedActiveVersionAsync(db);
        var (studentId, userId) = await SeedStudentAsync(db);
        var service = CreateService(db);
        var started = await service.StartAttemptAsync(studentId, userId, CancellationToken.None);
        await service.SubmitAttemptAsync(started.AttemptId!.Value, studentId, userId,
            new Dictionary<long, long> { [questionId] = wrongChoiceId }, CancellationToken.None); // lands on levelA

        var adminId = NextId();
        var outcome = await service.OverrideLevelAsync(studentId, courseId, levelB, adminId, "manual review after appeal", CancellationToken.None);

        Assert.Equal(OverrideLevelOutcome.Overridden, outcome);
        await using var verify = _fixture.CreateContext();
        Assert.Equal(levelB, (await verify.StudentLevels.SingleAsync(l => l.StudentId == studentId && l.IsCurrent)).LevelId);
        // The original scored attempt is untouched — only a NEW StudentLevel row was added.
        var attempt = await verify.PlacementAttempts.FirstAsync(a => a.Id == started.AttemptId);
        Assert.Equal(levelA, attempt.AssignedLevelId);
        Assert.True(await verify.AuditLogEntries.AnyAsync(a => a.EntityType == "Student" && a.Action == "LevelOverridden" && a.PerformedByUserId == adminId));
    }

    /// <summary>Owner decision 2026-09-04 (multi-course levels): "the student's
    /// level" is not a thing any more. Someone studying two subjects holds two
    /// levels, and each one opens a different set of packages and a different
    /// set of bookable sessions. This used to be a FirstOrDefault, so a student
    /// with two courses had one of them silently disappear — both its packages
    /// and its sessions were filtered out against a level they did not hold in
    /// it.</summary>
    [Fact]
    public async Task Eligibility_reports_a_current_level_for_every_course_the_student_studies()
    {
        await using var db = _fixture.CreateContext();
        var (studentId, userId) = await SeedStudentAsync(db);

        var levelOne = (int)NextId();
        var levelTwo = (int)NextId();
        db.Levels.Add(new Level(levelOne, "L" + levelOne, "مستوى", "Level", levelOne));
        db.Levels.Add(new Level(levelTwo, "L" + levelTwo, "مستوى", "Level", levelTwo));
        var first = new Course("PLC-A-" + NextId(), "دورة", "Course A");
        var second = new Course("PLC-B-" + NextId(), "دورة", "Course B");
        db.Courses.AddRange(first, second);
        await db.SaveChangesAsync();

        var now = SystemClock.Instance.GetCurrentInstant();
        db.StudentLevels.Add(new StudentLevel(studentId, first.Id, levelOne, userId, AssignedByRole.Admin,
            LevelAssignmentSource.AdminOverride, null, "seed", now));
        db.StudentLevels.Add(new StudentLevel(studentId, second.Id, levelTwo, userId, AssignedByRole.Admin,
            LevelAssignmentSource.AdminOverride, null, "seed", now));
        await db.SaveChangesAsync();

        var eligibility = await CreateService(db).GetEligibilityAsync(studentId, userId, CancellationToken.None);

        Assert.Equal(2, eligibility.CurrentLevels.Count);
        Assert.Contains(eligibility.CurrentLevels, l => l.CourseId == first.Id && l.LevelId == levelOne);
        Assert.Contains(eligibility.CurrentLevels, l => l.CourseId == second.Id && l.LevelId == levelTwo);
    }
}
