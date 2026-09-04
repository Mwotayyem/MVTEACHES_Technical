using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MVTeaches.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Owner decision 2026-09-04: how many students a GROUP session seats is
    /// the centre's to choose, per class. Four was never a fact about teaching
    /// — it was one room's size written into a CHECK constraint, and it stopped
    /// the centre running a class of six.
    ///
    /// <para><b>This migration only widens.</b> No column is added or dropped,
    /// no row is read or written, and every session already in the database
    /// (group at 4, private and placement at 1) satisfies the new constraints
    /// unchanged. That is what makes it safe to run against live data with no
    /// backfill at all.</para>
    ///
    /// <para><b>What is deliberately NOT relaxed:</b> Private and Placement are
    /// still pinned to exactly 1. One-to-one is what those session types MEAN —
    /// a "private" lesson seating two is a group lesson wearing the wrong
    /// label, and it would be priced and paid as the wrong thing. Only the
    /// Group half of the constraint opens up.</para>
    ///
    /// <para>The band's ceiling moves 10 → 50 to match
    /// ClassSession.MaximumGroupCapacity. That is a sanity bound against a
    /// typo, not a business rule the owner set; raising it means raising both
    /// together.</para>
    ///
    /// <para><b>Down</b> restores the old constraints and will FAIL if a group
    /// session with any seat count other than 4 exists by then. That is the
    /// correct outcome: refusing loudly is better than a migration that cannot
    /// say what it would do to a class of six.</para>
    /// </summary>
    public partial class AdminChosenSessionCapacity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_session_capacity_band",
                table: "class_sessions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_session_capacity_matches_type",
                table: "class_sessions");

            migrationBuilder.AddCheckConstraint(
                name: "ck_session_capacity_band",
                table: "class_sessions",
                sql: "capacity BETWEEN 1 AND 50");

            migrationBuilder.AddCheckConstraint(
                name: "ck_session_capacity_matches_type",
                table: "class_sessions",
                sql: "(session_type = 'Group' AND capacity >= 1) OR (session_type IN ('Private', 'Placement') AND capacity = 1)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_session_capacity_band",
                table: "class_sessions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_session_capacity_matches_type",
                table: "class_sessions");

            migrationBuilder.AddCheckConstraint(
                name: "ck_session_capacity_band",
                table: "class_sessions",
                sql: "capacity BETWEEN 1 AND 10");

            migrationBuilder.AddCheckConstraint(
                name: "ck_session_capacity_matches_type",
                table: "class_sessions",
                sql: "(session_type = 'Group' AND capacity = 4) OR (session_type IN ('Private', 'Placement') AND capacity = 1)");
        }
    }
}
