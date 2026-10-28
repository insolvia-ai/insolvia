using Insolvia.Core.Cases;
using Insolvia.Core.Debtors;
using Insolvia.Core.Documents;
using Npgsql;

namespace Insolvia.Data.Tests.Conformance;

/// <summary>
/// What only the Postgres case tables can be asked — what the SCHEMA refuses
/// on its own, whoever writes: tenancy through the composite foreign keys,
/// the purge's cascade, one client per case, the history's same-instant key
/// and its shape checks, and that keyed values are columns.
/// </summary>
[Collection(PostgresTests.Name)]
public sealed class PostgresCaseSchemaTests(PostgresFixture postgres) : CaseConformance
{
    private readonly PostgresCaseWorld _world = new(postgres);

    public override async ValueTask InitializeAsync()
    {
        await _world.InitializeAsync();
        await base.InitializeAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await _world.DisposeAsync();
        await base.DisposeAsync();
    }

    private protected override CaseStores Connect() => _world.Connect();

    [Fact]
    public async Task A_child_row_cannot_point_at_another_firm_s_case()
    {
        var theirs = await CreateCaseAsync(Ids.OtherFirm);
        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            $"""
            INSERT INTO insolvia.documents (case_id, id, firm_id, kind, file_name, content_type, byte_size, storage_ref, uploaded_by, uploaded_at, status, channel)
            VALUES ('{theirs.Id}', 'doc-1', '{Ids.Firm}', 'other', 'x.pdf', 'application/pdf', 1, 'ref', '{Ids.Alice}', now(), 'pending', 'staff')
            """));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
    }

    [Fact]
    public async Task A_child_of_a_case_that_does_not_exist_is_refused_by_the_store()
    {
        var ghost = Make.Case(NewId());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Connect().Documents.CreateAsync(
            new Document("doc-1", ghost.Id, DocumentKind.Other, "x.pdf", DocumentContentType.Pdf, 1, "ref", Ids.Alice, Opened),
            Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Connect().Cases.AssignAsync(Make.Assignment(ghost), Ct));
    }

    [Fact]
    public async Task Purging_a_case_takes_every_child_and_its_portal_access_with_it()
    {
        var client = await ClientAsync();
        var owner = await CreateCaseAsync(debtors: null);
        await Connect().Debtors.LinkAsync(MakeDebtor(owner, clientId: client.Id), create: true, Ids.Firm, Ct);
        await Connect().Cases.RecordAsync(CaseLifecycle.StatusMove(owner, owner with { Status = CaseStatus.ReadyToFile }, [], Ids.Alice)!, Ct);
        await new Portal.PostgresClientBindingStore(_world.Harness.NewContext()).BindAsync(Make.Binding(caseId: owner.Id), [], Ct);

        await ExecuteAsync($"DELETE FROM insolvia.cases WHERE id = '{owner.Id}'");

        foreach (var table in new[] { "case_assignments", "case_lifecycle_events", "debtors", "client_bindings", "case_client_bindings", "case_client_role_claims" })
        {
            Assert.Equal(0L, await ScalarAsync<long>($"SELECT count(*) FROM insolvia.{table} WHERE case_id = '{owner.Id}'"));
        }

        // The client the case was opened for is the firm's, and stays.
        Assert.NotNull(await Connect().Firms.GetClientAsync(Ids.Firm, client.Id, Ct));
    }

    [Fact]
    public async Task One_client_answers_for_one_role_of_a_case_whoever_writes()
    {
        var client = await ClientAsync();
        var owner = await CreateCaseAsync();
        await Connect().Debtors.PutAsync(MakeDebtor(owner, FilingRole.Debtor1, client.Id), Ct);
        var error = await Assert.ThrowsAsync<PostgresException>(() => Connect().Debtors.PutAsync(MakeDebtor(owner, FilingRole.Debtor2, client.Id), Ct));
        Assert.Equal((PostgresErrorCodes.UniqueViolation, "ux_debtors_case_id_client_id"), (error.SqlState, error.ConstraintName));
    }

    [Fact]
    public async Task Two_events_of_one_case_at_one_instant_is_a_key_the_history_refuses()
    {
        var owner = await CreateCaseAsync();
        var insert =
            $"""
            INSERT INTO insolvia.case_lifecycle_events (case_id, recorded_at, firm_id, kind, recorded_by, provenance, from_status, to_status)
            VALUES ('{owner.Id}', '2026-10-01T09:00:00Z', '{Ids.Firm}', 'status', '{Ids.Alice}', 'manual', 'intake', 'ready_to_file')
            """;
        await ExecuteAsync(insert);
        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(insert));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
    }

    [Theory]
    [InlineData("'conversion', 'manual', NULL, NULL, NULL, 7, NULL", "ck_case_lifecycle_events_conversion")]
    [InlineData("'confirmation', 'manual', NULL, NULL, NULL, NULL, NULL", "ck_case_lifecycle_events_court_date")]
    [InlineData("'status', 'court_notice', NULL, 'filed', 'dismissed', NULL, NULL", "ck_case_lifecycle_events_candidate")]
    [InlineData("'status', 'manual', NULL, NULL, 'dismissed', NULL, NULL", "ck_case_lifecycle_events_status")]
    public async Task An_event_no_writer_could_have_made_is_refused(string values, string constraint)
    {
        var owner = await CreateCaseAsync();
        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            $"""
            INSERT INTO insolvia.case_lifecycle_events (case_id, recorded_at, firm_id, kind, provenance, effective_on, from_status, to_status, from_chapter, to_chapter, recorded_by)
            VALUES ('{owner.Id}', now(), '{Ids.Firm}', {values}, '{Ids.Alice}')
            """));
        Assert.Equal((PostgresErrorCodes.CheckViolation, constraint), (error.SqlState, error.ConstraintName));
    }

    [Fact]
    public async Task Documents_are_jsonb_court_dates_are_dates_and_the_envelope_is_bytes()
    {
        var columns = await ColumnTypesAsync();
        Assert.Equal("jsonb", columns[("debtors", "body")]);
        Assert.Equal("jsonb", columns[("debtors", "provenance")]);
        Assert.Equal("jsonb", columns[("case_entities", "body")]);
        Assert.Equal("jsonb", columns[("candidates", "payload")]);
        Assert.Equal("date", columns[("cases", "filed_at")]);
        Assert.Equal("date", columns[("case_lifecycle_events", "effective_on")]);
        Assert.Equal("timestamp with time zone", columns[("case_lifecycle_events", "recorded_at")]);
        Assert.Equal("timestamp with time zone", columns[("cases", "lifecycle_recorded_at")]);
        Assert.Equal("text", columns[("cases", "status")]);
        Assert.Equal("text", columns[("cases", "case_number_key")]);
        Assert.Equal("bytea", columns[("tax_ids", "ciphertext")]);
        Assert.Equal("ARRAY", columns[("document_requests", "document_ids")]);
        Assert.Equal("text", columns[("filings", "attempt_id")]);
    }

    [Fact]
    public async Task The_case_number_key_is_derived_on_every_write()
    {
        var owner = await CreateCaseAsync();
        await Connect().Cases.UpdateAsync(owner with { CaseNumber = "8:26-bk-01234" }, CaseStatus.Intake, Ct);
        Assert.Equal("flmb:8:26-bk-01234", await ScalarAsync<string>($"SELECT case_number_key FROM insolvia.cases WHERE id = '{owner.Id}'"));
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_world.Harness.ConnectionString);
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // Test-authored SQL over fixture ids.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(_world.Harness.ConnectionString);
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // Test-authored SQL over fixture ids.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task<Dictionary<(string Table, string Column), string>> ColumnTypesAsync()
    {
        await using var connection = new NpgsqlConnection(_world.Harness.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT table_name, column_name, data_type FROM information_schema.columns WHERE table_schema = 'insolvia'",
            connection);
        var columns = new Dictionary<(string, string), string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            columns[(reader.GetString(0), reader.GetString(1))] = reader.GetString(2);
        }

        return columns;
    }
}
