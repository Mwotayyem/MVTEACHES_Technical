using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MVTeaches.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NotificationOutboxSessionId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "session_id",
                table: "notification_outbox",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_notification_outbox_event_session_id_recipient_user_id",
                table: "notification_outbox",
                columns: new[] { "event", "session_id", "recipient_user_id" },
                filter: "\"session_id\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_notification_outbox_event_session_id_recipient_user_id",
                table: "notification_outbox");

            migrationBuilder.DropColumn(
                name: "session_id",
                table: "notification_outbox");
        }
    }
}
