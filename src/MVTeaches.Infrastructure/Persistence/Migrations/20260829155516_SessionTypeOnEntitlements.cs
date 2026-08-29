using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MVTeaches.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SessionTypeOnEntitlements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Owner decision 2026-08-30 rule 4. The system has not launched yet
            // (no real subscriptions/ledger rows exist), but "Group" — never an
            // empty string, which is not a valid SessionType — is used as the
            // backfill default for any pre-existing row on the off chance a dev
            // or staging database already has one, matching the same caution
            // the SessionCapacityMatchesType migration took.
            migrationBuilder.AddColumn<string>(
                name: "session_type",
                table: "subscriptions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Group");

            migrationBuilder.AddColumn<string>(
                name: "session_type",
                table: "entitlement_ledger",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Group");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "session_type",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "session_type",
                table: "entitlement_ledger");
        }
    }
}
