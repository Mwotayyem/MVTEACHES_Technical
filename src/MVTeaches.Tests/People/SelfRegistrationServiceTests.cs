using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using MVTeaches.Application.People;
using MVTeaches.Domain.Catalog;
using MVTeaches.Domain.People;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.People;
using MVTeaches.Infrastructure.Persistence;
using NodaTime;
using Xunit;

namespace MVTeaches.Tests.People;

/// <summary>
/// Owner decision 2026-09-04: families create their own accounts.
///
/// <para>The rules that matter here are the ones about what a new account can
/// and cannot do. A self-registered account must be no more powerful than an
/// admin-registered one — no level, no package, no sessions — and a guardian's
/// children must be genuinely independent of each other. Both are asserted
/// against the database rather than against the service's own return value.</para>
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class SelfRegistrationServiceTests
{
    private readonly TestDatabaseFixture _fixture;
    private static long _idSeed = 84_000_000; // a range distinct from every other test class sharing this DB

    public SelfRegistrationServiceTests(TestDatabaseFixture fixture) => _fixture = fixture;

    private static long NextId() => Interlocked.Increment(ref _idSeed);

    /// <summary>A 2-character country code that CANNOT collide with a real one.
    /// The usual letter-pair scheme in the other test classes walks straight
    /// through live ISO codes — this class's id range lands on "JO" within a
    /// few increments, which stole Jordan's code from the seeder and broke
    /// LocalDevelopmentSeederTests. A letter followed by a DIGIT is safe by
    /// construction: no ISO 3166 alpha-2 code contains a digit, so nothing the
    /// application seeds can ever want one of these.</summary>
    private static string TwoLetterCode(long seed)
    {
        var n = (int)(seed % 260);
        return string.Concat((char)('A' + n / 10), (char)('0' + n % 10));
    }

    private const string GoodPassword = "CorrectHorseBattery1!";

    private static string FreshEmail() => $"{Guid.NewGuid():N}@test.mvteaches.local";

    /// <summary>Same retry-on-collision reasoning as every other class here:
    /// the 2-letter country-code space is shared by the whole run, so a fixed
    /// range breaks the moment another class adds a test.</summary>
    private static async Task<int> SeedCountryAsync(MvTeachesDbContext db, bool isActive = true)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var countryId = (int)NextId();
            var country = new Country(countryId, TwoLetterCode(countryId), "دولة", "Country", "JOD", "+962", "Asia/Amman");
            db.Countries.Add(country);
            try
            {
                await db.SaveChangesAsync();
                if (!isActive)
                {
                    // Country.IsActive has no domain setter — retiring a country
                    // is not something the application does. Written directly
                    // here purely to construct the state a hostile request would
                    // aim at: an id that exists but is not open for business.
                    await db.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE countries SET is_active = false WHERE \"Id\" = {countryId}");
                    db.ChangeTracker.Clear();
                }

                return countryId;
            }
            catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
            {
                db.ChangeTracker.Clear();
            }
        }

        throw new InvalidOperationException("Could not find a free 2-letter country code after 10 attempts.");
    }

    /// <summary>The same in-memory Identity harness StudentAdmissionServiceTests
    /// builds, for the same reason: the test host never runs Program.cs, so
    /// UserManager and RoleManager have to be composed here. The service under
    /// test is layered on the REAL StudentAdmissionService rather than a fake,
    /// because "self-registration drives the same domain rules as an admin's"
    /// is the property most worth proving.</summary>
    private static (MvTeachesDbContext Db, ISelfRegistrationService Service, Identity Identity) CreateService(
        TestDatabaseFixture fixture)
    {
        var db = fixture.CreateContext();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(db);
        services.AddIdentityCore<ApplicationUser>(options => options.Password.RequiredLength = 10)
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<MvTeachesDbContext>();
        var provider = services.BuildServiceProvider();

        var userManager = provider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = provider.GetRequiredService<RoleManager<ApplicationRole>>();

        var admissions = new StudentAdmissionService(db, userManager, SystemClock.Instance);
        return (db, new SelfRegistrationService(db, userManager, admissions),
            new Identity(userManager, roleManager));
    }

    /// <summary>Roles are normally seeded at app startup by DataSeeder, which
    /// the test host never runs — created idempotently here the same way.</summary>
    private sealed record Identity(UserManager<ApplicationUser> UserManager, RoleManager<ApplicationRole> RoleManager)
    {
        public async Task EnsureRolesExistAsync()
        {
            foreach (var role in RoleNames.All)
            {
                if (!await RoleManager.RoleExistsAsync(role))
                {
                    await RoleManager.CreateAsync(new ApplicationRole(role));
                }
            }
        }
    }

    [Fact]
    public async Task A_guardian_can_register_themselves_with_a_real_login_and_role()
    {
        var (db, service, identity) = CreateService(_fixture);
        await using var _ = db;
        await identity.EnsureRolesExistAsync();
        var countryId = await SeedCountryAsync(db);
        var email = FreshEmail();

        var result = await service.RegisterGuardianAsync(email, GoodPassword, "Self Signed Guardian",
            "+962790001111", countryId, CancellationToken.None);

        Assert.Equal(SelfRegisterOutcome.Registered, result.Outcome);
        var guardian = await db.Guardians.FirstAsync(g => g.Id == result.GuardianId);
        Assert.Equal("Self Signed Guardian", guardian.FullName);

        var user = await db.Users.FirstAsync(u => u.Id == guardian.UserId);
        Assert.Equal(email, user.Email);
        Assert.Equal("+962790001111", user.PhoneNumber);
        Assert.Equal(countryId, user.CountryId);
        Assert.Contains(RoleNames.Guardian, await identity.UserManager.GetRolesAsync(user));

        // They start with nobody attached — children are added afterwards.
        Assert.Equal(0, await db.Guardianships.CountAsync(g => g.GuardianId == guardian.Id));
    }

    /// <summary>An adult signing themselves up gets a login and NO guardian.
    /// Being unlinked is exactly what lets them buy for themselves — a student
    /// with a guardian is blocked from purchasing — so this is a rule about who
    /// pays, not a detail of the data model.</summary>
    [Fact]
    public async Task An_adult_student_registers_with_a_login_and_no_guardian()
    {
        var (db, service, identity) = CreateService(_fixture);
        await using var _ = db;
        await identity.EnsureRolesExistAsync();
        var countryId = await SeedCountryAsync(db);
        var email = FreshEmail();

        var result = await service.RegisterAdultStudentAsync(email, GoodPassword, "Adult Learner",
            new LocalDate(1995, 4, 3), "+962790002222", countryId, CancellationToken.None);

        Assert.Equal(SelfRegisterOutcome.Registered, result.Outcome);
        var student = await db.Students.FirstAsync(s => s.Id == result.StudentId);
        Assert.NotNull(student.UserId);
        Assert.Equal("+962790002222", student.PhoneNumber);
        Assert.Equal(0, await db.Guardianships.CountAsync(g => g.StudentId == student.Id));
        Assert.Contains(RoleNames.Student,
            await identity.UserManager.GetRolesAsync(await db.Users.FirstAsync(u => u.Id == student.UserId)));
    }

    /// <summary>The central safety property: registering yourself grants
    /// NOTHING. No level, no package, no sessions, and PendingVerification
    /// rather than Active — identical to what an admin-registered student
    /// starts with. A self-service door that handed out more than the staffed
    /// one would be the whole risk of this feature.</summary>
    [Fact]
    public async Task A_self_registered_student_starts_with_no_level_no_package_and_no_sessions()
    {
        var (db, service, identity) = CreateService(_fixture);
        await using var _ = db;
        await identity.EnsureRolesExistAsync();
        var countryId = await SeedCountryAsync(db);

        var result = await service.RegisterAdultStudentAsync(FreshEmail(), GoodPassword, "Brand New",
            new LocalDate(1999, 1, 1), "+962790003333", countryId, CancellationToken.None);

        var studentId = result.StudentId!.Value;
        var student = await db.Students.FirstAsync(s => s.Id == studentId);

        Assert.Equal(StudentStatus.PendingVerification, student.Status);
        Assert.Equal(0, await db.StudentLevels.CountAsync(l => l.StudentId == studentId));
        Assert.Equal(0, await db.Subscriptions.CountAsync(s => s.StudentId == studentId));
        Assert.Equal(0, await db.SessionEnrollments.CountAsync(e => e.StudentId == studentId));
        Assert.Equal(0, await db.EntitlementLedgerEntries.CountAsync(l => l.StudentId == studentId));
    }

    /// <summary>A phone number is mandatory in both self-registration flows,
    /// and a refusal writes nothing at all — no orphaned Identity user left
    /// behind to block the email on a second, correct attempt.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Self_registration_without_a_phone_number_is_refused_and_creates_nothing(string phone)
    {
        var (db, service, identity) = CreateService(_fixture);
        await using var _ = db;
        await identity.EnsureRolesExistAsync();
        var countryId = await SeedCountryAsync(db);
        var email = FreshEmail();

        var guardian = await service.RegisterGuardianAsync(email, GoodPassword, "No Phone", phone, countryId,
            CancellationToken.None);
        Assert.Equal(SelfRegisterOutcome.PhoneRequired, guardian.Outcome);

        var student = await service.RegisterAdultStudentAsync(email, GoodPassword, "No Phone",
            new LocalDate(1990, 1, 1), phone, countryId, CancellationToken.None);
        Assert.Equal(SelfRegisterOutcome.PhoneRequired, student.Outcome);

        // The email is still free — the refusal really did write nothing.
        Assert.Equal(0, await db.Users.CountAsync(u => u.Email == email));
    }

    /// <summary>A country id in a request body is not a promise the country
    /// exists or that the centre operates there, so it is re-checked against
    /// the active list server-side.</summary>
    [Fact]
    public async Task Registering_into_an_inactive_country_is_refused()
    {
        var (db, service, identity) = CreateService(_fixture);
        await using var _ = db;
        await identity.EnsureRolesExistAsync();
        var inactiveCountryId = await SeedCountryAsync(db, isActive: false);
        var email = FreshEmail();

        var result = await service.RegisterGuardianAsync(email, GoodPassword, "Wrong Country",
            "+962790004444", inactiveCountryId, CancellationToken.None);

        Assert.Equal(SelfRegisterOutcome.CountryNotAvailable, result.Outcome);
        Assert.Equal(0, await db.Users.CountAsync(u => u.Email == email));
    }

    /// <summary>Identity's own refusal (a duplicate email here) is surfaced
    /// with its reasons rather than flattened into "something went wrong" —
    /// otherwise the person retypes the same address forever.</summary>
    [Fact]
    public async Task A_duplicate_email_is_refused_with_the_reason_attached()
    {
        var (db, service, identity) = CreateService(_fixture);
        await using var _ = db;
        await identity.EnsureRolesExistAsync();
        var countryId = await SeedCountryAsync(db);
        var email = FreshEmail();

        var first = await service.RegisterGuardianAsync(email, GoodPassword, "First", "+962790005555",
            countryId, CancellationToken.None);
        Assert.Equal(SelfRegisterOutcome.Registered, first.Outcome);

        var second = await service.RegisterGuardianAsync(email, GoodPassword, "Second", "+962790006666",
            countryId, CancellationToken.None);

        Assert.Equal(SelfRegisterOutcome.LoginFailed, second.Outcome);
        Assert.NotNull(second.Errors);
        Assert.NotEmpty(second.Errors!);
        Assert.Equal(1, await db.Guardians.CountAsync(g => g.FullName == "First"));
        Assert.Equal(0, await db.Guardians.CountAsync(g => g.FullName == "Second"));
    }

    /// <summary>A guardian adds two children and each is a fully independent
    /// student: separate ids, separate records, and separately linked. The
    /// owner asked specifically that sibling balances never mix, so the
    /// independence is asserted rather than assumed.</summary>
    [Fact]
    public async Task A_guardian_can_add_several_children_and_each_one_is_independent()
    {
        var (db, service, identity) = CreateService(_fixture);
        await using var _ = db;
        await identity.EnsureRolesExistAsync();
        var countryId = await SeedCountryAsync(db);

        var guardian = await service.RegisterGuardianAsync(FreshEmail(), GoodPassword, "Parent of Two",
            "+962790007777", countryId, CancellationToken.None);
        var guardianUserId = (await db.Guardians.FirstAsync(g => g.Id == guardian.GuardianId)).UserId;

        var first = await service.AddOwnChildAsync(guardianUserId, "First Child", new LocalDate(2014, 5, 1),
            phoneNumber: null, countryId, CancellationToken.None);
        var second = await service.AddOwnChildAsync(guardianUserId, "Second Child", new LocalDate(2016, 9, 12),
            phoneNumber: "+962790008888", countryId, CancellationToken.None);

        Assert.Equal(AddOwnChildOutcome.Added, first.Outcome);
        Assert.Equal(AddOwnChildOutcome.Added, second.Outcome);
        Assert.NotEqual(first.StudentId, second.StudentId);

        // Both are linked to this guardian, and to nobody else.
        Assert.Equal(2, await db.Guardianships.CountAsync(g => g.GuardianId == guardian.GuardianId));
        Assert.Equal(1, await db.Guardianships.CountAsync(g => g.StudentId == first.StudentId));
        Assert.Equal(1, await db.Guardianships.CountAsync(g => g.StudentId == second.StudentId));

        // Neither has a login of their own — the ordinary child case.
        Assert.Null((await db.Students.FirstAsync(s => s.Id == first.StudentId)).UserId);
        Assert.Null((await db.Students.FirstAsync(s => s.Id == second.StudentId)).UserId);

        // The optional child phone is recorded when given and left null when not.
        Assert.Null((await db.Students.FirstAsync(s => s.Id == first.StudentId)).PhoneNumber);
        Assert.Equal("+962790008888", (await db.Students.FirstAsync(s => s.Id == second.StudentId)).PhoneNumber);

        // And neither child starts with anything.
        foreach (var childId in new[] { first.StudentId!.Value, second.StudentId!.Value })
        {
            Assert.Equal(0, await db.StudentLevels.CountAsync(l => l.StudentId == childId));
            Assert.Equal(0, await db.Subscriptions.CountAsync(s => s.StudentId == childId));
            Assert.Equal(0, await db.EntitlementLedgerEntries.CountAsync(l => l.StudentId == childId));
        }
    }

    /// <summary>Which guardian is adding the child comes from the signed-in
    /// account, never from the form — an account with no Guardian row is
    /// refused rather than silently creating an unparented student.</summary>
    [Fact]
    public async Task An_account_that_is_not_a_guardian_cannot_add_a_child()
    {
        var (db, service, identity) = CreateService(_fixture);
        await using var _ = db;
        await identity.EnsureRolesExistAsync();
        var countryId = await SeedCountryAsync(db);

        var stranger = new ApplicationUser { UserName = FreshEmail(), Email = FreshEmail() };
        db.Users.Add(stranger);
        await db.SaveChangesAsync();

        var result = await service.AddOwnChildAsync(stranger.Id, "Not Their Child", new LocalDate(2015, 1, 1),
            phoneNumber: null, countryId, CancellationToken.None);

        Assert.Equal(AddOwnChildOutcome.NotAGuardian, result.Outcome);
        Assert.Equal(0, await db.Students.CountAsync(s => s.FullName == "Not Their Child"));
    }
}
