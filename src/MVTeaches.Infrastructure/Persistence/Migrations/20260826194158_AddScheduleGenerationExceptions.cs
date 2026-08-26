using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MVTeaches.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduleGenerationExceptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "schedule_generation_exceptions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    recurring_id = table.Column<long>(type: "bigint", nullable: false),
                    occurrence_date = table.Column<LocalDate>(type: "date", nullable: false),
                    reason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    detail = table.Column<string>(type: "text", nullable: false),
                    detected_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    resolved = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    resolved_by = table.Column<long>(type: "bigint", nullable: true),
                    resolved_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_schedule_generation_exceptions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_schedule_generation_exceptions_resolved_detected_at_utc",
                table: "schedule_generation_exceptions",
                columns: new[] { "resolved", "detected_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_schedule_generation_exception",
                table: "schedule_generation_exceptions",
                columns: new[] { "recurring_id", "occurrence_date" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "schedule_generation_exceptions");
        }
    }
}
