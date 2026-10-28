using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Insolvia.Data.Migrations
{
    /// <summary>
    /// The firm stores (18.10): firms, firm users, library creditors, firm
    /// clients, the questionnaire config and document checklist, the portal
    /// client bindings (authoritative row, case-side mirror, role claims) and
    /// the append-only access log. Grants are the migrator's, not this file's.
    /// </summary>
    public partial class FirmStores : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "insolvia");

            migrationBuilder.CreateTable(
                name: "access_log",
                schema: "insolvia",
                columns: table => new
                {
                    event_id = table.Column<string>(type: "text", nullable: false),
                    subject_kind = table.Column<string>(type: "text", nullable: false),
                    subject_id = table.Column<string>(type: "text", nullable: false),
                    principal = table.Column<string>(type: "text", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    outcome = table.Column<string>(type: "text", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    filing_role = table.Column<string>(type: "text", nullable: true),
                    purpose = table.Column<string>(type: "text", nullable: true),
                    roles = table.Column<string>(type: "text", nullable: true),
                    filing_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_access_log", x => x.event_id);
                    table.CheckConstraint("ck_access_log_outcome", "outcome IN ('allowed', 'denied')");
                    table.CheckConstraint("ck_access_log_subject_kind", "subject_kind IN ('CASE', 'CLIENT', 'CREDENTIAL', 'AUTHORIZATION')");
                });

            migrationBuilder.CreateTable(
                name: "firms",
                schema: "insolvia",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: true),
                    created_by_email = table.Column<string>(type: "text", nullable: true),
                    default_court = table.Column<string>(type: "text", nullable: true),
                    default_division = table.Column<string>(type: "text", nullable: true),
                    default_chapter = table.Column<int>(type: "integer", nullable: true),
                    letterhead = table.Column<JsonDocument>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_firms", x => x.id);
                    table.CheckConstraint("ck_firms_default_court", "(default_court IS NULL) = (default_division IS NULL)");
                    table.CheckConstraint("ck_firms_status", "status IN ('active', 'suspended')");
                });

            migrationBuilder.CreateTable(
                name: "case_client_bindings",
                schema: "insolvia",
                columns: table => new
                {
                    case_id = table.Column<string>(type: "text", nullable: false),
                    subject = table.Column<string>(type: "text", nullable: false),
                    firm_id = table.Column<string>(type: "text", nullable: false),
                    email = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    roles = table.Column<string[]>(type: "text[]", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    invited_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_case_client_bindings", x => new { x.case_id, x.subject });
                    table.CheckConstraint("ck_case_client_bindings_status", "status IN ('invited', 'active', 'revoked')");
                    table.ForeignKey(
                        name: "fk_case_client_bindings_firm",
                        column: x => x.firm_id,
                        principalSchema: "insolvia",
                        principalTable: "firms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "case_client_role_claims",
                schema: "insolvia",
                columns: table => new
                {
                    case_id = table.Column<string>(type: "text", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    firm_id = table.Column<string>(type: "text", nullable: false),
                    subject = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_case_client_role_claims", x => new { x.case_id, x.role });
                    table.CheckConstraint("ck_case_client_role_claims_role", "role IN ('debtor_1', 'debtor_2')");
                    table.ForeignKey(
                        name: "fk_case_client_role_claims_firm",
                        column: x => x.firm_id,
                        principalSchema: "insolvia",
                        principalTable: "firms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "client_bindings",
                schema: "insolvia",
                columns: table => new
                {
                    firm_id = table.Column<string>(type: "text", nullable: false),
                    subject = table.Column<string>(type: "text", nullable: false),
                    case_id = table.Column<string>(type: "text", nullable: false),
                    email = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    roles = table.Column<string[]>(type: "text[]", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    invited_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_client_bindings", x => new { x.firm_id, x.subject });
                    table.CheckConstraint("ck_client_bindings_status", "status IN ('invited', 'active', 'revoked')");
                    table.ForeignKey(
                        name: "fk_client_bindings_firm",
                        column: x => x.firm_id,
                        principalSchema: "insolvia",
                        principalTable: "firms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "firm_clients",
                schema: "insolvia",
                columns: table => new
                {
                    firm_id = table.Column<string>(type: "text", nullable: false),
                    id = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    body = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    first_retained_at = table.Column<DateOnly>(type: "date", nullable: true),
                    tax_id_ref = table.Column<string>(type: "text", nullable: true),
                    tax_id_last_four = table.Column<string>(type: "text", nullable: true),
                    merged_into = table.Column<string>(type: "text", nullable: true),
                    merging_into = table.Column<string>(type: "text", nullable: true),
                    merging_from = table.Column<string>(type: "text", nullable: true),
                    prospect_stage = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_firm_clients", x => new { x.firm_id, x.id });
                    table.CheckConstraint("ck_firm_clients_prospect_stage", "prospect_stage IS NULL OR prospect_stage IN ('possible', 'consultation_scheduled', 'awaiting_signed_agreement', 'exhausted')");
                    table.CheckConstraint("ck_firm_clients_status", "status IN ('active', 'archived')");
                    table.ForeignKey(
                        name: "fk_firm_clients_firm",
                        column: x => x.firm_id,
                        principalSchema: "insolvia",
                        principalTable: "firms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_firm_clients_merged_into",
                        columns: x => new { x.firm_id, x.merged_into },
                        principalSchema: "insolvia",
                        principalTable: "firm_clients",
                        principalColumns: new[] { "firm_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_firm_clients_merging_from",
                        columns: x => new { x.firm_id, x.merging_from },
                        principalSchema: "insolvia",
                        principalTable: "firm_clients",
                        principalColumns: new[] { "firm_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_firm_clients_merging_into",
                        columns: x => new { x.firm_id, x.merging_into },
                        principalSchema: "insolvia",
                        principalTable: "firm_clients",
                        principalColumns: new[] { "firm_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "firm_document_checklists",
                schema: "insolvia",
                columns: table => new
                {
                    firm_id = table.Column<string>(type: "text", nullable: false),
                    items = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_firm_document_checklists", x => x.firm_id);
                    table.ForeignKey(
                        name: "fk_firm_document_checklists_firm",
                        column: x => x.firm_id,
                        principalSchema: "insolvia",
                        principalTable: "firms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "firm_questionnaires",
                schema: "insolvia",
                columns: table => new
                {
                    firm_id = table.Column<string>(type: "text", nullable: false),
                    sections = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_firm_questionnaires", x => x.firm_id);
                    table.ForeignKey(
                        name: "fk_firm_questionnaires_firm",
                        column: x => x.firm_id,
                        principalSchema: "insolvia",
                        principalTable: "firms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "firm_users",
                schema: "insolvia",
                columns: table => new
                {
                    firm_id = table.Column<string>(type: "text", nullable: false),
                    subject = table.Column<string>(type: "text", nullable: false),
                    email = table.Column<string>(type: "text", nullable: false),
                    first_name = table.Column<string>(type: "text", nullable: false),
                    last_name = table.Column<string>(type: "text", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    is_admin = table.Column<bool>(type: "boolean", nullable: false),
                    access_all_cases = table.Column<bool>(type: "boolean", nullable: false),
                    permissions = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    signature_block = table.Column<JsonDocument>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_firm_users", x => new { x.firm_id, x.subject });
                    table.CheckConstraint("ck_firm_users_role", "role IN ('attorney', 'paralegal', 'staff')");
                    table.CheckConstraint("ck_firm_users_status", "status IN ('active', 'disabled')");
                    table.ForeignKey(
                        name: "fk_firm_users_firm",
                        column: x => x.firm_id,
                        principalSchema: "insolvia",
                        principalTable: "firms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "library_creditors",
                schema: "insolvia",
                columns: table => new
                {
                    firm_id = table.Column<string>(type: "text", nullable: false),
                    id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    address = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    additional_notice_parties = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    preferred = table.Column<bool>(type: "boolean", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_library_creditors", x => new { x.firm_id, x.id });
                    table.ForeignKey(
                        name: "fk_library_creditors_firm",
                        column: x => x.firm_id,
                        principalSchema: "insolvia",
                        principalTable: "firms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_access_log_principal_recorded_at",
                schema: "insolvia",
                table: "access_log",
                columns: new[] { "principal", "recorded_at" });

            migrationBuilder.CreateIndex(
                name: "ix_access_log_subject_kind_subject_id_recorded_at",
                schema: "insolvia",
                table: "access_log",
                columns: new[] { "subject_kind", "subject_id", "recorded_at" });

            migrationBuilder.CreateIndex(
                name: "ix_case_client_bindings_firm_id_case_id",
                schema: "insolvia",
                table: "case_client_bindings",
                columns: new[] { "firm_id", "case_id" });

            migrationBuilder.CreateIndex(
                name: "ix_case_client_role_claims_firm_id_case_id",
                schema: "insolvia",
                table: "case_client_role_claims",
                columns: new[] { "firm_id", "case_id" });

            migrationBuilder.CreateIndex(
                name: "ix_client_bindings_firm_id_email",
                schema: "insolvia",
                table: "client_bindings",
                columns: new[] { "firm_id", "email" });

            migrationBuilder.CreateIndex(
                name: "ix_client_bindings_subject",
                schema: "insolvia",
                table: "client_bindings",
                column: "subject");

            migrationBuilder.CreateIndex(
                name: "ix_firm_clients_firm_id_merged_into",
                schema: "insolvia",
                table: "firm_clients",
                columns: new[] { "firm_id", "merged_into" });

            migrationBuilder.CreateIndex(
                name: "ix_firm_clients_firm_id_merging_from",
                schema: "insolvia",
                table: "firm_clients",
                columns: new[] { "firm_id", "merging_from" });

            migrationBuilder.CreateIndex(
                name: "ix_firm_clients_firm_id_merging_into",
                schema: "insolvia",
                table: "firm_clients",
                columns: new[] { "firm_id", "merging_into" });

            migrationBuilder.CreateIndex(
                name: "ix_firm_users_subject",
                schema: "insolvia",
                table: "firm_users",
                column: "subject");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "access_log",
                schema: "insolvia");

            migrationBuilder.DropTable(
                name: "case_client_bindings",
                schema: "insolvia");

            migrationBuilder.DropTable(
                name: "case_client_role_claims",
                schema: "insolvia");

            migrationBuilder.DropTable(
                name: "client_bindings",
                schema: "insolvia");

            migrationBuilder.DropTable(
                name: "firm_clients",
                schema: "insolvia");

            migrationBuilder.DropTable(
                name: "firm_document_checklists",
                schema: "insolvia");

            migrationBuilder.DropTable(
                name: "firm_questionnaires",
                schema: "insolvia");

            migrationBuilder.DropTable(
                name: "firm_users",
                schema: "insolvia");

            migrationBuilder.DropTable(
                name: "library_creditors",
                schema: "insolvia");

            migrationBuilder.DropTable(
                name: "firms",
                schema: "insolvia");
        }
    }
}
