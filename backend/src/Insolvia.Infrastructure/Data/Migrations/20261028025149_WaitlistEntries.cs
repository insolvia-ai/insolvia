using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Insolvia.Data.Migrations
{
    /// <inheritdoc />
    public partial class WaitlistEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "waitlist_entries",
                schema: "insolvia",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    firm = table.Column<string>(type: "text", nullable: false),
                    email = table.Column<string>(type: "text", nullable: false),
                    host = table.Column<string>(type: "text", nullable: true),
                    current_software = table.Column<string>(type: "text", nullable: true),
                    message = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_waitlist_entries", x => x.id);
                    table.CheckConstraint("ck_waitlist_entries_status", "status IN ('new')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_waitlist_entries_submitted_at_id",
                schema: "insolvia",
                table: "waitlist_entries",
                columns: new[] { "submitted_at", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "waitlist_entries",
                schema: "insolvia");
        }
    }
}
