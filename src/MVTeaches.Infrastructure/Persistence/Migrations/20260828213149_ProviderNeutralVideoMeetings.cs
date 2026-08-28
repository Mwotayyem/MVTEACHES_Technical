using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MVTeaches.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProviderNeutralVideoMeetings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "oauth_authorization_states",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    teacher_id = table.Column<long>(type: "bigint", nullable: false),
                    state_token = table.Column<string>(type: "text", nullable: false),
                    code_verifier = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    consumed_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oauth_authorization_states", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "provisioned_meetings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    session_id = table.Column<long>(type: "bigint", nullable: false),
                    connection_id = table.Column<long>(type: "bigint", nullable: false),
                    provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    external_meeting_id = table.Column<string>(type: "text", nullable: true),
                    join_url = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status_detail = table.Column<string>(type: "text", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    superseded_by_meeting_id = table.Column<long>(type: "bigint", nullable: true),
                    claimed_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    claim_token = table.Column<Guid>(type: "uuid", nullable: true),
                    provisioned_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provisioned_meetings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "teacher_meeting_connections",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    teacher_id = table.Column<long>(type: "bigint", nullable: false),
                    provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    external_account_id = table.Column<string>(type: "text", nullable: false),
                    external_account_email = table.Column<string>(type: "text", nullable: true),
                    encrypted_access_token = table.Column<string>(type: "text", nullable: false),
                    encrypted_refresh_token = table.Column<string>(type: "text", nullable: true),
                    access_token_expires_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    token_version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    capability_tier = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    capability_minutes_limit = table.Column<int>(type: "integer", nullable: true),
                    capability_verified_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status_detail = table.Column<string>(type: "text", nullable: true),
                    is_default = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    connected_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    disconnected_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_teacher_meeting_connections", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_oauth_state_token",
                table: "oauth_authorization_states",
                column: "state_token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_provisioned_meetings_connection_id",
                table: "provisioned_meetings",
                column: "connection_id");

            migrationBuilder.CreateIndex(
                name: "ux_provisioned_meeting_active_session",
                table: "provisioned_meetings",
                column: "session_id",
                unique: true,
                filter: "\"is_active\" = true");

            migrationBuilder.CreateIndex(
                name: "ux_teacher_meeting_connection",
                table: "teacher_meeting_connections",
                columns: new[] { "teacher_id", "provider" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_teacher_meeting_connection_default",
                table: "teacher_meeting_connections",
                column: "teacher_id",
                unique: true,
                filter: "\"is_default\" = true");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "oauth_authorization_states");

            migrationBuilder.DropTable(
                name: "provisioned_meetings");

            migrationBuilder.DropTable(
                name: "teacher_meeting_connections");
        }
    }
}
