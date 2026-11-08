using Insolvia.Core.Local;
using Insolvia.Data.Tests;
using Insolvia.Migrator.Seeding;
using Npgsql;

namespace Insolvia.Migrator.Tests.Seeding;

// The loader against a real Postgres, as it runs in the migrator: migrate,
// then seed the COMMITTED fixtures through the Postgres stores AS THE
// MIGRATOR ROLE (so a grant the seeder lacks fails here, not on a staging
// deploy), then seed again and see nothing written. Locally the subjects are
// derived and the bytes land in a directory; the stores are the real ones.
[Collection(PostgresTests.Name)]
public sealed class SeedPostgresTests(PostgresFixture postgres) : IDisposable
{
    private const string RolePassword = "insolvia-test-only";
    private readonly string _documents = Path.Combine(Path.GetTempPath(), "insolvia-seed-pg-tests", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_documents))
        {
            Directory.Delete(_documents, recursive: true);
        }
    }

    [Theory]
    [InlineData("dev", "local")]
    [InlineData("staging", "staging")]
    public async Task The_committed_fixture_seeds_as_the_migrator_role_and_a_second_run_writes_nothing(string file, string environment)
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var run = MigratorCommand.FromArguments(["migrate"]);
        var variables = Variables(connectionString, environment, file);
        Assert.Equal(MigratorRun.Ok, (await run.RunAsync(variables, isLambda: false, Ct)).ExitCode);

        var first = await MigratorCommand.FromArguments(["seed"]).RunAsync(variables, isLambda: false, Ct);
        Assert.True(first.ExitCode == MigratorRun.Ok, first.Refusal);
        var counts = await CountsAsync(connectionString);
        var second = await MigratorCommand.FromArguments(["seed"]).RunAsync(variables, isLambda: false, Ct);
        var check = await MigratorCommand.FromArguments(["seed", "--check"]).RunAsync(variables, isLambda: false, Ct);

        Assert.True(first.Seed!.Missing > 0);
        Assert.Contains(first.Lines, line => line.StartsWith("  case 'chapter-7-sample': created", StringComparison.Ordinal));
        Assert.Equal(0, second.Seed!.Missing);
        Assert.All(second.Lines, line => Assert.DoesNotContain("created", line, StringComparison.Ordinal));
        Assert.Equal(MigratorRun.Ok, check.ExitCode);
        Assert.Equal(counts, await CountsAsync(connectionString));
        Assert.Equal(2L, counts["cases"]);
        Assert.Equal(2L, counts["documents"]);
        // The two objects, each with its content-type sidecar (DirectoryDocumentBlobStore).
        var files = Directory.GetFiles(_documents, "*", SearchOption.AllDirectories);
        Assert.Equal(2, files.Count(path => !path.EndsWith(DirectoryDocumentBlobStore.ContentTypeSuffix, StringComparison.Ordinal)));
        Assert.Equal(2, files.Count(path => path.EndsWith(DirectoryDocumentBlobStore.ContentTypeSuffix, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task The_seeder_runs_as_the_migrator_role_and_production_is_refused_before_connecting()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var variables = Variables(connectionString, "local", "dev");
        await MigratorCommand.FromArguments([]).RunAsync(variables, isLambda: false, Ct);
        var settings = MigratorSettings.FromEnvironment(variables);

        var dataSource = await settings.CreateDataSourceAsync(Ct, role: ServiceRoles.Migrator);
        await using (dataSource)
        {
            await using var command = dataSource.CreateCommand("SELECT current_user");
            Assert.Equal("migrator", await command.ExecuteScalarAsync(Ct));

            // The role inserts what a load writes and deletes nothing.
            await using var delete = dataSource.CreateCommand("DELETE FROM insolvia.cases");
            var refused = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync(Ct));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
        }

        var production = await MigratorCommand.FromArguments(["seed"]).RunAsync(name => name == "INSOLVIA_ENV" ? "production" : variables(name), isLambda: false, Ct);
        Assert.Equal(MigratorRun.Refused, production.ExitCode);
        Assert.Contains("production", production.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_default_run_seeds_only_when_asked()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var variables = Variables(connectionString, "local", "dev");

        var plain = await MigratorCommand.FromArguments([]).RunAsync(variables, isLambda: false, Ct);
        var seeding = await MigratorCommand.FromArguments([]).RunAsync(name => name == SeedComposition.SeedVariable ? "true" : variables(name), isLambda: false, Ct);

        Assert.Null(plain.Seed);
        Assert.NotNull(seeding.Migration);
        Assert.NotNull(seeding.Seed);
        Assert.Equal(2L, (await CountsAsync(connectionString))["cases"]);
    }

    [Fact]
    public async Task A_deployed_seed_refuses_the_local_fallbacks()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var variables = Variables(connectionString, "staging", "staging");
        await MigratorCommand.FromArguments([]).RunAsync(variables, isLambda: false, Ct);

        var run = await MigratorCommand.FromArguments(["seed"]).RunAsync(variables, isLambda: true, Ct);

        Assert.Equal(MigratorRun.Refused, run.ExitCode);
        Assert.Contains("AUTH_USER_POOL_ID", run.Refusal, StringComparison.Ordinal);
    }

    private Func<string, string?> Variables(string connectionString, string environment, string file) => name => name switch
    {
        "INSOLVIA_DB_CONNECTION_STRING" => connectionString,
        "INSOLVIA_DB_ROLE_PASSWORD" => RolePassword,
        "INSOLVIA_ENV" => environment,
        "INSOLVIA_SEED_FIXTURE" => Path.Combine(Repository.Seeds, $"{file}.json"),
        "INSOLVIA_SEED_DOCUMENT_DIR" => _documents,
        "E2E_TEST_USER_PASSWORD" => "Example-Passw0rd-not-real",
        _ => null,
    };

    private static async Task<Dictionary<string, long>> CountsAsync(string connectionString)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        foreach (var table in new[] { "firms", "firm_users", "firm_clients", "cases", "case_assignments", "debtors", "case_entities", "documents", "tax_ids", "client_bindings", "case_lifecycle_events" })
        {
#pragma warning disable CA2100 // Constant table names.
            await using var command = new NpgsqlCommand($"SELECT count(*) FROM insolvia.{table}", connection);
#pragma warning restore CA2100
            counts[table] = (long)(await command.ExecuteScalarAsync(Ct))!;
        }

        return counts;
    }
}
