using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MVTeaches.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OperatingExpenses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "operating_expenses",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    country_id = table.Column<int>(type: "integer", nullable: false),
                    category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(12,3)", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    incurred_on = table.Column<LocalDate>(type: "date", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    entered_by = table.Column<long>(type: "bigint", nullable: false),
                    created_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_operating_expenses", x => x.Id);
                    table.CheckConstraint("ck_operating_expense_amount_positive", "amount > 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_operating_expenses_incurred_on",
                table: "operating_expenses",
                column: "incurred_on");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "operating_expenses");
        }
    }
}
