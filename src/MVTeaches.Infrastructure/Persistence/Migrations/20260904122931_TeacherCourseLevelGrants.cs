using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MVTeaches.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Owner decision 2026-09-04: a teacher is permitted to teach a COURSE at a
    /// LEVEL, not a level in the abstract. "Authorised for B2" silently
    /// authorised B2 in Spanish and in Quran too, for somebody hired to teach
    /// English — which stopped being harmless the moment the centre taught more
    /// than one subject.
    ///
    /// <para><b>Why this file is hand-written.</b> The scaffolded version added
    /// <c>course_id</c> as NOT NULL with <c>defaultValue: 0</c>, stamping every
    /// existing grant with a course that does not exist, and the foreign key
    /// added moments later would then have failed. The add-nullable / backfill
    /// / set-not-null sequence below is what makes this safe on a database that
    /// already holds grants — Local Staging has fourteen.</para>
    ///
    /// <para><b>What happens to existing grants.</b> Each is backfilled to the
    /// centre's ORIGINAL course — the lowest-id course, the only one that
    /// existed when the grant was made. No grant changes meaning and none is
    /// deleted: a row that said "this teacher may teach B2" now says "this
    /// teacher may teach B2 in the course they were always teaching".</para>
    ///
    /// <para><b>On the index swap.</b> <c>ux_teacher_level</c> (teacher, level)
    /// becomes <c>ux_teacher_course_level</c> (teacher, course, level). The old
    /// shape made "B2 English" and "B2 Spanish" the same row, so granting the
    /// second collided with the first. Since every existing row backfills to
    /// one course, no row that satisfied the old index can violate the new
    /// one.</para>
    ///
    /// <para><b>Down</b> restores the previous schema. It will FAIL on the
    /// unique index if a teacher has by then been granted the same level in two
    /// courses — which is correct: refusing loudly beats silently discarding
    /// one of two real authorisations.</para>
    /// </summary>
    public partial class TeacherCourseLevelGrants : Migration
    {
        /// <summary>The course every pre-existing grant belongs to: the centre's
        /// first. MIN(Id) rather than a hardcoded value, so it is right whatever
        /// ids a given database handed out.</summary>
        private const string OriginalCourseId = "(SELECT MIN(\"Id\") FROM courses)";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_teacher_level",
                table: "teacher_level_assignments");

            // Step 1: nullable, so adding it cannot fail on existing rows.
            migrationBuilder.AddColumn<long>(
                name: "course_id",
                table: "teacher_level_assignments",
                type: "bigint",
                nullable: true);

            // Step 2: backfill. Empty on a fresh database, which is why it is
            // safe to run unconditionally.
            migrationBuilder.Sql(
                $"UPDATE teacher_level_assignments SET course_id = {OriginalCourseId} WHERE course_id IS NULL;");

            // Step 3: only now can it be NOT NULL. A database holding grants but
            // no courses fails here — loudly, and before any foreign key — which
            // is the right outcome for a state this migration exists to forbid.
            migrationBuilder.AlterColumn<long>(
                name: "course_id",
                table: "teacher_level_assignments",
                type: "bigint",
                nullable: false);

            migrationBuilder.CreateIndex(
                name: "IX_teacher_level_assignments_course_id",
                table: "teacher_level_assignments",
                column: "course_id");

            migrationBuilder.CreateIndex(
                name: "ux_teacher_course_level",
                table: "teacher_level_assignments",
                columns: new[] { "teacher_id", "course_id", "level_id" },
                unique: true);

            // Restrict, never Cascade: retiring a course must not erase the
            // record of who was ever permitted to teach it.
            migrationBuilder.AddForeignKey(
                name: "FK_teacher_level_assignments_courses_course_id",
                table: "teacher_level_assignments",
                column: "course_id",
                principalTable: "courses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_teacher_level_assignments_courses_course_id",
                table: "teacher_level_assignments");

            migrationBuilder.DropIndex(
                name: "IX_teacher_level_assignments_course_id",
                table: "teacher_level_assignments");

            migrationBuilder.DropIndex(
                name: "ux_teacher_course_level",
                table: "teacher_level_assignments");

            migrationBuilder.DropColumn(
                name: "course_id",
                table: "teacher_level_assignments");

            // See the class remarks: this fails rather than discarding a real
            // authorisation if a teacher now holds the same level in two courses.
            migrationBuilder.CreateIndex(
                name: "ux_teacher_level",
                table: "teacher_level_assignments",
                columns: new[] { "teacher_id", "level_id" },
                unique: true);
        }
    }
}
