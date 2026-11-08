using System.Text.Json.Nodes;
using Insolvia.Core.Cases;
using Insolvia.Core.Clients;
using Insolvia.Core.Debtors;
using Insolvia.Core.FirmClients;
using Insolvia.Core.Firms;
using Insolvia.Data;
using Insolvia.Data.Cases;
using Insolvia.Data.Firms;
using Insolvia.Data.Pagination;
using Insolvia.Data.Portal;
using Insolvia.Data.Tests;
using Insolvia.Migrator.Purge;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Insolvia.Migrator.Tests;

// services/admin tests/unit/test_purge_pre_client.py in intent, against a real
// Postgres: the rule (a case is pre-client iff no debtor names a client), what
// goes with a purged case (everything that belongs to it, by cascade), what
// can never be deleted, dry run by default, and a second run deleting nothing.
// The Python's partition-shape tests (no META root, non-case partitions, the
// scan's projection, an empty clientId) have no relational counterpart: a
// child row cannot exist without its case (foreign key), and client_id is
// either a firm client's id or NULL.
[Collection(PostgresTests.Name)]
public sealed class PurgeTests(PostgresFixture postgres)
{
    private const string Opener = "00000000-0000-4000-8000-00000000a11c";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class World(string connectionString, NpgsqlDataSource dataSource, InsolviaDbContext db) : IAsyncDisposable
    {
        public string ConnectionString { get; } = connectionString;

        public NpgsqlDataSource DataSource { get; } = dataSource;

        public PostgresFirmStore Firms { get; } = new(db, TimeProvider.System);

        public PostgresCaseStore Cases { get; } = new(db, new CursorCodec(new byte[32]));

        public PostgresDebtorStore Debtors { get; } = new(db);

        public PostgresClientBindingStore Bindings { get; } = new(db);

        public string FirmId { get; set; } = string.Empty;

        public async ValueTask DisposeAsync()
        {
            await db.DisposeAsync();
            await DataSource.DisposeAsync();
        }
    }

    private async Task<World> WorldAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await MigratorCommand.FromArguments([]).RunAsync(name => name switch
        {
            "INSOLVIA_DB_CONNECTION_STRING" => connectionString,
            "INSOLVIA_DB_ROLE_PASSWORD" => "insolvia-test-only",
            _ => null,
        }, isLambda: false, Ct);
        var dataSource = NpgsqlDataSource.Create(connectionString);
        var options = new DbContextOptionsBuilder<InsolviaDbContext>();
        options.UseInsolviaNpgsql(dataSource);
        var world = new World(connectionString, dataSource, new InsolviaDbContext(options.Options));
        var firm = FirmLifecycle.CreateFirm(FirmParser.ParseFirmCreation(new JsonObject { ["name"] = "Example LLP" }));
        await world.Firms.CreateFirmAsync(firm, Ct);
        world.FirmId = firm.Id;
        return world;
    }

    private static Case NewCase(World world) =>
        CaseRules.Create(CaseRules.ParseCreation(new JsonObject { ["chapter"] = 7, ["court"] = "flmb", ["division"] = "tampa" }, requireClients: false), world.FirmId, Opener).Case;

    private static DebtorDraft Draft(string given) =>
        DebtorRules.Parse(new JsonObject { ["name"] = new JsonObject { ["given"] = given }, ["provenance"] = new JsonObject { ["name.given"] = new JsonObject { ["source"] = "staff_typed" } } });

    /// <summary>A case written before clients: debtors with no client_id.</summary>
    private static async Task<string> PreClientCaseAsync(World world, int debtors = 1)
    {
        var created = NewCase(world);
        await world.Cases.CreateAsync(created, CaseRules.Assign(created, Opener, Opener), [], Ct);
        foreach (var role in new[] { FilingRole.Debtor1, FilingRole.Debtor2 }.Take(debtors))
        {
            await world.Debtors.CreateAsync(DebtorRules.Create(Draft("Old"), created.Id, role), Ct);
        }

        return created.Id;
    }

    private static async Task<FirmClient> ClientAsync(World world)
    {
        var client = FirmClientRules.Create(FirmClientRules.Parse(new JsonObject { ["name"] = new JsonObject { ["given"] = "Sample" } }), world.FirmId, Opener);
        await world.Firms.CreateClientAsync(client, Ct);
        return client;
    }

    /// <summary>A case opened for a client, as POST /v1/cases opens one.</summary>
    private static async Task<string> ClientCaseAsync(World world)
    {
        var client = await ClientAsync(world);
        var created = NewCase(world);
        await world.Cases.CreateAsync(
            created,
            CaseRules.Assign(created, Opener, Opener),
            [DebtorRules.Create(Draft("Sample"), created.Id, FilingRole.Debtor1, clientId: client.Id, caseCreatedAt: created.CreatedAt)],
            Ct);
        return created.Id;
    }

    private static async Task<long> CountAsync(World world, string table)
    {
        await using var connection = new NpgsqlConnection(world.ConnectionString);
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // Constant table names.
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM insolvia.{table}", connection);
#pragma warning restore CA2100
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    [Fact]
    public async Task A_case_whose_debtors_name_no_client_is_deleted_whole()
    {
        await using var world = await WorldAsync();
        var doomed = await PreClientCaseAsync(world, debtors: 2);

        var report = await PreClientPurge.RunAsync(world.DataSource, apply: true, Ct);

        Assert.Equal([doomed], report.Deleted);
        Assert.Equal(0, await CountAsync(world, "cases"));
        Assert.Equal(0, await CountAsync(world, "debtors"));
        Assert.Equal(0, await CountAsync(world, "case_assignments"));

        // The case row, its assignment and its two debtors.
        Assert.Equal(4, report.RowsDeleted);
    }

    [Fact]
    public async Task A_case_opened_for_a_client_is_never_planned()
    {
        await using var world = await WorldAsync();
        var kept = await ClientCaseAsync(world);

        var report = await PreClientPurge.RunAsync(world.DataSource, apply: true, Ct);

        Assert.Empty(report.Planned);
        Assert.Equal(1, report.KeptWithClient);
        Assert.NotNull(await world.Cases.ReadForWorkerAsync(kept, Ct));
    }

    [Fact]
    public async Task A_case_with_no_debtors_at_all_is_pre_client()
    {
        await using var world = await WorldAsync();
        var doomed = await PreClientCaseAsync(world, debtors: 0);

        Assert.Equal([doomed], (await PreClientPurge.RunAsync(world.DataSource, apply: false, Ct)).Planned);
    }

    [Fact]
    public async Task A_dry_run_writes_nothing_and_a_second_run_deletes_nothing()
    {
        await using var world = await WorldAsync();
        await PreClientCaseAsync(world);
        await ClientCaseAsync(world);

        var dry = await PreClientPurge.RunAsync(world.DataSource, apply: false, Ct);
        Assert.Single(dry.Planned);
        Assert.Empty(dry.Deleted);
        Assert.Equal(2, await CountAsync(world, "cases"));

        Assert.Single((await PreClientPurge.RunAsync(world.DataSource, apply: true, Ct)).Deleted);
        var again = await PreClientPurge.RunAsync(world.DataSource, apply: true, Ct);
        Assert.Empty(again.Planned);
        Assert.Empty(again.Deleted);
        Assert.Equal(1, await CountAsync(world, "cases"));
    }

    [Fact]
    public async Task A_client_linked_after_the_plan_saves_the_case()
    {
        // The statement's own WHERE is the fence: a debtor that names a client
        // by the time of the delete stops it, and nothing half-deletes.
        await using var world = await WorldAsync();
        var saved = await PreClientCaseAsync(world);
        var client = await ClientAsync(world);

        var report = await PreClientPurge.RunAsync(world.DataSource, apply: true, Ct, beforeDelete: async caseId =>
        {
            await using var connection = new NpgsqlConnection(world.ConnectionString);
            await connection.OpenAsync(Ct);
            await using var link = new NpgsqlCommand("UPDATE insolvia.debtors SET client_id = @client, case_created_at = now() WHERE case_id = @case", connection);
            link.Parameters.AddWithValue("client", client.Id);
            link.Parameters.AddWithValue("case", caseId);
            await link.ExecuteNonQueryAsync(Ct);
        });

        Assert.Equal([saved], report.Planned);
        Assert.Equal([saved], report.Stopped);
        Assert.Empty(report.Deleted);
        Assert.Equal(1, await CountAsync(world, "debtors"));
        Assert.NotNull(await world.Cases.ReadForWorkerAsync(saved, Ct));
    }

    [Fact]
    public async Task A_purged_cases_portal_binding_goes_with_it_and_another_cases_stays()
    {
        await using var world = await WorldAsync();
        var doomed = await PreClientCaseAsync(world);
        var kept = await ClientCaseAsync(world);
        foreach (var (caseId, subject) in new[] { (doomed, "00000000-0000-4000-8000-0000000000c1"), (kept, "00000000-0000-4000-8000-0000000000c2") })
        {
            var draft = ClientBindings.ParseInvitation(new JsonObject { ["email"] = $"{subject[^2..]}@insolvia.test", ["displayName"] = "Sample", ["roles"] = new JsonArray("debtor_1") });
            await world.Bindings.BindAsync(ClientBindings.CreateBinding(draft, world.FirmId, caseId, subject, Opener), [], Ct);
        }

        var report = await PreClientPurge.RunAsync(world.DataSource, apply: true, Ct);

        Assert.Equal(1, report.BindingsDeleted);
        Assert.Null(await world.Bindings.GetAsync(world.FirmId, "00000000-0000-4000-8000-0000000000c1", Ct));
        Assert.NotNull(await world.Bindings.GetAsync(world.FirmId, "00000000-0000-4000-8000-0000000000c2", Ct));
    }

    [Fact]
    public async Task The_access_log_is_never_purged()
    {
        await using var world = await WorldAsync();
        var doomed = await PreClientCaseAsync(world);
        await using (var connection = new NpgsqlConnection(world.ConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using var log = new NpgsqlCommand(
                "INSERT INTO insolvia.access_log (event_id, subject_kind, subject_id, action, principal, outcome, recorded_at) VALUES ('e1', 'CASE', @case, 'case.read', 'p', 'allowed', now())",
                connection);
            log.Parameters.AddWithValue("case", doomed);
            await log.ExecuteNonQueryAsync(Ct);
        }

        await PreClientPurge.RunAsync(world.DataSource, apply: true, Ct);

        Assert.Equal(1, await CountAsync(world, "access_log"));
    }

    [Theory]
    [InlineData("local")]
    [InlineData("staging")]
    [InlineData("production")]
    public async Task Every_environment_may_run_it_and_check_fails_while_anything_remains(string environment)
    {
        await using var world = await WorldAsync();
        await PreClientCaseAsync(world);
        string? Variables(string name) => name switch
        {
            "INSOLVIA_DB_CONNECTION_STRING" => world.ConnectionString,
            "INSOLVIA_ENV" => environment,
            _ => null,
        };

        var check = await MigratorCommand.FromArguments(["purge-pre-client", "--check"]).RunAsync(Variables, isLambda: false, Ct);
        var dry = await MigratorCommand.FromArguments(["purge-pre-client"]).RunAsync(Variables, isLambda: false, Ct);
        var apply = await MigratorCommand.FromArguments(["purge-pre-client", "--apply"]).RunAsync(Variables, isLambda: false, Ct);
        var after = await MigratorCommand.FromArguments(["purge-pre-client", "--check"]).RunAsync(Variables, isLambda: false, Ct);

        Assert.Equal(MigratorRun.Missing, check.ExitCode);
        Assert.Equal(MigratorRun.Ok, dry.ExitCode);
        Assert.StartsWith($"{environment}: 1 pre-client case(s); 0 opened for clients, kept", dry.Lines[0], StringComparison.Ordinal);
        Assert.StartsWith("  would delete case ", dry.Lines[1], StringComparison.Ordinal);
        Assert.Equal(MigratorRun.Ok, apply.ExitCode);
        Assert.StartsWith("  deleted case ", apply.Lines[1], StringComparison.Ordinal);
        Assert.Equal(MigratorRun.Ok, after.ExitCode);
    }

    [Fact]
    public void Apply_and_check_are_exclusive()
    {
        Assert.Throws<ArgumentException>(() => MigratorCommand.FromArguments(["purge-pre-client", "--apply", "--check"]));
    }
}
