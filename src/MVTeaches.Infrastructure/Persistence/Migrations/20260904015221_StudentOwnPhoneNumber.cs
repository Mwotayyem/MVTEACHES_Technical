using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MVTeaches.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StudentOwnPhoneNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "phone_number",
                table: "students",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "phone_number",
                table: "students");
        }
    }
}
