using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MVTeaches.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PlacementTestEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "placement_retake_requests",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    student_id = table.Column<long>(type: "bigint", nullable: false),
                    requested_by = table.Column<long>(type: "bigint", nullable: false),
                    requested_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    decided_by = table.Column<long>(type: "bigint", nullable: true),
                    decided_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    decision_reason = table.Column<string>(type: "text", nullable: true),
                    consumed_by_attempt_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_placement_retake_requests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "placement_test_versions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    title = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    created_by = table.Column<long>(type: "bigint", nullable: false),
                    created_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    published_by = table.Column<long>(type: "bigint", nullable: true),
                    published_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_placement_test_versions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "placement_attempts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    student_id = table.Column<long>(type: "bigint", nullable: false),
                    test_version_id = table.Column<long>(type: "bigint", nullable: false),
                    approved_retake_request_id = table.Column<long>(type: "bigint", nullable: true),
                    started_by = table.Column<long>(type: "bigint", nullable: false),
                    started_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    completed_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    score = table.Column<int>(type: "integer", nullable: true),
                    assigned_level_id = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_placement_attempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_placement_attempts_placement_test_versions_test_version_id",
                        column: x => x.test_version_id,
                        principalTable: "placement_test_versions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "placement_questions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    test_version_id = table.Column<long>(type: "bigint", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    points = table.Column<int>(type: "integer", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_placement_questions", x => x.Id);
                    table.CheckConstraint("ck_placement_question_points_positive", "points > 0");
                    table.ForeignKey(
                        name: "FK_placement_questions_placement_test_versions_test_version_id",
                        column: x => x.test_version_id,
                        principalTable: "placement_test_versions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "placement_score_ranges",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    test_version_id = table.Column<long>(type: "bigint", nullable: false),
                    min_score = table.Column<int>(type: "integer", nullable: false),
                    max_score = table.Column<int>(type: "integer", nullable: false),
                    level_id = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_placement_score_ranges", x => x.Id);
                    table.CheckConstraint("ck_placement_score_range_valid", "max_score >= min_score AND min_score >= 0");
                    table.ForeignKey(
                        name: "FK_placement_score_ranges_levels_level_id",
                        column: x => x.level_id,
                        principalTable: "levels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_placement_score_ranges_placement_test_versions_test_version~",
                        column: x => x.test_version_id,
                        principalTable: "placement_test_versions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "placement_attempt_answers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    attempt_id = table.Column<long>(type: "bigint", nullable: false),
                    question_id = table.Column<long>(type: "bigint", nullable: false),
                    selected_choice_id = table.Column<long>(type: "bigint", nullable: false),
                    is_correct_snapshot = table.Column<bool>(type: "boolean", nullable: false),
                    points_awarded_snapshot = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_placement_attempt_answers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_placement_attempt_answers_placement_attempts_attempt_id",
                        column: x => x.attempt_id,
                        principalTable: "placement_attempts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "placement_answer_choices",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    question_id = table.Column<long>(type: "bigint", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    is_correct = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_placement_answer_choices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_placement_answer_choices_placement_questions_question_id",
                        column: x => x.question_id,
                        principalTable: "placement_questions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_placement_answer_choices_question_id",
                table: "placement_answer_choices",
                column: "question_id");

            migrationBuilder.CreateIndex(
                name: "ux_placement_attempt_answer",
                table: "placement_attempt_answers",
                columns: new[] { "attempt_id", "question_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_placement_attempts_test_version_id",
                table: "placement_attempts",
                column: "test_version_id");

            migrationBuilder.CreateIndex(
                name: "ux_placement_attempt_in_progress",
                table: "placement_attempts",
                column: "student_id",
                unique: true,
                filter: "\"status\" = 'InProgress'");

            migrationBuilder.CreateIndex(
                name: "IX_placement_questions_test_version_id",
                table: "placement_questions",
                column: "test_version_id");

            migrationBuilder.CreateIndex(
                name: "ux_placement_retake_consumed",
                table: "placement_retake_requests",
                column: "consumed_by_attempt_id",
                unique: true,
                filter: "\"consumed_by_attempt_id\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_placement_retake_pending",
                table: "placement_retake_requests",
                column: "student_id",
                unique: true,
                filter: "\"status\" = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_placement_score_ranges_level_id",
                table: "placement_score_ranges",
                column: "level_id");

            migrationBuilder.CreateIndex(
                name: "IX_placement_score_ranges_test_version_id",
                table: "placement_score_ranges",
                column: "test_version_id");

            migrationBuilder.CreateIndex(
                name: "ux_placement_test_active",
                table: "placement_test_versions",
                column: "is_active",
                unique: true,
                filter: "\"is_active\" = true");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "placement_answer_choices");

            migrationBuilder.DropTable(
                name: "placement_attempt_answers");

            migrationBuilder.DropTable(
                name: "placement_retake_requests");

            migrationBuilder.DropTable(
                name: "placement_score_ranges");

            migrationBuilder.DropTable(
                name: "placement_questions");

            migrationBuilder.DropTable(
                name: "placement_attempts");

            migrationBuilder.DropTable(
                name: "placement_test_versions");
        }
    }
}
