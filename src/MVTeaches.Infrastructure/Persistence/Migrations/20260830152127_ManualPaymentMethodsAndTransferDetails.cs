using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MVTeaches.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ManualPaymentMethodsAndTransferDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "payer_display_name",
                table: "payments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "payment_method_config_id",
                table: "payments",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "received_amount",
                table: "payments",
                type: "numeric(12,3)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "received_currency",
                table: "payments",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "supersedes_payment_id",
                table: "payments",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<LocalDate>(
                name: "transfer_date",
                table: "payments",
                type: "date",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "payment_method_configs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    beneficiary_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    cliq_alias = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    iban = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    bank_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    swift_bic = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    country_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    instructions = table.Column<string>(type: "text", nullable: true),
                    accepted_currencies_csv = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_by = table.Column<long>(type: "bigint", nullable: false),
                    created_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    deactivated_by = table.Column<long>(type: "bigint", nullable: true),
                    deactivated_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_method_configs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_payments_supersedes_payment_id",
                table: "payments",
                column: "supersedes_payment_id");

            migrationBuilder.CreateIndex(
                name: "IX_payment_method_configs_type_is_active",
                table: "payment_method_configs",
                columns: new[] { "type", "is_active" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "payment_method_configs");

            migrationBuilder.DropIndex(
                name: "IX_payments_supersedes_payment_id",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "payer_display_name",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "payment_method_config_id",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "received_amount",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "received_currency",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "supersedes_payment_id",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "transfer_date",
                table: "payments");
        }
    }
}
