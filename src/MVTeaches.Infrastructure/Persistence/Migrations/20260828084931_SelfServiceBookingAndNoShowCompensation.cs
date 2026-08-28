using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MVTeaches.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SelfServiceBookingAndNoShowCompensation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<long>(
                name: "marked_by",
                table: "attendance",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            // defaultValue: true, NOT false — every attendance row that could
            // possibly already exist represents a real Join under the pre-2026-08-28
            // logic (NoShow rows did not exist before this migration), so
            // backfilling existing rows as Present is the only correct value.
            migrationBuilder.AddColumn<bool>(
                name: "is_present",
                table: "attendance",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "compensation_requests",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    student_id = table.Column<long>(type: "bigint", nullable: false),
                    original_session_id = table.Column<long>(type: "bigint", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    requested_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    replacement_session_id = table.Column<long>(type: "bigint", nullable: true),
                    rejection_reason = table.Column<string>(type: "text", nullable: true),
                    resolved_by = table.Column<long>(type: "bigint", nullable: true),
                    resolved_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_compensation_requests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_compensation_requests_pending",
                table: "compensation_requests",
                columns: new[] { "status", "requested_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_compensation_request_open",
                table: "compensation_requests",
                columns: new[] { "original_session_id", "student_id" },
                unique: true,
                filter: "\"status\" IN ('Pending', 'Approved')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "compensation_requests");

            migrationBuilder.DropColumn(
                name: "is_present",
                table: "attendance");

            migrationBuilder.AlterColumn<long>(
                name: "marked_by",
                table: "attendance",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);
        }
    }
}
