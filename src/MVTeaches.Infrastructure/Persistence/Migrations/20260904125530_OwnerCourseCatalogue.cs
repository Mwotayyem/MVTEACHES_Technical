using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MVTeaches.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Owner decision 2026-09-04 (revised): the centre's catalogue is twenty-one
    /// named courses and <b>every one of them is levelled on the existing A1-C2
    /// ladder</b>. An earlier list seeded the same day marked IELTS, TOEFL and
    /// Quran as level-less; the owner corrected that outright, so those three
    /// carry levels like every other course and no new level scheme exists.
    ///
    /// <para><b>This migration changes no schema at all — it is a one-time data
    /// fix, and nothing else.</b> The twenty-one courses themselves are seeded
    /// by <c>DataSeeder.CourseCatalogue</c>, which adds a row only when its code
    /// is missing. What the seeder cannot do is correct a row that already
    /// exists, because <c>Course</c> exposes no setters and a seeder that
    /// rewrote names on every start-up would overwrite the admin's own edits
    /// forever. That correction belongs here, where it happens exactly once.</para>
    ///
    /// <para><b>What it touches.</b> Two things, both narrow:</para>
    /// <list type="number">
    /// <item>The seven course codes seeded earlier today are brought onto the
    /// owner's own names — but ONLY while the row still carries the exact
    /// Arabic AND English name that seeder wrote. An admin who has since
    /// renamed a course keeps their name; this migration silently skips it.</item>
    /// <item><c>is_leveled</c> is set true wherever it is false. That is the
    /// owner's decision applied directly, and it is what makes it possible to
    /// place a student, publish a package, or authorise a teacher in the three
    /// courses that were previously excluded.</item>
    /// </list>
    ///
    /// <para><b>What it deliberately does NOT touch.</b> No course is deleted,
    /// deactivated, merged or re-coded. In particular GENERAL-ENGLISH keeps its
    /// code and its id: every <c>course_id</c> in Local Staging — student
    /// levels, sessions, subscriptions, teacher grants — points at that row, so
    /// it is renamed in place rather than replaced. No <c>student_levels</c>,
    /// <c>class_sessions</c>, <c>pricing_plans</c> or
    /// <c>teacher_level_assignments</c> row is read or written here.</para>
    ///
    /// <para><b>On the one judgement call.</b> GENERAL-ENGLISH's old name maps
    /// to the owner's adults' general-English course rather than the kids' one.
    /// The owner's list splits general English in two and the old row named
    /// neither — the adults' course is the reading that keeps the centre's
    /// existing paying students where they are. It is a label on one row and
    /// nothing in code follows from it: if those students were in fact the
    /// kids' course, an admin renames it from the control panel and no data
    /// moves.</para>
    ///
    /// <para><b>Down</b> restores the seven previous names under the same
    /// "only if untouched" guard, and puts IELTS/TOEFL/Quran back to
    /// level-less. It deletes nothing — the fourteen courses added alongside
    /// this migration by the seeder stay, because by the time anyone rolls
    /// back they may already carry students, packages and sessions.</para>
    /// </summary>
    public partial class OwnerCourseCatalogue : Migration
    {
        /// <summary>code, previous ar, previous en, owner's ar, owner's en.</summary>
        private static readonly (string Code, string OldAr, string OldEn, string NewAr, string NewEn)[] Renames =
        {
            ("GENERAL-ENGLISH", "تقوية إنجليزي عام", "General English", "الإنجليزي العام للكبار", "General English - Adults"),
            ("ARABIC", "اللغة العربية", "Arabic", "اللغة العربية العامة للكبار", "General Arabic - Adults"),
            ("SPANISH", "اللغة الإسبانية", "Spanish", "اللغة الإسبانية العامة للكبار", "General Spanish - Adults"),
            ("BUSINESS-ENGLISH", "إنجليزي الأعمال", "Business English", "دورات إدارة الأعمال باللغة الإنجليزية", "Business English"),
            ("IELTS", "آيلتس", "IELTS", "دورات تحضيرية للإيلتس IELTS", "IELTS Preparation"),
            ("TOEFL", "توفل", "TOEFL", "دورات تحضيرية للتوفل TOEFL", "TOEFL Preparation"),
            ("QURAN", "تحفيظ القرآن الكريم", "Quran", "القرآن الكريم للكبار", "Holy Quran - Adults"),
        };

        /// <summary>The three the earlier list wrongly marked level-less.</summary>
        private static readonly string[] PreviouslyUnlevelled = { "IELTS", "TOEFL", "QURAN" };

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var (code, oldAr, oldEn, newAr, newEn) in Renames)
            {
                Rename(migrationBuilder, code, fromAr: oldAr, fromEn: oldEn, toAr: newAr, toEn: newEn);
            }

            // The owner's decision, applied to every course at once rather than
            // to a named few: there is no such thing as a level-less course in
            // this catalogue, so anything still false is wrong whichever row it is.
            migrationBuilder.Sql("UPDATE courses SET is_leveled = true WHERE is_leveled = false;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var (code, oldAr, oldEn, newAr, newEn) in Renames)
            {
                Rename(migrationBuilder, code, fromAr: newAr, fromEn: newEn, toAr: oldAr, toEn: oldEn);
            }

            foreach (var code in PreviouslyUnlevelled)
            {
                migrationBuilder.Sql($"UPDATE courses SET is_leveled = false WHERE code = '{Quote(code)}';");
            }
        }

        /// <summary>Renames one course only while it still carries BOTH of the
        /// names it is expected to have — an admin's own rename is never
        /// overwritten, it simply matches nothing and the statement is a no-op.
        /// Every value interpolated here is a literal from the array above,
        /// never anything read from the database or supplied by a user.</summary>
        private static void Rename(MigrationBuilder migrationBuilder, string code,
            string fromAr, string fromEn, string toAr, string toEn) =>
            migrationBuilder.Sql(
                $"UPDATE courses SET name_ar = '{Quote(toAr)}', name_en = '{Quote(toEn)}' " +
                $"WHERE code = '{Quote(code)}' AND name_ar = '{Quote(fromAr)}' AND name_en = '{Quote(fromEn)}';");

        /// <summary>PostgreSQL string-literal escaping. None of these names
        /// contains an apostrophe today; this is here so that adding one later
        /// cannot quietly produce broken SQL.</summary>
        private static string Quote(string value) => value.Replace("'", "''");
    }
}
