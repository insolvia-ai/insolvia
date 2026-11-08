using Insolvia.Core;
using Insolvia.Core.FirmClients;
using Insolvia.Core.Firms;
using Insolvia.Data.Firms;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Insolvia.Data.Tests.Conformance;

/// <summary>
/// What only the Postgres store can be asked: how it reads a row an older or
/// newer writer left, what the schema itself refuses, and the seam the case
/// stores (18.11) will call. The behaviour both stores share is the
/// conformance suites'.
/// </summary>
[Collection(PostgresTests.Name)]
public sealed class PostgresFirmStoreTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly PostgresHarness _harness = new(postgres);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _harness.InitializeAsync();
        await _harness.SeedFirmsAsync();
    }

    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    [Fact]
    public async Task A_stored_permission_map_drops_features_and_levels_this_version_does_not_know()
    {
        var store = new PostgresFirmStore(_harness.NewContext());
        await store.AddUserAsync(Make.User(), Ct);

        // A row written by a newer version: a feature and a level we have never heard of.
        await ExecuteAsync(
            """
            UPDATE insolvia.firm_users
            SET permissions = '{"cases": "view_only", "teleportation": "add_edit", "notes": "superuser", "tasks": 2}'::jsonb
            """);

        var read = await new PostgresFirmStore(_harness.NewContext()).GetUserAsync(Ids.Firm, Ids.Alice, Ct);
        Assert.Equal(
            new Dictionary<Feature, PermissionLevel> { [Feature.Cases] = PermissionLevel.ViewOnly }.ToValueDictionary(),
            read!.Permissions);
        Assert.Equal(PermissionLevel.Hidden, Permissions.PermissionFor(read, Feature.Notes));
    }

    [Fact]
    public async Task A_permission_map_that_is_not_an_object_reads_as_empty()
    {
        await new PostgresFirmStore(_harness.NewContext()).AddUserAsync(Make.User(), Ct);
        await ExecuteAsync("UPDATE insolvia.firm_users SET permissions = '[\"cases\"]'::jsonb");
        Assert.Empty((await new PostgresFirmStore(_harness.NewContext()).GetUserAsync(Ids.Firm, Ids.Alice, Ct))!.Permissions);
    }

    [Fact]
    public async Task A_merge_pointer_cannot_name_a_client_in_another_firm()
    {
        var store = new PostgresFirmStore(_harness.NewContext());
        var mine = Make.Client();
        var theirs = Make.Client(Ids.OtherFirm, "Theirs");
        await store.CreateClientAsync(mine, Ct);
        await store.CreateClientAsync(theirs, Ct);

        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            $"UPDATE insolvia.firm_clients SET merging_into = '{theirs.Id}' WHERE id = '{mine.Id}'"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
    }

    [Fact]
    public async Task A_tenant_row_cannot_name_a_firm_that_does_not_exist()
    {
        var store = new PostgresFirmStore(_harness.NewContext());
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => store.AddUserAsync(Make.User(firmId: "no-such-firm"), Ct));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ((PostgresException)error.InnerException!).SqlState);
    }

    [Fact]
    public async Task The_link_seam_admits_an_active_unmerged_client_and_one_receiving_a_merge()
    {
        var store = new PostgresFirmStore(_harness.NewContext());
        var (merged, survivor, archived) = (Make.Client(surname: "Merged"), Make.Client(surname: "Survivor"), Make.Client(surname: "Archived"));
        await store.CreateClientAsync(merged, Ct);
        await store.CreateClientAsync(survivor, Ct);
        await store.CreateClientAsync(FirmClientRules.SetStatus(archived, FirmClientStatus.Archived), Ct);
        Assert.True(await store.ClaimClientMergeAsync(Ids.Firm, merged.Id, survivor.Id, Ct));

        var db = _harness.NewContext();
        Assert.True(await FirmClientLinks.LockLinkableAsync(db, Ids.Firm, survivor.Id, Ct));
        Assert.False(await FirmClientLinks.LockLinkableAsync(db, Ids.Firm, merged.Id, Ct));
        Assert.False(await FirmClientLinks.LockLinkableAsync(db, Ids.Firm, archived.Id, Ct));
        Assert.False(await FirmClientLinks.LockLinkableAsync(db, Ids.OtherFirm, survivor.Id, Ct));
        Assert.False(await FirmClientLinks.LockLinkableAsync(db, Ids.Firm, "no-such-client", Ct));
    }

    [Fact]
    public async Task Documents_are_jsonb_and_everything_conditioned_on_is_a_column()
    {
        var columns = await ColumnTypesAsync();

        Assert.Equal("jsonb", columns[("firm_clients", "body")]);
        Assert.Equal("date", columns[("firm_clients", "first_retained_at")]);
        Assert.Equal("text", columns[("firm_clients", "merging_into")]);
        Assert.Equal("text", columns[("firm_clients", "prospect_stage")]);
        Assert.Equal("jsonb", columns[("firm_users", "permissions")]);
        Assert.Equal("jsonb", columns[("firms", "letterhead")]);
        Assert.Equal("ARRAY", columns[("client_bindings", "roles")]);
        Assert.Equal("timestamp with time zone", columns[("access_log", "recorded_at")]);
        Assert.DoesNotContain(columns.Values, type => type is "serial" or "bigserial");
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_harness.ConnectionString);
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // Test-authored SQL over fixture ids.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<Dictionary<(string Table, string Column), string>> ColumnTypesAsync()
    {
        await using var connection = new NpgsqlConnection(_harness.ConnectionString);
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
