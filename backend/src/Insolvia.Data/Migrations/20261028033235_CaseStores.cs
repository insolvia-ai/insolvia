using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Insolvia.Data.Migrations
{
    /// <inheritdoc />
    public partial class CaseStores : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "calendar_tokens",
                schema: "insolvia",
                columns: table => new
                {
                    firm_id = table.Column<string>(type: "text", nullable: false),
                    subject = table.Column<string>(type: "text", nullable: false),
                    secret_hash = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_calendar_tokens", x => new { x.firm_id, x.subject });
                    table.ForeignKey(
                        name: "fk_calendar_tokens_firm",
                        column: x => x.firm_id,
                        principalSchema: "insolvia",
                        principalTable: "firms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "cases",
                schema: "insolvia",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    firm_id = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    chapter = table.Column<int>(type: "integer", nullable: false),
                    district = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    court = table.Column<string>(type: "text", nullable: true),
                    division = table.Column<string>(type: "text", nullable: true),
                    form_revisions = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    constants_set_id = table.Column<string>(type: "text", nullable: true),
                    exemption_set = table.Column<string>(type: "text", nullable: true),
                    filed_at = table.Column<DateOnly>(type: "date", nullable: true),
                    meeting341at = table.Column<DateOnly>(type: "date", nullable: true),
                    case_number = table.Column<string>(type: "text", nullable: true),
                    case_number_key = table.Column<string>(type: "text", nullable: true),
                    judge = table.Column<string>(type: "text", nullable: true),
                    trustee = table.Column<string>(type: "text", nullable: true),
                    office_file_number = table.Column<string>(type: "text", nullable: true),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    archived_by = table.Column<string>(type: "text", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    deleted_by = table.Column<string>(type: "text", nullable: true),
                    lifecycle_recorded_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cases", x => x.id);
                    table.UniqueConstraint("ak_cases_firm_id_id", x => new { x.firm_id, x.id });
                    table.CheckConstraint("ck_cases_archived", "(archived_at IS NULL) = (archived_by IS NULL)");
                    table.CheckConstraint("ck_cases_court", "(court IS NULL) = (division IS NULL)");
                    table.CheckConstraint("ck_cases_deleted", "(deleted_at IS NULL) = (deleted_by IS NULL)");
                    table.CheckConstraint("ck_cases_exemption_set", "exemption_set IS NULL OR exemption_set IN ('state_and_federal_nonbankruptcy', 'federal')");
                    table.CheckConstraint("ck_cases_status", "status IN ('intake', 'ready_to_file', 'filed', 'discharged', 'dismissed', 'closed')");
                    table.ForeignKey(
                        name: "fk_cases_firm",
                        column: x => x.firm_id,
                        principalSchema: "insolvia",
                        principalTable: "firms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "calendar_events",
                schema: "insolvia",
                columns: table => new
                {
                    firm_id = table.Column<string>(type: "text", nullable: false),
                    id = table.Column<string>(type: "text", nullable: false),
                    case_id = table.Column<string>(type: "text", nullable: true),
                    title = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    all_day = table.Column<bool>(type: "boolean", nullable: false),
                    starts_on = table.Column<DateOnly>(type: "date", nullable: true),
                    ends_on = table.Column<DateOnly>(type: "date", nullable: true),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    sort_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    location = table.Column<string>(type: "text", nullable: true),
                    attendees = table.Column<string[]>(type: "text[]", nullable: false),
                    rule_id = table.Column<string>(type: "text", nullable: true),
                    rule_citation = table.Column<string>(type: "text", nullable: true),
                    dismissed = table.Column<bool>(type: "boolean", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_calendar_events", x => new { x.firm_id, x.id });
                    table.CheckConstraint("ck_calendar_events_days", "(all_day AND starts_on IS NOT NULL AND ends_on IS NOT NULL AND starts_at IS NULL AND ends_at IS NULL) OR (NOT all_day AND starts_at IS NOT NULL AND ends_at IS NOT NULL AND starts_on IS NULL AND ends_on IS NULL)");
                    table.ForeignKey(
                        name: "fk_calendar_events_case",
                        columns: x => new { x.firm_id, x.case_id },
                        principalSchema: "insolvia",
                        principalTable: "cases",
                        principalColumns: new[] { "firm_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_calendar_events_firm",
                        column: x => x.firm_id,
                        principalSchema: "insolvia",
                        principalTable: "firms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "candidates",
                schema: "insolvia",
                columns: table => new
                {
                    case_id = table.Column<string>(type: "text", nullable: false),
                    id = table.Column<string>(type: "text", nullable: false),
                    firm_id = table.Column<string>(type: "text", nullable: false),
                    entity_type = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    origin_channel = table.Column<string>(type: "text", nullable: false),
                    origin_client_id = table.Column<string>(type: "text", nullable: false),
                    origin_subject = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    document_id = table.Column<string>(type: "text", nullable: true),
                    external_ref = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    confidence = table.Column<double>(type: "double precision", nullable: true),
                    locator = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    confirmed_by = table.Column<string>(type: "text", nullable: true),
                    confirmed_at = table.Column<string>(type: "text", nullable: true),
                    corrected_payload = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    resulting_record_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_candidates", x => new { x.case_id, x.id });
                    table.CheckConstraint("ck_candidates_origin_channel", "origin_channel IN ('mcp', 'extraction', 'client')");
                    table.CheckConstraint("ck_candidates_status", "status IN ('pending', 'accepted', 'corrected', 'rejected', 'withdrawn')");
                    table.ForeignKey(
                        name: "fk_candidates_case",
                        columns: x => new { x.firm_id, x.case_id },
                        principalSchema: "insolvia",
                        principalTable: "cases",
                        principalColumns: new[] { "firm_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "case_assignments",
                schema: "insolvia",
                columns: table => new
                {
                    case_id = table.Column<string>(type: "text", nullable: false),
                    subject = table.Column<string>(type: "text", nullable: false),
                    firm_id = table.Column<string>(type: "text", nullable: false),
                    case_created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    assigned_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    assigned_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_case_assignments", x => new { x.case_id, x.subject });
                    table.ForeignKey(
                        name: "fk_case_assignments_case",
                        columns: x => new { x.firm_id, x.case_id },
                        principalSchema: "insolvia",
                        principalTable: "cases",
                        principalColumns: new[] { "firm_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "case_entities",
                schema: "insolvia",
                columns: table => new
                {
                    case_id = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    id = table.Column<string>(type: "text", nullable: false),
                    firm_id = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    body = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    provenance = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    amended = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_case_entities", x => new { x.case_id, x.kind, x.id });
                    table.ForeignKey(
                        name: "fk_case_entities_case",
                        columns: x => new { x.firm_id, x.case_id },
                        principalSchema: "insolvia",
                        principalTable: "cases",
                        principalColumns: new[] { "firm_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "case_lifecycle_events",
                schema: "insolvia",
                columns: table => new
                {
                    case_id = table.Column<string>(type: "text", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    firm_id = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    effective_on = table.Column<DateOnly>(type: "date", nullable: true),
                    recorded_by = table.Column<string>(type: "text", nullable: false),
                    provenance = table.Column<string>(type: "text", nullable: false),
                    candidate_id = table.Column<string>(type: "text", nullable: true),
                    from_status = table.Column<string>(type: "text", nullable: true),
                    to_status = table.Column<string>(type: "text", nullable: true),
                    chapter = table.Column<int>(type: "integer", nullable: true),
                    filing_id = table.Column<string>(type: "text", nullable: true),
                    from_chapter = table.Column<int>(type: "integer", nullable: true),
                    to_chapter = table.Column<int>(type: "integer", nullable: true),
                    plan_version_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_case_lifecycle_events", x => new { x.case_id, x.recorded_at });
                    table.CheckConstraint("ck_case_lifecycle_events_candidate", "(provenance = 'court_notice') = (candidate_id IS NOT NULL)");
                    table.CheckConstraint("ck_case_lifecycle_events_conversion", "(kind = 'conversion') = (from_chapter IS NOT NULL AND to_chapter IS NOT NULL)");
                    table.CheckConstraint("ck_case_lifecycle_events_court_date", "kind = 'status' OR effective_on IS NOT NULL");
                    table.CheckConstraint("ck_case_lifecycle_events_kind", "kind IN ('status', 'conversion', 'confirmation', 'confirmation_denied')");
                    table.CheckConstraint("ck_case_lifecycle_events_provenance", "provenance IN ('manual', 'court_notice', 'filing-worker')");
                    table.CheckConstraint("ck_case_lifecycle_events_status", "(kind = 'status') = (from_status IS NOT NULL AND to_status IS NOT NULL)");
                    table.CheckConstraint("ck_case_lifecycle_events_statuses", "(from_status IS NULL OR from_status IN ('intake', 'ready_to_file', 'filed', 'discharged', 'dismissed', 'closed')) AND (to_status IS NULL OR to_status IN ('intake', 'ready_to_file', 'filed', 'discharged', 'dismissed', 'closed'))");
                    table.ForeignKey(
                        name: "fk_case_lifecycle_events_case",
                        columns: x => new { x.firm_id, x.case_id },
                        principalSchema: "insolvia",
                        principalTable: "cases",
                        principalColumns: new[] { "firm_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "case_tasks",
                schema: "insolvia",
                columns: table => new
                {
                    case_id = table.Column<string>(type: "text", nullable: false),
                    id = table.Column<string>(type: "text", nullable: false),
                    firm_id = table.Column<string>(type: "text", nullable: false),
                    subject = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    due_date = table.Column<DateOnly>(type: "date", nullable: true),
                    assignee_subject = table.Column<string>(type: "text", nullable: true),
                    form_series = table.Column<string>(type: "text", nullable: true),
                    done = table.Column<bool>(type: "boolean", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_case_tasks", x => new { x.case_id, x.id });
                    table.ForeignKey(
                        name: "fk_case_tasks_case",
                        columns: x => new { x.firm_id, x.case_id },
                        principalSchema: "insolvia",
                        principalTable: "cases",
                        principalColumns: new[] { "firm_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "debtors",
                schema: "insolvia",
                columns: table => new
                {
                    case_id = table.Column<string>(type: "text", nullable: false),
                    filing_role = table.Column<string>(type: "text", nullable: false),
                    firm_id = table.Column<string>(type: "text", nullable: false),
                    id = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    body = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    provenance = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    tax_id_kind = table.Column<string>(type: "text", nullable: true),
                    tax_id_last_four = table.Column<string>(type: "text", nullable: true),
                    tax_id_ref = table.Column<string>(type: "text", nullable: true),
                    client_id = table.Column<string>(type: "text", nullable: true),
                    case_created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_debtors", x => new { x.case_id, x.filing_role });
                    table.CheckConstraint("ck_debtors_client", "(client_id IS NULL) = (case_created_at IS NULL)");
                    table.CheckConstraint("ck_debtors_filing_role", "filing_role IN ('debtor_1', 'debtor_2', 'non_filing_spouse')");
                    table.CheckConstraint("ck_debtors_tax_id_kind", "tax_id_kind IS NULL OR tax_id_kind IN ('ssn', 'itin')");
                    table.ForeignKey(
                        name: "fk_debtors_case",
                        columns: x => new { x.firm_id, x.case_id },
                        principalSchema: "insolvia",
                        principalTable: "cases",
                        principalColumns: new[] { "firm_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_debtors_client",
                        columns: x => new { x.firm_id, x.client_id },
                        principalSchema: "insolvia",
                        principalTable: "firm_clients",
                        principalColumns: new[] { "firm_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "document_requests",
                schema: "insolvia",
                columns: table => new
                {
                    case_id = table.Column<string>(type: "text", nullable: false),
                    id = table.Column<string>(type: "text", nullable: false),
                    firm_id = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    document_ids = table.Column<string[]>(type: "text[]", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_requests", x => new { x.case_id, x.id });
                    table.CheckConstraint("ck_document_requests_kind", "kind IN ('credit_report', 'pay_stub', 'bank_statement', 'tax_return', 'identification', 'court_notice', 'other')");
                    table.CheckConstraint("ck_document_requests_status", "status IN ('requested', 'received', 'waived')");
                    table.ForeignKey(
                        name: "fk_document_requests_case",
                        columns: x => new { x.firm_id, x.case_id },
                        principalSchema: "insolvia",
                        principalTable: "cases",
                        principalColumns: new[] { "firm_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "documents",
                schema: "insolvia",
                columns: table => new
                {
                    case_id = table.Column<string>(type: "text", nullable: false),
                    id = table.Column<string>(type: "text", nullable: false),
                    firm_id = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    content_type = table.Column<string>(type: "text", nullable: false),
                    byte_size = table.Column<long>(type: "bigint", nullable: false),
                    storage_ref = table.Column<string>(type: "text", nullable: false),
                    uploaded_by = table.Column<string>(type: "text", nullable: false),
                    uploaded_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    etag = table.Column<string>(type: "text", nullable: true),
                    channel = table.Column<string>(type: "text", nullable: false),
                    request_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_documents", x => new { x.case_id, x.id });
                    table.CheckConstraint("ck_documents_channel", "channel IN ('staff', 'client')");
                    table.CheckConstraint("ck_documents_content_type", "content_type IN ('application/pdf', 'image/jpeg', 'image/png', 'image/heic', 'image/tiff')");
                    table.CheckConstraint("ck_documents_kind", "kind IN ('credit_report', 'pay_stub', 'bank_statement', 'tax_return', 'identification', 'court_notice', 'other')");
                    table.CheckConstraint("ck_documents_status", "status IN ('pending', 'stored')");
                    table.ForeignKey(
                        name: "fk_documents_case",
                        columns: x => new { x.firm_id, x.case_id },
                        principalSchema: "insolvia",
                        principalTable: "cases",
                        principalColumns: new[] { "firm_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "filing_approvals",
                schema: "insolvia",
                columns: table => new
                {
                    case_id = table.Column<string>(type: "text", nullable: false),
                    approval_id = table.Column<string>(type: "text", nullable: false),
                    firm_id = table.Column<string>(type: "text", nullable: false),
                    filing_id = table.Column<string>(type: "text", nullable: false),
                    attorney_id = table.Column<string>(type: "text", nullable: false),
                    credential_id = table.Column<string>(type: "text", nullable: false),
                    authorization_id = table.Column<string>(type: "text", nullable: false),
                    digest = table.Column<string>(type: "text", nullable: false),
                    packet_id = table.Column<string>(type: "text", nullable: false),
                    court = table.Column<string>(type: "text", nullable: false),
                    division = table.Column<string>(type: "text", nullable: false),
                    registry_release = table.Column<string>(type: "text", nullable: false),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    authenticated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    expires_at = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    voided_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    void_reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("ak_filing_approval_row_case_id_approval_id", x => new { x.case_id, x.approval_id });
                    table.CheckConstraint("ck_filing_approvals_status", "status IN ('pending', 'consumed', 'voided')");
                    table.ForeignKey(
                        name: "fk_filing_approvals_case",
                        columns: x => new { x.firm_id, x.case_id },
                        principalSchema: "insolvia",
                        principalTable: "cases",
                        principalColumns: new[] { "firm_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "filings",
                schema: "insolvia",
                columns: table => new
                {
                    case_id = table.Column<string>(type: "text", nullable: false),
                    filing_id = table.Column<string>(type: "text", nullable: false),
                    firm_id = table.Column<string>(type: "text", nullable: false),
                    approval_id = table.Column<string>(type: "text", nullable: false),
                    attorney_id = table.Column<string>(type: "text", nullable: false),
                    credential_id = table.Column<string>(type: "text", nullable: false),
                    court = table.Column<string>(type: "text", nullable: false),
                    division = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    attempt_id = table.Column<string>(type: "text", nullable: false),
                    claimed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    lease_expires_at = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    history = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    driver = table.Column<string>(type: "text", nullable: true),
                    hand_back = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    unknown_reason = table.Column<string>(type: "text", nullable: true),
                    confirmation = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    receipt_document_id = table.Column<string>(type: "text", nullable: true),
                    follow_ups = table.Column<string[]>(type: "text[]", nullable: false),
                    resolution = table.Column<JsonDocument>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_filings", x => new { x.case_id, x.filing_id });
                    table.CheckConstraint("ck_filings_state", "state IN ('claimed', 'signed_in', 'uploading', 'at_final_submit', 'submitted', 'filed', 'handed_back', 'outcome_unknown')");
                    table.ForeignKey(
                        name: "fk_filings_case",
                        columns: x => new { x.firm_id, x.case_id },
                        principalSchema: "insolvia",
                        principalTable: "cases",
                        principalColumns: new[] { "firm_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "jobs",
                schema: "insolvia",
                columns: table => new
                {
                    case_id = table.Column<string>(type: "text", nullable: false),
                    id = table.Column<string>(type: "text", nullable: false),
                    firm_id = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    document_id = table.Column<string>(type: "text", nullable: true),
                    options = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    failure = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    result = table.Column<JsonDocument>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_jobs", x => new { x.case_id, x.id });
                    table.CheckConstraint("ck_jobs_status", "status IN ('queued', 'running', 'succeeded', 'failed')");
                    table.ForeignKey(
                        name: "fk_jobs_case",
                        columns: x => new { x.firm_id, x.case_id },
                        principalSchema: "insolvia",
                        principalTable: "cases",
                        principalColumns: new[] { "firm_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "packets",
                schema: "insolvia",
                columns: table => new
                {
                    case_id = table.Column<string>(type: "text", nullable: false),
                    id = table.Column<string>(type: "text", nullable: false),
                    firm_id = table.Column<string>(type: "text", nullable: false),
                    job_id = table.Column<string>(type: "text", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    content_type = table.Column<string>(type: "text", nullable: false),
                    byte_size = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "text", nullable: false),
                    storage_ref = table.Column<string>(type: "text", nullable: false),
                    form_revisions = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    constants_set_id = table.Column<string>(type: "text", nullable: false),
                    creditor_count = table.Column<int>(type: "integer", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    options = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    parts = table.Column<JsonDocument>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_packets", x => new { x.case_id, x.id });
                    table.ForeignKey(
                        name: "fk_packets_case",
                        columns: x => new { x.firm_id, x.case_id },
                        principalSchema: "insolvia",
                        principalTable: "cases",
                        principalColumns: new[] { "firm_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tax_ids",
                schema: "insolvia",
                columns: table => new
                {
                    case_id = table.Column<string>(type: "text", nullable: false),
                    @ref = table.Column<string>(name: "ref", type: "text", nullable: false),
                    firm_id = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    last_four = table.Column<string>(type: "text", nullable: false),
                    scheme = table.Column<string>(type: "text", nullable: false),
                    ciphertext = table.Column<byte[]>(type: "bytea", nullable: false),
                    nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    wrapped_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tax_ids", x => new { x.case_id, x.@ref });
                    table.CheckConstraint("ck_tax_ids_kind", "kind IN ('ssn', 'itin')");
                    table.ForeignKey(
                        name: "fk_tax_ids_case",
                        columns: x => new { x.firm_id, x.case_id },
                        principalSchema: "insolvia",
                        principalTable: "cases",
                        principalColumns: new[] { "firm_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "current_filing_approvals",
                schema: "insolvia",
                columns: table => new
                {
                    case_id = table.Column<string>(type: "text", nullable: false),
                    firm_id = table.Column<string>(type: "text", nullable: false),
                    approval_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_current_filing_approvals", x => x.case_id);
                    table.ForeignKey(
                        name: "fk_current_filing_approvals_approval",
                        columns: x => new { x.case_id, x.approval_id },
                        principalSchema: "insolvia",
                        principalTable: "filing_approvals",
                        principalColumns: new[] { "case_id", "approval_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_current_filing_approvals_case",
                        columns: x => new { x.firm_id, x.case_id },
                        principalSchema: "insolvia",
                        principalTable: "cases",
                        principalColumns: new[] { "firm_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_client_bindings_firm_id_case_id",
                schema: "insolvia",
                table: "client_bindings",
                columns: new[] { "firm_id", "case_id" });

            migrationBuilder.CreateIndex(
                name: "ix_calendar_events_firm_id_case_id",
                schema: "insolvia",
                table: "calendar_events",
                columns: new[] { "firm_id", "case_id" });

            migrationBuilder.CreateIndex(
                name: "ix_calendar_events_firm_id_sort_at",
                schema: "insolvia",
                table: "calendar_events",
                columns: new[] { "firm_id", "sort_at" });

            migrationBuilder.CreateIndex(
                name: "ix_candidates_firm_id_case_id",
                schema: "insolvia",
                table: "candidates",
                columns: new[] { "firm_id", "case_id" });

            migrationBuilder.CreateIndex(
                name: "ix_case_assignments_firm_id_case_id",
                schema: "insolvia",
                table: "case_assignments",
                columns: new[] { "firm_id", "case_id" });

            migrationBuilder.CreateIndex(
                name: "ix_case_assignments_subject_case_created_at",
                schema: "insolvia",
                table: "case_assignments",
                columns: new[] { "subject", "case_created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_case_entities_firm_id_case_id",
                schema: "insolvia",
                table: "case_entities",
                columns: new[] { "firm_id", "case_id" });

            migrationBuilder.CreateIndex(
                name: "ix_case_lifecycle_events_firm_id_case_id",
                schema: "insolvia",
                table: "case_lifecycle_events",
                columns: new[] { "firm_id", "case_id" });

            migrationBuilder.CreateIndex(
                name: "ix_case_tasks_firm_id_case_id",
                schema: "insolvia",
                table: "case_tasks",
                columns: new[] { "firm_id", "case_id" });

            migrationBuilder.CreateIndex(
                name: "ix_cases_case_number_key",
                schema: "insolvia",
                table: "cases",
                column: "case_number_key");

            migrationBuilder.CreateIndex(
                name: "ix_cases_firm_id_created_at_id",
                schema: "insolvia",
                table: "cases",
                columns: new[] { "firm_id", "created_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_current_filing_approvals_case_id_approval_id",
                schema: "insolvia",
                table: "current_filing_approvals",
                columns: new[] { "case_id", "approval_id" });

            migrationBuilder.CreateIndex(
                name: "ix_current_filing_approvals_firm_id_case_id",
                schema: "insolvia",
                table: "current_filing_approvals",
                columns: new[] { "firm_id", "case_id" });

            migrationBuilder.CreateIndex(
                name: "ix_debtors_client_id",
                schema: "insolvia",
                table: "debtors",
                column: "client_id");

            migrationBuilder.CreateIndex(
                name: "ix_debtors_firm_id_case_id",
                schema: "insolvia",
                table: "debtors",
                columns: new[] { "firm_id", "case_id" });

            migrationBuilder.CreateIndex(
                name: "ix_debtors_firm_id_client_id",
                schema: "insolvia",
                table: "debtors",
                columns: new[] { "firm_id", "client_id" });

            migrationBuilder.CreateIndex(
                name: "ux_debtors_case_id_client_id",
                schema: "insolvia",
                table: "debtors",
                columns: new[] { "case_id", "client_id" },
                unique: true,
                filter: "client_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_document_requests_firm_id_case_id",
                schema: "insolvia",
                table: "document_requests",
                columns: new[] { "firm_id", "case_id" });

            migrationBuilder.CreateIndex(
                name: "ix_documents_firm_id_case_id",
                schema: "insolvia",
                table: "documents",
                columns: new[] { "firm_id", "case_id" });

            migrationBuilder.CreateIndex(
                name: "ix_filing_approvals_firm_id_case_id",
                schema: "insolvia",
                table: "filing_approvals",
                columns: new[] { "firm_id", "case_id" });

            migrationBuilder.CreateIndex(
                name: "ix_filings_firm_id_case_id",
                schema: "insolvia",
                table: "filings",
                columns: new[] { "firm_id", "case_id" });

            migrationBuilder.CreateIndex(
                name: "ix_jobs_firm_id_case_id",
                schema: "insolvia",
                table: "jobs",
                columns: new[] { "firm_id", "case_id" });

            migrationBuilder.CreateIndex(
                name: "ix_packets_firm_id_case_id",
                schema: "insolvia",
                table: "packets",
                columns: new[] { "firm_id", "case_id" });

            migrationBuilder.CreateIndex(
                name: "ix_tax_ids_firm_id_case_id",
                schema: "insolvia",
                table: "tax_ids",
                columns: new[] { "firm_id", "case_id" });

            migrationBuilder.AddForeignKey(
                name: "fk_case_client_bindings_case",
                schema: "insolvia",
                table: "case_client_bindings",
                columns: new[] { "firm_id", "case_id" },
                principalSchema: "insolvia",
                principalTable: "cases",
                principalColumns: new[] { "firm_id", "id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_case_client_role_claims_case",
                schema: "insolvia",
                table: "case_client_role_claims",
                columns: new[] { "firm_id", "case_id" },
                principalSchema: "insolvia",
                principalTable: "cases",
                principalColumns: new[] { "firm_id", "id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_client_bindings_case",
                schema: "insolvia",
                table: "client_bindings",
                columns: new[] { "firm_id", "case_id" },
                principalSchema: "insolvia",
                principalTable: "cases",
                principalColumns: new[] { "firm_id", "id" },
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_case_client_bindings_case",
                schema: "insolvia",
                table: "case_client_bindings");

            migrationBuilder.DropForeignKey(
                name: "fk_case_client_role_claims_case",
                schema: "insolvia",
                table: "case_client_role_claims");

            migrationBuilder.DropForeignKey(
                name: "fk_client_bindings_case",
                schema: "insolvia",
                table: "client_bindings");

            migrationBuilder.DropTable(
                name: "calendar_events",
                schema: "insolvia");

            migrationBuilder.DropTable(
                name: "calendar_tokens",
                schema: "insolvia");

            migrationBuilder.DropTable(
                name: "candidates",
                schema: "insolvia");

            migrationBuilder.DropTable(
                name: "case_assignments",
                schema: "insolvia");

            migrationBuilder.DropTable(
                name: "case_entities",
                schema: "insolvia");

            migrationBuilder.DropTable(
                name: "case_lifecycle_events",
                schema: "insolvia");

            migrationBuilder.DropTable(
                name: "case_tasks",
                schema: "insolvia");

            migrationBuilder.DropTable(
                name: "current_filing_approvals",
                schema: "insolvia");

            migrationBuilder.DropTable(
                name: "debtors",
                schema: "insolvia");

            migrationBuilder.DropTable(
                name: "document_requests",
                schema: "insolvia");

            migrationBuilder.DropTable(
                name: "documents",
                schema: "insolvia");

            migrationBuilder.DropTable(
                name: "filings",
                schema: "insolvia");

            migrationBuilder.DropTable(
                name: "jobs",
                schema: "insolvia");

            migrationBuilder.DropTable(
                name: "packets",
                schema: "insolvia");

            migrationBuilder.DropTable(
                name: "tax_ids",
                schema: "insolvia");

            migrationBuilder.DropTable(
                name: "filing_approvals",
                schema: "insolvia");

            migrationBuilder.DropTable(
                name: "cases",
                schema: "insolvia");

            migrationBuilder.DropIndex(
                name: "ix_client_bindings_firm_id_case_id",
                schema: "insolvia",
                table: "client_bindings");
        }
    }
}
