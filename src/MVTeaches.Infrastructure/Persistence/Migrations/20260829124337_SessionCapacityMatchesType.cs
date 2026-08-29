using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MVTeaches.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SessionCapacityMatchesType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Owner decision 2026-08-30: capacity is fixed by session type
            // (Group = 4, Private/Placement = 1) and is no longer accepted from
            // any caller. Existing rows predate that rule and may hold any value
            // in the old 1..10 band, so they are normalised FIRST — adding the
            // CHECK to a table containing a violating row would abort the
            // migration outright.
            //
            // Widening a group session up to 4 cannot over-fill it: seats_taken
            // is untouched and ck_session_seats (seats_taken <= capacity) still
            // holds. Narrowing DOWN to 4 is the only genuinely unsafe case, so a
            // group session that already has more than 4 seats taken aborts the
            // migration with an actionable message rather than being silently
            // truncated into an inconsistent state.
            migrationBuilder.Sql(@"
                DO $$
                DECLARE oversubscribed int;
                BEGIN
                    SELECT count(*) INTO oversubscribed
                    FROM class_sessions
                    WHERE session_type = 'Group' AND seats_taken > 4;

                    IF oversubscribed > 0 THEN
                        RAISE EXCEPTION
                            'Cannot enforce Group capacity = 4: % existing group session(s) already have more than 4 seats taken. Resolve these manually before applying this migration.',
                            oversubscribed;
                    END IF;
                END $$;");

            migrationBuilder.Sql(
                "UPDATE class_sessions SET capacity = 4 WHERE session_type = 'Group' AND capacity <> 4;");
            migrationBuilder.Sql(
                "UPDATE class_sessions SET capacity = 1 WHERE session_type IN ('Private', 'Placement') AND capacity <> 1;");

            migrationBuilder.AddCheckConstraint(
                name: "ck_session_capacity_matches_type",
                table: "class_sessions",
                sql: "(session_type = 'Group' AND capacity = 4) OR (session_type IN ('Private', 'Placement') AND capacity = 1)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_session_capacity_matches_type",
                table: "class_sessions");
        }
    }
}
