using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MVTeaches.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Owner decision 2026-09-04 (multi-course levels): a level belongs to a
    /// course. A student who is B2 in English may be A1 in Spanish, and the
    /// single global "current level" this schema enforced could not express
    /// that — it silently made every second course inherit the first one's
    /// level.
    ///
    /// <para><b>Why this file is hand-written.</b> The scaffolded version added
    /// <c>course_id</c> as NOT NULL with <c>defaultValue: 0</c>, which would
    /// have stamped every existing row with course 0 — not a real course — and
    /// the foreign key added moments later would then have failed outright.
    /// The three-step add-nullable / backfill / set-not-null sequence below is
    /// what makes this safe on a database that already holds data.</para>
    ///
    /// <para><b>What happens to existing rows.</b> Every <c>student_levels</c>
    /// and <c>placement_test_versions</c> row is backfilled to the centre's
    /// ORIGINAL course — the lowest-id course, which is the only one that
    /// existed when those rows were written. No row changes meaning, nothing is
    /// deleted, and no level history is rewritten: a row that said "this
    /// student is B2" now says "this student is B2 in the course they were
    /// always in".</para>
    ///
    /// <para><b>On the index swap.</b> <c>ux_student_current_level</c>
    /// (unique on student_id where is_current) becomes
    /// <c>ux_student_course_current_level</c> (unique on student_id, course_id
    /// where is_current). The guarantee is unchanged in kind — still exactly
    /// one current row — only its scope moves from the student to the
    /// (student, course) pair. Since every existing row is backfilled to the
    /// same single course, no row that satisfied the old index can violate the
    /// new one.</para>
    ///
    /// <para><b>Down</b> restores the previous schema exactly. It is safe only
    /// while every student still has at most one course's level, which is true
    /// immediately after Up; once a student really has been placed in a second
    /// course, Down fails loudly on the unique index rather than silently
    /// discarding somebody's placement. That is the correct behaviour for a
    /// data change that is irreversible in practice.</para>
    /// </summary>
    public partial class MultiCourseStudentLevels : Migration
    {
        /// <summary>The course every pre-existing row belongs to: the centre's
        /// first course. MIN(Id) rather than a hardcoded 1 or a code literal,
        /// so this is correct whatever ids a given database happens to have
        /// handed out.</summary>
        private const string OriginalCourseId = "(SELECT MIN(\"Id\") FROM courses)";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ---- student_levels ------------------------------------------
            migrationBuilder.DropIndex(
                name: "ux_student_current_level",
                table: "student_levels");

            // Step 1: nullable, so adding it cannot fail on existing rows.
            migrationBuilder.AddColumn<long>(
                name: "course_id",
                table: "student_levels",
                type: "bigint",
                nullable: true);

            // Step 2: backfill. On a fresh database (the test databases, a new
            // deployment) both tables are empty and this updates nothing, which
            // is why it is safe to run unconditionally.
            migrationBuilder.Sql(
                $"UPDATE student_levels SET course_id = {OriginalCourseId} WHERE course_id IS NULL;");

            // Step 3: only now can it be NOT NULL. If a database somehow held
            // level rows but no courses at all, this fails here — loudly, and
            // before any foreign key is added — which is the right outcome: a
            // level with no course is exactly the state this migration exists
            // to make impossible.
            migrationBuilder.AlterColumn<long>(
                name: "course_id",
                table: "student_levels",
                type: "bigint",
                nullable: false);

            // ---- placement_test_versions ---------------------------------
            // Same three steps, same reason: a test places into one course's
            // level ladder, and every existing version placed into the original
            // course because it was the only one there was.
            migrationBuilder.AddColumn<long>(
                name: "course_id",
                table: "placement_test_versions",
                type: "bigint",
                nullable: true);

            migrationBuilder.Sql(
                $"UPDATE placement_test_versions SET course_id = {OriginalCourseId} WHERE course_id IS NULL;");

            migrationBuilder.AlterColumn<long>(
                name: "course_id",
                table: "placement_test_versions",
                type: "bigint",
                nullable: false);

            // ---- indexes and foreign keys --------------------------------
            migrationBuilder.CreateIndex(
                name: "IX_student_levels_course_id",
                table: "student_levels",
                column: "course_id");

            migrationBuilder.CreateIndex(
                name: "ux_student_course_current_level",
                table: "student_levels",
                columns: new[] { "student_id", "course_id" },
                unique: true,
                filter: "\"is_current\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_placement_test_versions_course_id",
                table: "placement_test_versions",
                column: "course_id");

            // Restrict, never Cascade: retiring a course must not erase the
            // level history of every student who ever took it.
            migrationBuilder.AddForeignKey(
                name: "FK_placement_test_versions_courses_course_id",
                table: "placement_test_versions",
                column: "course_id",
                principalTable: "courses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_student_levels_courses_course_id",
                table: "student_levels",
                column: "course_id",
                principalTable: "courses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_placement_test_versions_courses_course_id",
                table: "placement_test_versions");

            migrationBuilder.DropForeignKey(
                name: "FK_student_levels_courses_course_id",
                table: "student_levels");

            migrationBuilder.DropIndex(
                name: "IX_student_levels_course_id",
                table: "student_levels");

            migrationBuilder.DropIndex(
                name: "ux_student_course_current_level",
                table: "student_levels");

            migrationBuilder.DropIndex(
                name: "IX_placement_test_versions_course_id",
                table: "placement_test_versions");

            migrationBuilder.DropColumn(
                name: "course_id",
                table: "student_levels");

            migrationBuilder.DropColumn(
                name: "course_id",
                table: "placement_test_versions");

            // See the class remarks: this recreation fails rather than
            // discarding data if a student has since been placed in more than
            // one course.
            migrationBuilder.CreateIndex(
                name: "ux_student_current_level",
                table: "student_levels",
                column: "student_id",
                unique: true,
                filter: "\"is_current\" = true");
        }
    }
}
