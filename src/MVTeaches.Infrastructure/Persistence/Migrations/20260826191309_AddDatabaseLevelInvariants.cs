using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MVTeaches.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Database-level invariants that have no EF Core fluent-API equivalent.
    /// Kept as its own migration (rather than hand-patched into InitialCreate
    /// each time) so that future `dotnet ef migrations add` calls never
    /// require re-applying this by hand — see JoinAttendanceService's remarks
    /// and Technical Study §14.2/§20.5.
    /// </summary>
    public partial class AddDatabaseLevelInvariants : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ⭐⭐ Technical Study §14.2 — makes a teacher schedule conflict a
            // physical impossibility, not an application-level check.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS btree_gist;");
            migrationBuilder.Sql(@"
                ALTER TABLE class_sessions ADD CONSTRAINT no_teacher_overlap
                  EXCLUDE USING gist (
                      teacher_id WITH =,
                      tstzrange(starts_at_utc, ends_at_utc, '[)') WITH &&
                  ) WHERE (status <> 'Cancelled');");

            // §20.5 rule 1: append-only at the database level, not merely by
            // convention in application code.
            migrationBuilder.Sql(@"
                CREATE OR REPLACE FUNCTION forbid_ledger_mutation() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'entitlement_ledger is append-only (Technical Study §20.5 rule 1) — % is not permitted', TG_OP;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_entitlement_ledger_append_only
                    BEFORE UPDATE OR DELETE ON entitlement_ledger
                    FOR EACH ROW EXECUTE FUNCTION forbid_ledger_mutation();
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_entitlement_ledger_append_only ON entitlement_ledger;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS forbid_ledger_mutation();");
            migrationBuilder.Sql("ALTER TABLE class_sessions DROP CONSTRAINT IF EXISTS no_teacher_overlap;");
        }
    }
}
