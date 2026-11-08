using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Insolvia.Data.Migrations
{
    /// <summary>
    /// The pipeline's first migration: the <c>insolvia</c> schema, and no
    /// tables and no extensions. Domain tables arrive with their stores (18.10+).
    /// </summary>
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(name: "insolvia");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The schema also holds the migrations history table, so it is not
            // dropped: reverting this migration leaves an empty schema behind.
        }
    }
}
