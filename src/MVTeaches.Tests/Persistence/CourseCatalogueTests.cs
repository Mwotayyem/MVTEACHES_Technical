using MVTeaches.Domain.Catalog;
using MVTeaches.Infrastructure.Persistence;
using Xunit;

namespace MVTeaches.Tests.Persistence;

/// <summary>
/// Owner decision 2026-09-04 (revised): the catalogue is twenty-one named
/// courses and every one of them is levelled on the existing A1-C2 ladder.
///
/// <para>The Arabic names below are typed out again, independently of
/// <see cref="DataSeeder.CourseCatalogue"/>, on purpose. This is a double-entry
/// check: a course quietly dropped, added, or reworded in the seeder shows up
/// here as a failure rather than as a catalogue that no longer matches what the
/// centre actually advertises. The Arabic name is the authority — it is what a
/// parent reads — so it is what is compared.</para>
///
/// <para>No database is needed for any of this, which is the point: these are
/// assertions about a decision, not about a deployment.</para>
/// </summary>
public class CourseCatalogueTests
{
    /// <summary>The owner's list verbatim, in the owner's order.</summary>
    private static readonly string[] OwnersCoursesInArabic =
    {
        // English
        "المحادثة الإنجليزية للأطفال",
        "المحادثة الإنجليزية للكبار",
        "الإنجليزي العام للأطفال",
        "الإنجليزي العام للكبار",
        "دورات تحضيرية للإيلتس IELTS",
        "دورات تأسيسية للإيلتس IELTS",
        "دورات تحضيرية للتوفل TOEFL",
        "دورات تأسيسية للتوفل TOEFL",
        "دورات إدارة الأعمال باللغة الإنجليزية",
        "SAT لجميع الصفوف",
        "IG لجميع الصفوف",

        // Arabic
        "المحادثة العربية للأطفال",
        "المحادثة العربية للكبار",
        "اللغة العربية العامة للأطفال",
        "اللغة العربية العامة للكبار",
        "القرآن الكريم للأطفال",
        "القرآن الكريم للكبار",

        // Spanish
        "المحادثة الإسبانية للأطفال",
        "المحادثة الإسبانية للكبار",
        "اللغة الإسبانية العامة للأطفال",
        "اللغة الإسبانية العامة للكبار",
    };

    [Fact]
    public void The_catalogue_is_exactly_the_owners_twenty_one_courses()
    {
        Assert.Equal(OwnersCoursesInArabic, DataSeeder.CourseCatalogue.Select(c => c.NameAr).ToArray());
    }

    /// <summary>The correction this catalogue exists to carry. An earlier list
    /// marked IELTS, TOEFL and Quran <c>isLeveled: false</c>; the owner ruled
    /// that every course is placed on the same existing ladder. A course that
    /// is not levelled cannot take a student level, a package, or a teacher
    /// grant — so this single flag decides whether a course can be sold at
    /// all.</summary>
    [Fact]
    public void Every_course_in_the_catalogue_is_levelled()
    {
        foreach (var seed in DataSeeder.CourseCatalogue)
        {
            var course = new Course(seed.Code, seed.NameAr, seed.NameEn);
            Assert.True(course.IsLeveled, $"{seed.Code} must be levelled — every course in this catalogue is.");
        }
    }

    /// <summary>Codes are what the rest of the system joins on, and one of them
    /// is looked up as a literal string in two seeders, so a duplicate here
    /// would be silently destructive rather than merely untidy.</summary>
    [Fact]
    public void Course_codes_are_unique_and_every_course_is_named_in_both_languages()
    {
        var codes = DataSeeder.CourseCatalogue.Select(c => c.Code).ToList();
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        Assert.All(DataSeeder.CourseCatalogue, seed =>
        {
            Assert.False(string.IsNullOrWhiteSpace(seed.Code));
            Assert.False(string.IsNullOrWhiteSpace(seed.NameAr));
            Assert.False(string.IsNullOrWhiteSpace(seed.NameEn));
        });
    }

    /// <summary>GENERAL-ENGLISH keeps its code even though its display name
    /// changed, because every course_id already in Local Staging points at that
    /// row and because LocalDevelopmentSeeder and StagingSeeder both look the
    /// course up by this exact literal. Minting a new code for the centre's
    /// original course would orphan real data and break both seeders at once.</summary>
    [Fact]
    public void The_original_general_english_code_is_still_in_the_catalogue()
    {
        Assert.Contains(DataSeeder.CourseCatalogue, c => c.Code == "GENERAL-ENGLISH");
    }
}
