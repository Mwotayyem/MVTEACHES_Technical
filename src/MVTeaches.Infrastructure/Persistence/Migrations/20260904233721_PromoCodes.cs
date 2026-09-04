using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MVTeaches.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PromoCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "discount_amount",
                table: "subscriptions",
                type: "numeric(12,3)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "discount_percent",
                table: "subscriptions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "list_price_amount",
                table: "subscriptions",
                type: "numeric(12,3)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<long>(
                name: "promo_code_id",
                table: "subscriptions",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "promo_codes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    code = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: false),
                    discount_percent = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    starts_on = table.Column<LocalDate>(type: "date", nullable: true),
                    ends_on = table.Column<LocalDate>(type: "date", nullable: true),
                    max_total_uses = table.Column<int>(type: "integer", nullable: true),
                    max_uses_per_student = table.Column<int>(type: "integer", nullable: true),
                    created_by = table.Column<long>(type: "bigint", nullable: false),
                    created_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_promo_codes", x => x.Id);
                    table.CheckConstraint("ck_promo_discount_percent", "discount_percent BETWEEN 1 AND 100");
                    table.CheckConstraint("ck_promo_max_total_uses", "max_total_uses IS NULL OR max_total_uses >= 1");
                    table.CheckConstraint("ck_promo_max_uses_per_student", "max_uses_per_student IS NULL OR max_uses_per_student >= 1");
                    table.CheckConstraint("ck_promo_window", "ends_on IS NULL OR starts_on IS NULL OR ends_on >= starts_on");
                });

            migrationBuilder.CreateTable(
                name: "promo_code_plans",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    promo_code_id = table.Column<long>(type: "bigint", nullable: false),
                    pricing_plan_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_promo_code_plans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_promo_code_plans_promo_codes_promo_code_id",
                        column: x => x.promo_code_id,
                        principalTable: "promo_codes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_subscriptions_promo_code_id",
                table: "subscriptions",
                column: "promo_code_id");

            migrationBuilder.CreateIndex(
                name: "ux_promo_code_plan",
                table: "promo_code_plans",
                columns: new[] { "promo_code_id", "pricing_plan_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_promo_codes_code",
                table: "promo_codes",
                column: "code",
                unique: true);

            // Every subscription that predates this column was bought at its
            // full price, so its price before the discount IS its price. Left
            // at the column default of 0 it would read as "this package used to
            // cost nothing", which is worse than wrong on a money screen -
            // list_price_amount is displayed directly beside price_amount.
            //
            // Data-only, idempotent, and touches nothing else: no row's
            // price_amount, status, or ledger is altered.
            migrationBuilder.Sql(
                "UPDATE subscriptions SET list_price_amount = price_amount WHERE list_price_amount = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "promo_code_plans");

            migrationBuilder.DropTable(
                name: "promo_codes");

            migrationBuilder.DropIndex(
                name: "IX_subscriptions_promo_code_id",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "discount_amount",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "discount_percent",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "list_price_amount",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "promo_code_id",
                table: "subscriptions");
        }
    }
}
