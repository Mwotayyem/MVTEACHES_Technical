using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MVTeaches.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TeacherLevelAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "teacher_level_assignments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    teacher_id = table.Column<long>(type: "bigint", nullable: false),
                    level_id = table.Column<int>(type: "integer", nullable: false),
                    granted_by = table.Column<long>(type: "bigint", nullable: false),
                    granted_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_teacher_level_assignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_teacher_level_assignments_levels_level_id",
                        column: x => x.level_id,
                        principalTable: "levels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_teacher_level_assignments_teachers_teacher_id",
                        column: x => x.teacher_id,
                        principalTable: "teachers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_teacher_level_assignments_level_id",
                table: "teacher_level_assignments",
                column: "level_id");

            migrationBuilder.CreateIndex(
                name: "ux_teacher_level",
                table: "teacher_level_assignments",
                columns: new[] { "teacher_id", "level_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "teacher_level_assignments");
        }
    }
}
