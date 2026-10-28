using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Insolvia.Data.Migrations
{
    /// <inheritdoc />
    public partial class SealedData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "admin_audit",
                schema: "insolvia",
                columns: table => new
                {
                    event_id = table.Column<string>(type: "text", nullable: false),
                    firm_id = table.Column<string>(type: "text", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    principal = table.Column<string>(type: "text", nullable: false),
                    principal_email = table.Column<string>(type: "text", nullable: false),
                    detail = table.Column<string>(type: "text", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_admin_audit", x => x.event_id);
                    table.CheckConstraint("ck_admin_audit_action", "action IN ('firm.provision', 'firm.suspend', 'firm.reactivate', 'invite.resend')");
                    table.ForeignKey(
                        name: "fk_admin_audit_firm",
                        column: x => x.firm_id,
                        principalSchema: "insolvia",
                        principalTable: "firms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "filing_authorizations",
                schema: "insolvia",
                columns: table => new
                {
                    authorization_id = table.Column<string>(type: "text", nullable: false),
                    firm_id = table.Column<string>(type: "text", nullable: false),
                    attorney_id = table.Column<string>(type: "text", nullable: false),
                    text_version = table.Column<string>(type: "text", nullable: false),
                    text_digest = table.Column<string>(type: "text", nullable: false),
                    signed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    authenticated_at = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    withdrawn_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_filing_authorizations", x => x.authorization_id);
                    table.UniqueConstraint("ak_filing_authorizations_owner", x => new { x.authorization_id, x.firm_id, x.attorney_id });
                    table.CheckConstraint("ck_filing_authorizations_status", "status IN ('signed', 'superseded', 'withdrawn')");
                    table.CheckConstraint("ck_filing_authorizations_withdrawn", "(status = 'withdrawn') = (withdrawn_at IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_filing_authorizations_firm",
                        column: x => x.firm_id,
                        principalSchema: "insolvia",
                        principalTable: "firms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "current_filing_authorizations",
                schema: "insolvia",
                columns: table => new
                {
                    firm_id = table.Column<string>(type: "text", nullable: false),
                    attorney_id = table.Column<string>(type: "text", nullable: false),
                    authorization_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_current_filing_authorizations", x => new { x.firm_id, x.attorney_id });
                    table.ForeignKey(
                        name: "fk_current_filing_authorizations_authorization",
                        columns: x => new { x.authorization_id, x.firm_id, x.attorney_id },
                        principalSchema: "insolvia",
                        principalTable: "filing_authorizations",
                        principalColumns: new[] { "authorization_id", "firm_id", "attorney_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_current_filing_authorizations_firm",
                        column: x => x.firm_id,
                        principalSchema: "insolvia",
                        principalTable: "firms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "filing_credentials",
                schema: "insolvia",
                columns: table => new
                {
                    firm_id = table.Column<string>(type: "text", nullable: false),
                    attorney_id = table.Column<string>(type: "text", nullable: false),
                    credential_id = table.Column<string>(type: "text", nullable: false),
                    login = table.Column<string>(type: "text", nullable: false),
                    courts = table.Column<string[]>(type: "text[]", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    scheme = table.Column<string>(type: "text", nullable: false),
                    ciphertext = table.Column<byte[]>(type: "bytea", nullable: false),
                    nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    wrapped_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    authorization_ref = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_filing_credentials", x => new { x.firm_id, x.attorney_id, x.credential_id });
                    table.CheckConstraint("ck_filing_credentials_status", "status IN ('active')");
                    table.ForeignKey(
                        name: "fk_filing_credentials_authorization",
                        columns: x => new { x.authorization_ref, x.firm_id, x.attorney_id },
                        principalSchema: "insolvia",
                        principalTable: "filing_authorizations",
                        principalColumns: new[] { "authorization_id", "firm_id", "attorney_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_filing_credentials_firm",
                        column: x => x.firm_id,
                        principalSchema: "insolvia",
                        principalTable: "firms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_admin_audit_firm_id_recorded_at_event_id",
                schema: "insolvia",
                table: "admin_audit",
                columns: new[] { "firm_id", "recorded_at", "event_id" });

            migrationBuilder.CreateIndex(
                name: "ix_current_filing_authorizations_authorization_id_firm_id_atto",
                schema: "insolvia",
                table: "current_filing_authorizations",
                columns: new[] { "authorization_id", "firm_id", "attorney_id" });

            migrationBuilder.CreateIndex(
                name: "ix_filing_authorizations_firm_id_attorney_id_signed_at",
                schema: "insolvia",
                table: "filing_authorizations",
                columns: new[] { "firm_id", "attorney_id", "signed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_filing_credentials_authorization_ref_firm_id_attorney_id",
                schema: "insolvia",
                table: "filing_credentials",
                columns: new[] { "authorization_ref", "firm_id", "attorney_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "admin_audit",
                schema: "insolvia");

            migrationBuilder.DropTable(
                name: "current_filing_authorizations",
                schema: "insolvia");

            migrationBuilder.DropTable(
                name: "filing_credentials",
                schema: "insolvia");

            migrationBuilder.DropTable(
                name: "filing_authorizations",
                schema: "insolvia");
        }
    }
}
