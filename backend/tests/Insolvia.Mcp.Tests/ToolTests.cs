using System.Text.Json.Nodes;
using Insolvia.Core;
using Insolvia.Core.AccessLog;
using Insolvia.Core.Debtors;
using Insolvia.Core.Firms;
using Insolvia.Mcp.Tests.Support;
using Insolvia.Mcp.Tools;
using static Insolvia.Mcp.Tests.Support.World;

namespace Insolvia.Mcp.Tests;

/// <summary>
/// services/mcp/tests/unit/test_tools.py: the tool layer over the memory
/// twins — gates, anti-oracle answers, pagination and the candidate flow,
/// without JSON-RPC in the way.
/// </summary>
public sealed class ToolTests
{
    private static readonly Core.Access.Accessor Admin = MakeAccessor(isAdmin: true);

    private static readonly Core.Access.Accessor Viewer = MakeAccessor(
        isAdmin: false,
        accessAllCases: true,
        permissions: new Dictionary<Feature, PermissionLevel>
        {
            [Feature.Cases] = PermissionLevel.ViewOnly,
            [Feature.Intake] = PermissionLevel.ViewOnly,
            [Feature.Documents] = PermissionLevel.ViewOnly,
        });

    private static readonly Core.Access.Accessor Stripped = MakeAccessor(isAdmin: false, accessAllCases: true, permissions: new Dictionary<Feature, PermissionLevel>());

    private static readonly Core.Access.Accessor OtherFirm = MakeAccessor(isAdmin: true, firmId: OtherFirmId);

    private static readonly Core.Access.Accessor IntakeOnly = MakeAccessor(
        isAdmin: false,
        accessAllCases: true,
        permissions: new Dictionary<Feature, PermissionLevel> { [Feature.Cases] = PermissionLevel.ViewOnly, [Feature.Intake] = PermissionLevel.AddEdit });

    // No intake at all: `clients` is the first entity type not under it.
    private static readonly Core.Access.Accessor ClientReader = MakeAccessor(
        isAdmin: false,
        accessAllCases: true,
        permissions: new Dictionary<Feature, PermissionLevel> { [Feature.Cases] = PermissionLevel.ViewOnly, [Feature.Clients] = PermissionLevel.ViewOnly });

    private static readonly string[] TaxIdKeys = ["tax_id", "tax_id_ref", "taxId"];

    private readonly Stores _stores = new();

    private CaseTools Tools => _stores.Tools;

    // ── whoami ──────────────────────────────────────────────────────

    [Fact]
    public void Whoami_reports_the_firm_and_permissions()
    {
        var result = CaseTools.Whoami(Viewer);
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["id"] = FirmId, ["name"] = "Example Firm" }, result["firm"]));
        Assert.Equal("Dev User", (string?)result["displayName"]);
        Assert.Equal("view_only", (string?)result["permissions"]!["cases"]);
        Assert.Equal("hidden", (string?)result["permissions"]!["extraction_review"]);
    }

    [Fact]
    public void Whoami_reports_the_clients_feature()
    {
        // ADR 0022: rows without `clients` have it hidden — the grant is an
        // admin's act, and whoami is where an agent learns of it.
        Assert.Equal("hidden", (string?)CaseTools.Whoami(Viewer)["permissions"]!["clients"]);
        var granted = MakeAccessor(isAdmin: false, permissions: new Dictionary<Feature, PermissionLevel> { [Feature.Clients] = PermissionLevel.ViewOnly });
        Assert.Equal("view_only", (string?)CaseTools.Whoami(granted)["permissions"]!["clients"]);
    }

    [Fact]
    public void Whoami_reports_the_absence_of_a_firm()
    {
        var result = CaseTools.Whoami(null);
        Assert.Null(result["firm"]);
        Assert.True(result.ContainsKey("firm"));
        Assert.Empty(result["permissions"]!.AsObject());
    }

    // ── listing and reading cases ───────────────────────────────────

    [Fact]
    public async Task List_cases_answers_the_callers_cases_newest_first()
    {
        var first = await SeedCaseAsync(_stores.Cases);
        var second = await SeedCaseAsync(_stores.Cases);
        var result = await Tools.ListCasesAsync(Admin, cancellationToken: Ct);
        Assert.Equal([second.Id, first.Id], result["cases"]!.AsArray().Select(found => (string?)found!["id"]));
        Assert.False(result.ContainsKey("nextCursor"));
    }

    [Fact]
    public async Task List_cases_is_firm_scoped()
    {
        await SeedCaseAsync(_stores.Cases);
        Assert.Empty((await Tools.ListCasesAsync(OtherFirm, cancellationToken: Ct))["cases"]!.AsArray());
    }

    [Fact]
    public async Task List_cases_filters_by_status_within_the_page()
    {
        await SeedCaseAsync(_stores.Cases);
        Assert.Empty((await Tools.ListCasesAsync(Admin, status: "filed", cancellationToken: Ct))["cases"]!.AsArray());
    }

    [Theory]
    [InlineData("open", null, null, "status")]
    [InlineData(null, 0L, null, "limit")]
    [InlineData(null, 101L, null, "limit")]
    [InlineData(null, null, "not-a-cursor", "cursor")]
    public async Task List_cases_rejects_bad_arguments(string? status, long? limit, string? cursor, string messagePart)
    {
        // `limit: True` and `cursor: 7` are pydantic's to refuse in this port,
        // before the tool runs — ArgumentTests pins them.
        await SeedCaseAsync(_stores.Cases);
        var error = await Assert.ThrowsAnyAsync<ValidationException>(() => Tools.ListCasesAsync(Admin, status, limit, cursor, Ct));
        Assert.Contains(messagePart, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_cases_requires_the_cases_feature() =>
        await Assert.ThrowsAnyAsync<ForbiddenException>(() => Tools.ListCasesAsync(Stripped, cancellationToken: Ct));

    [Fact]
    public async Task Get_case_answers_the_case_and_its_record_counts()
    {
        var found = await SeedCaseAsync(_stores.Cases);
        await CreditorAsync(_stores.Entities, found.Id);
        var result = await Tools.GetCaseAsync(Admin, found.Id, Ct);
        Assert.Equal(found.Id, (string?)result["case"]!["id"]);
        Assert.Equal(1, (int)result["recordCounts"]!["creditors"]!);
        Assert.Equal(CaseTools.EntityTypes.Order(), result["recordCounts"]!.AsObject().Select(pair => pair.Key).Order());
    }

    [Fact]
    public async Task Another_firms_case_is_not_found_not_forbidden()
    {
        // The anti-oracle rule: indistinguishable from a case that does not exist.
        var found = await SeedCaseAsync(_stores.Cases);
        await Assert.ThrowsAsync<NotFoundException>(() => Tools.GetCaseAsync(OtherFirm, found.Id, Ct));
    }

    [Fact]
    public async Task Case_reads_are_recorded_either_way()
    {
        var found = await SeedCaseAsync(_stores.Cases);
        await Tools.GetCaseAsync(Admin, found.Id, Ct);
        await Assert.ThrowsAsync<NotFoundException>(() => Tools.GetCaseAsync(OtherFirm, found.Id, Ct));
        var outcomes = _stores.AccessLog.Events.Select(row => (row.Action, row.Outcome)).ToList();
        Assert.Contains((AccessAction.CaseRead, AccessOutcome.Allowed), outcomes);
        Assert.Contains((AccessAction.CaseRead, AccessOutcome.Denied), outcomes);
    }

    // ── the generic record tools ────────────────────────────────────

    [Fact]
    public async Task List_case_records_serializes_the_apis_wire_shape()
    {
        var found = await SeedCaseAsync(_stores.Cases);
        var entity = await CreditorAsync(_stores.Entities, found.Id);
        var result = await Tools.ListCaseRecordsAsync(Admin, found.Id, "creditors", cancellationToken: Ct);
        var record = Assert.Single(result["records"]!.AsArray())!;
        Assert.Equal(entity.Id, (string?)record["id"]);
        Assert.Equal("Example Bank", (string?)record["name"]);
        Assert.NotNull(record["provenance"]);
    }

    [Fact]
    public async Task List_case_records_paginates_with_bound_cursors()
    {
        var found = await SeedCaseAsync(_stores.Cases);
        for (var index = 0; index < 3; index++)
        {
            await CreditorAsync(_stores.Entities, found.Id, $"Creditor {index}");
        }

        var first = await Tools.ListCaseRecordsAsync(Admin, found.Id, "creditors", limit: 2, cancellationToken: Ct);
        Assert.Equal(2, first["records"]!.AsArray().Count);
        var second = await Tools.ListCaseRecordsAsync(Admin, found.Id, "creditors", limit: 2, cursor: (string?)first["nextCursor"], cancellationToken: Ct);
        Assert.Single(second["records"]!.AsArray());
        Assert.False(second.ContainsKey("nextCursor"));
    }

    [Fact]
    public async Task A_cursor_replayed_against_another_listing_is_rejected()
    {
        var found = await SeedCaseAsync(_stores.Cases);
        var other = await SeedCaseAsync(_stores.Cases);
        for (var index = 0; index < 3; index++)
        {
            await CreditorAsync(_stores.Entities, found.Id, $"Creditor {index}");
        }

        var page = await Tools.ListCaseRecordsAsync(Admin, found.Id, "creditors", limit: 2, cancellationToken: Ct);
        await Assert.ThrowsAnyAsync<ValidationException>(() => Tools.ListCaseRecordsAsync(Admin, other.Id, "creditors", cursor: (string?)page["nextCursor"], cancellationToken: Ct));
    }

    [Fact]
    public async Task An_unknown_entity_type_is_a_validation_failure()
    {
        var found = await SeedCaseAsync(_stores.Cases);
        var error = await Assert.ThrowsAnyAsync<ValidationException>(() => Tools.ListCaseRecordsAsync(Admin, found.Id, "wallets", cancellationToken: Ct));
        Assert.Contains("entityType", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("creditors")]
    [InlineData("debtors")]
    public async Task Record_reads_gate_on_intake(string entityType)
    {
        var found = await SeedCaseAsync(_stores.Cases);
        await Assert.ThrowsAnyAsync<ForbiddenException>(() => Tools.ListCaseRecordsAsync(Stripped, found.Id, entityType, cancellationToken: Ct));
    }

    [Fact]
    public async Task Document_reads_gate_on_documents()
    {
        var found = await SeedCaseAsync(_stores.Cases);
        await Assert.ThrowsAnyAsync<ForbiddenException>(() => Tools.ListCaseRecordsAsync(IntakeOnly, found.Id, "documents", cancellationToken: Ct));
    }

    [Fact]
    public async Task Get_case_record_answers_one_record()
    {
        var found = await SeedCaseAsync(_stores.Cases);
        var entity = await CreditorAsync(_stores.Entities, found.Id);
        var result = await Tools.GetCaseRecordAsync(Admin, found.Id, "creditors", entity.Id, Ct);
        Assert.Equal(entity.Id, (string?)result["record"]!["id"]);
    }

    [Fact]
    public async Task A_record_id_from_another_case_is_not_found()
    {
        var found = await SeedCaseAsync(_stores.Cases);
        var other = await SeedCaseAsync(_stores.Cases);
        var entity = await CreditorAsync(_stores.Entities, found.Id);
        await Assert.ThrowsAsync<NotFoundException>(() => Tools.GetCaseRecordAsync(Admin, other.Id, "creditors", entity.Id, Ct));
    }

    // ── a case's clients (ADR 0022) ─────────────────────────────────

    // A case with two linked clients, plus a third client in the same firm
    // that this case does not name.
    private async Task<(Core.Cases.Case Case, Core.FirmClients.FirmClient First, Core.FirmClients.FirmClient Second, Core.FirmClients.FirmClient Stranger)> JointCaseAsync()
    {
        await _stores.Firms.CreateFirmAsync(MakeFirm(), Ct);
        var found = await SeedCaseAsync(_stores.Cases);
        var first = await MakeFirmClientAsync(_stores.Firms, "First");
        var second = await MakeFirmClientAsync(_stores.Firms, "Second", withTaxId: false);
        var stranger = await MakeFirmClientAsync(_stores.Firms, "Stranger");
        await LinkClientAsync(_stores.Debtors, first, found, FilingRole.Debtor1);
        await LinkClientAsync(_stores.Debtors, second, found, FilingRole.Debtor2);
        return (found, first, second, stranger);
    }

    [Fact]
    public async Task List_case_records_answers_the_cases_linked_clients()
    {
        var (found, first, second, _) = await JointCaseAsync();
        var result = await Tools.ListCaseRecordsAsync(Admin, found.Id, "clients", cancellationToken: Ct);

        // Filing-role order, and only the clients this case's debtors name.
        var records = result["records"]!.AsArray();
        Assert.Equal([first.Id, second.Id], records.Select(record => (string?)record!["id"]));
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["given"] = "Pat", ["surname"] = "First" }, records[0]!["name"]));
        Assert.False(result.ContainsKey("nextCursor"));
    }

    [Fact]
    public async Task A_client_record_carries_the_last_four_never_the_ref()
    {
        // The tax id rule (#382, ADR 0022): last four only — never a ref, never a value.
        var (found, first, _, _) = await JointCaseAsync();
        var listed = await Tools.ListCaseRecordsAsync(Admin, found.Id, "clients", cancellationToken: Ct);
        var read = await Tools.GetCaseRecordAsync(Admin, found.Id, "clients", first.Id, Ct);
        foreach (var record in listed["records"]!.AsArray().Append(read["record"]))
        {
            Assert.DoesNotContain(TaxIdRef, record!.ToJsonString(), StringComparison.Ordinal);
            foreach (var key in TaxIdKeys)
            {
                Assert.False(record.AsObject().ContainsKey(key), key);
            }
        }

        Assert.Equal("6789", (string?)read["record"]!["tax_id_last_four"]);
        Assert.False(listed["records"]![1]!.AsObject().ContainsKey("tax_id_last_four"));
    }

    [Fact]
    public async Task A_case_without_linked_clients_lists_none()
    {
        var found = await SeedCaseAsync(_stores.Cases);
        await MakeFirmClientAsync(_stores.Firms);
        Assert.Empty((await Tools.ListCaseRecordsAsync(Admin, found.Id, "clients", cancellationToken: Ct))["records"]!.AsArray());
    }

    [Fact]
    public async Task Get_case_counts_the_linked_clients()
    {
        var (found, _, _, _) = await JointCaseAsync();
        var counts = (await Tools.GetCaseAsync(Admin, found.Id, Ct))["recordCounts"]!;
        Assert.Equal(2, (int)counts["clients"]!);
        Assert.Equal(2, (int)counts["debtors"]!);
    }

    [Fact]
    public async Task Client_reads_gate_on_clients_not_intake()
    {
        var (found, first, _, _) = await JointCaseAsync();

        // Intake does not admit a client read...
        await Assert.ThrowsAnyAsync<ForbiddenException>(() => Tools.ListCaseRecordsAsync(IntakeOnly, found.Id, "clients", cancellationToken: Ct));
        await Assert.ThrowsAnyAsync<ForbiddenException>(() => Tools.GetCaseRecordAsync(IntakeOnly, found.Id, "clients", first.Id, Ct));

        // ...and `clients` alone does, with no intake grant at all.
        Assert.Equal(2, (await Tools.ListCaseRecordsAsync(ClientReader, found.Id, "clients", cancellationToken: Ct))["records"]!.AsArray().Count);
        await Assert.ThrowsAnyAsync<ForbiddenException>(() => Tools.ListCaseRecordsAsync(ClientReader, found.Id, "debtors", cancellationToken: Ct));
    }

    [Fact]
    public async Task A_client_the_case_does_not_link_is_not_found()
    {
        // Same firm, real client — but not this case's: the answer a client of
        // another firm gets. There is no firm-wide client read here.
        var (found, _, _, stranger) = await JointCaseAsync();
        await Assert.ThrowsAsync<NotFoundException>(() => Tools.GetCaseRecordAsync(Admin, found.Id, "clients", stranger.Id, Ct));
    }

    [Fact]
    public async Task Another_firms_case_answers_not_found_for_its_clients()
    {
        var (found, first, _, _) = await JointCaseAsync();
        await Assert.ThrowsAsync<NotFoundException>(() => Tools.ListCaseRecordsAsync(OtherFirm, found.Id, "clients", cancellationToken: Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => Tools.GetCaseRecordAsync(OtherFirm, found.Id, "clients", first.Id, Ct));
    }

    [Fact]
    public async Task A_client_record_read_is_logged_under_the_client()
    {
        var (found, first, _, stranger) = await JointCaseAsync();
        await Tools.ListCaseRecordsAsync(Admin, found.Id, "clients", cancellationToken: Ct);
        await Tools.GetCaseRecordAsync(Admin, found.Id, "clients", first.Id, Ct);
        await Assert.ThrowsAsync<NotFoundException>(() => Tools.GetCaseRecordAsync(Admin, found.Id, "clients", stranger.Id, Ct));
        var clientRows = _stores.AccessLog.Events
            .Where(row => row.ClientId is not null)
            .Select(row => (row.ClientId, row.Action, row.Outcome))
            .ToList();

        // The listing is not logged per client (the API's directory list is
        // not); each single-record read is, refused ones denied.
        Assert.Equal(
            [(first.Id, AccessAction.ClientRead, AccessOutcome.Allowed), (stranger.Id, AccessAction.ClientRead, AccessOutcome.Denied)],
            clientRows);
    }

    [Fact]
    public async Task Clients_cannot_be_proposed_yet()
    {
        // Read-only in this revision: a client candidate needs a review path
        // that writes the firm table, which candidate review does not have.
        var found = await SeedCaseAsync(_stores.Cases);
        await Assert.ThrowsAsync<FieldValidationException>(() => Tools.ProposeCaseRecordsAsync(
            Admin, found.Id, new JsonArray(new JsonObject { ["entityType"] = "clients", ["payload"] = new JsonObject { ["name"] = new JsonObject() } }), "c", Ct));
        Assert.Empty(await _stores.Candidates.ListForCaseAsync(found.Id, Ct));
    }

    // ── the candidate flow ──────────────────────────────────────────

    private static JsonObject Proposal(string name = "First Example Bank") =>
        new() { ["entityType"] = "creditors", ["payload"] = new JsonObject { ["name"] = name } };

    [Fact]
    public async Task Propose_stores_pending_candidates_with_the_token_origin()
    {
        var found = await SeedCaseAsync(_stores.Cases);
        var result = await Tools.ProposeCaseRecordsAsync(Admin, found.Id, new JsonArray(Proposal(), Proposal("Second Bank")), "mcp-client-1", Ct);
        Assert.Equal(["pending", "pending"], result["candidates"]!.AsArray().Select(row => (string?)row!["status"]));
        var stored = await _stores.Candidates.ListForCaseAsync(found.Id, Ct);
        Assert.Equal(["mcp-client-1"], stored.Select(candidate => candidate.Origin.ClientId).Distinct());
        Assert.Equal([Subject], stored.Select(candidate => candidate.Origin.Subject).Distinct());
    }

    [Fact]
    public async Task Propose_requires_intake_add_edit()
    {
        var found = await SeedCaseAsync(_stores.Cases);
        await Assert.ThrowsAnyAsync<ForbiddenException>(() => Tools.ProposeCaseRecordsAsync(Viewer, found.Id, new JsonArray(Proposal()), "c", Ct));
    }

    [Fact]
    public async Task A_malformed_batch_stores_nothing()
    {
        var found = await SeedCaseAsync(_stores.Cases);
        await Assert.ThrowsAsync<FieldValidationException>(() => Tools.ProposeCaseRecordsAsync(
            Admin, found.Id, new JsonArray(Proposal(), new JsonObject { ["entityType"] = "nope" }), "c", Ct));
        Assert.Empty(await _stores.Candidates.ListForCaseAsync(found.Id, Ct));
    }

    [Fact]
    public async Task Propose_against_another_firms_case_is_not_found()
    {
        var found = await SeedCaseAsync(_stores.Cases);
        await Assert.ThrowsAsync<NotFoundException>(() => Tools.ProposeCaseRecordsAsync(OtherFirm, found.Id, new JsonArray(Proposal()), "c", Ct));
    }

    [Fact]
    public async Task Check_proposals_reports_status_and_filters()
    {
        var found = await SeedCaseAsync(_stores.Cases);
        var proposed = await Tools.ProposeCaseRecordsAsync(Admin, found.Id, new JsonArray(Proposal()), "c", Ct);
        var candidateId = (string?)proposed["candidates"]![0]!["candidateId"];
        Assert.Equal(candidateId, (string?)(await Tools.CheckProposalsAsync(Admin, found.Id, cancellationToken: Ct))["candidates"]![0]!["candidateId"]);
        Assert.Empty((await Tools.CheckProposalsAsync(Admin, found.Id, status: "accepted", cancellationToken: Ct))["candidates"]!.AsArray());
        Assert.Equal(candidateId, (string?)(await Tools.CheckProposalsAsync(Admin, found.Id, candidateIds: [candidateId!], cancellationToken: Ct))["candidates"]![0]!["candidateId"]);
    }

    [Fact]
    public async Task Withdraw_is_the_proposers_own()
    {
        var found = await SeedCaseAsync(_stores.Cases);
        var proposed = await Tools.ProposeCaseRecordsAsync(Admin, found.Id, new JsonArray(Proposal()), "c", Ct);
        var candidateId = (string)proposed["candidates"]![0]!["candidateId"]!;

        var colleague = MakeAccessor(isAdmin: true, subject: Colleague);
        await Assert.ThrowsAnyAsync<ForbiddenException>(() => Tools.WithdrawProposalAsync(colleague, found.Id, candidateId, Ct));

        var result = await Tools.WithdrawProposalAsync(Admin, found.Id, candidateId, Ct);
        Assert.Equal("withdrawn", (string?)result["status"]);

        // Withdrawn is terminal: a second withdrawal conflicts rather than
        // pretending to succeed.
        await Assert.ThrowsAnyAsync<ConflictException>(() => Tools.WithdrawProposalAsync(Admin, found.Id, candidateId, Ct));
    }

    [Fact]
    public async Task Withdrawing_an_unknown_candidate_is_not_found()
    {
        var found = await SeedCaseAsync(_stores.Cases);
        await Assert.ThrowsAsync<NotFoundException>(() => Tools.WithdrawProposalAsync(Admin, found.Id, "missing", Ct));
    }

    // ── beyond the Python file: the write path is candidates only ────

    [Fact]
    public async Task A_proposal_writes_no_case_record()
    {
        // Confirm-before-entry, structurally: proposing a creditor leaves the
        // case's creditor collection empty until a human accepts it.
        var found = await SeedCaseAsync(_stores.Cases);
        await Tools.ProposeCaseRecordsAsync(Admin, found.Id, new JsonArray(Proposal()), "c", Ct);
        Assert.Empty(await _stores.Entities.ListForCaseAsync(found.Id, Core.CaseEntities.CaseCollections.ByCollection["creditors"], Ct));
        Assert.Single(await _stores.Candidates.ListForCaseAsync(found.Id, Ct));
    }

    [Fact]
    public async Task Proposals_and_withdrawals_are_access_logged()
    {
        var found = await SeedCaseAsync(_stores.Cases);
        var proposed = await Tools.ProposeCaseRecordsAsync(Admin, found.Id, new JsonArray(Proposal()), "c", Ct);
        await Tools.WithdrawProposalAsync(Admin, found.Id, (string)proposed["candidates"]![0]!["candidateId"]!, Ct);
        var actions = _stores.AccessLog.Events.Select(row => row.Action).ToList();
        Assert.Contains(AccessAction.CandidatePropose, actions);
        Assert.Contains(AccessAction.CandidateWithdraw, actions);
    }

    [Fact]
    public void The_entity_type_enum_is_derived_from_the_registry() =>
        Assert.Equal(
            ["creditors", "claims", "assets", "employments", "pay_period_records", "other_income_records", "income_summaries",
             "households", "expenses", "dependents", "codebtors", "sofa_entries", "petitions", "prior_cases", "related_cases",
             "sole_proprietorships", "filing_professionals", "exemptions", "contract_leases", "community_household_members",
             "means_test_inputs", "plans", "debtors", "documents", "clients"],
            CaseTools.EntityTypes);
}
