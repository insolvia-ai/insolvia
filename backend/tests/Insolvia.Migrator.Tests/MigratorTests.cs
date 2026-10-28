using Insolvia.Data.Connections;
using Insolvia.Data.Tests;
using Npgsql;

namespace Insolvia.Migrator.Tests;

// Roles are cluster-wide, so every test here shares the one collection (and
// therefore runs serially) and reads the roles' state rather than assuming
// it starts empty.
[Collection(PostgresTests.Name)]
public sealed class MigratorTests(PostgresFixture postgres)
{
    private const string RolePassword = "insolvia-test-only";

    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task A_second_run_applies_nothing_and_leaves_the_same_roles()
    {
        var connectionString = await postgres.CreateDatabaseAsync();

        var first = await RunAsync(connectionString, []);
        var second = await RunAsync(connectionString, []);

        Assert.Contains(first.AppliedMigrations, name => name.EndsWith("_InitialSchema", StringComparison.Ordinal));
        Assert.Empty(second.AppliedMigrations);
        Assert.Equal(first.Migrations, second.Migrations);
        Assert.Equal(["api", "worker", "admin", "mcp", "filing", "migrator"], second.Roles);
        Assert.Equal("Password", second.RoleLogin);
        Assert.Equal(
            first.Migrations.Count,
            await ScalarAsync<long>(connectionString, "SELECT count(*) FROM insolvia.__ef_migrations_history"));
    }

    [Fact]
    public async Task Every_service_role_can_log_in_locally_and_use_the_schema_but_not_create_in_it()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await RunAsync(connectionString, []);

        foreach (var role in ServiceRoles.All)
        {
            Assert.Equal(role, await ScalarAsync<string>(As(connectionString, role), "SELECT current_user"));
            Assert.True(await ScalarAsync<bool>(As(connectionString, role), "SELECT has_schema_privilege('insolvia', 'USAGE')"));
            Assert.False(await ScalarAsync<bool>(As(connectionString, role), "SELECT has_schema_privilege('insolvia', 'CREATE')"));
        }
    }

    [Fact]
    public async Task An_append_only_table_takes_inserts_and_reads_and_refuses_update_delete_and_truncate()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await RunAsync(connectionString, []);
        await ExecuteAsync(connectionString, "CREATE TABLE insolvia.access_log_demo (id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, what text NOT NULL)");

        await RunAsync(connectionString, [TableGrant.AppendOnly("access_log_demo", ServiceRoles.Api)]);

        var api = As(connectionString, ServiceRoles.Api);
        await ExecuteAsync(api, "INSERT INTO insolvia.access_log_demo (what) VALUES ('opened case')");
        Assert.Equal(1L, await ScalarAsync<long>(api, "SELECT count(*) FROM insolvia.access_log_demo"));
        foreach (var rewrite in new[]
        {
            "UPDATE insolvia.access_log_demo SET what = 'nothing happened'",
            "DELETE FROM insolvia.access_log_demo",
            "TRUNCATE insolvia.access_log_demo",
        })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(api, rewrite));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }

        // A role the table does not name cannot read it at all.
        var worker = await Assert.ThrowsAsync<PostgresException>(() =>
            ScalarAsync<long>(As(connectionString, ServiceRoles.Worker), "SELECT count(*) FROM insolvia.access_log_demo"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, worker.SqlState);
    }

    [Fact]
    public async Task A_grant_removed_from_the_table_is_revoked_on_the_next_run()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await RunAsync(connectionString, []);
        await ExecuteAsync(connectionString, "CREATE TABLE insolvia.note_demo (id int PRIMARY KEY)");
        await RunAsync(connectionString, [new TableGrant("note_demo", ServiceRoles.Api, TablePrivileges.ReadWrite)]);
        Assert.Equal(0L, await ScalarAsync<long>(As(connectionString, ServiceRoles.Api), "SELECT count(*) FROM insolvia.note_demo"));

        var report = await RunAsync(connectionString, []);

        Assert.Equal(["note_demo"], report.Tables);
        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            ScalarAsync<long>(As(connectionString, ServiceRoles.Api), "SELECT count(*) FROM insolvia.note_demo"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    [Fact]
    public async Task A_grant_naming_a_missing_table_or_an_unknown_role_fails_the_run()
    {
        var connectionString = await postgres.CreateDatabaseAsync();

        var table = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunAsync(connectionString, [TableGrant.AppendOnly("no_such_table", ServiceRoles.Api)]));
        Assert.Contains("no_such_table", table.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            RunAsync(connectionString, [TableGrant.AppendOnly("x", "postgres")]));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            RunAsync(connectionString, [TableGrant.AppendOnly("x; DROP TABLE y", ServiceRoles.Api)]));
    }

    [Fact]
    public async Task Without_rds_iam_and_without_a_password_the_run_refuses_to_guess()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var dataSource = InsolviaDataSource.Create(new DatabaseSettings { ConnectionString = connectionString });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MigrationRunner.RunAsync(dataSource, [], localPassword: null, None));

        Assert.Contains("INSOLVIA_DB_ROLE_PASSWORD", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task On_a_database_with_rds_iam_every_role_is_granted_it_and_loses_its_password()
    {
        // Aurora is recognised by its rds_iam role; a stand-in makes this
        // Postgres look like one for the length of the test.
        var connectionString = await postgres.CreateDatabaseAsync();
        await RunAsync(connectionString, []);
        await ExecuteAsync(connectionString, "CREATE ROLE rds_iam NOLOGIN");
        try
        {
            var report = await RunAsync(connectionString, []);

            Assert.Equal("Iam", report.RoleLogin);
            foreach (var role in ServiceRoles.All)
            {
                Assert.True(await ScalarAsync<bool>(connectionString, $"SELECT pg_has_role('{role}', 'rds_iam', 'MEMBER')"));
                Assert.True(await ScalarAsync<bool>(connectionString, $"SELECT rolpassword IS NULL FROM pg_authid WHERE rolname = '{role}'"));
            }
        }
        finally
        {
            await ExecuteAsync(connectionString, "DROP OWNED BY rds_iam; DROP ROLE rds_iam");
            await RunAsync(connectionString, []);
        }
    }

    [Fact]
    public void Local_settings_take_a_connection_string_and_deployed_settings_take_the_ssm_contract()
    {
        var local = MigratorSettings.FromEnvironment(name => name switch
        {
            "INSOLVIA_DB_CONNECTION_STRING" => "Host=postgres",
            "INSOLVIA_DB_ROLE_PASSWORD" => "pw",
            _ => null,
        });
        var deployed = MigratorSettings.FromEnvironment(name => name switch
        {
            "INSOLVIA_DB_ENDPOINT" => "db.cluster.example",
            "INSOLVIA_DB_PORT" => "5432",
            "INSOLVIA_DB_DATABASE_NAME" => "insolvia",
            "INSOLVIA_DB_MASTER_SECRET_ARN" => "arn:aws:secretsmanager:us-east-1:000000000000:secret:rds!cluster-example",
            _ => null,
        });

        Assert.Equal(("Host=postgres", "pw"), (local.ConnectionString, local.RolePassword));
        Assert.Null(deployed.ConnectionString);
        Assert.Equal("db.cluster.example", deployed.Endpoint);
        Assert.StartsWith("arn:aws:secretsmanager:", deployed.MasterSecretArn, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => MigratorSettings.FromEnvironment(_ => null));
    }

    private static async Task<MigrationReport> RunAsync(string connectionString, IReadOnlyList<TableGrant> grants)
    {
        await using var dataSource = InsolviaDataSource.Create(new DatabaseSettings { ConnectionString = connectionString });
        return await MigrationRunner.RunAsync(dataSource, grants, RolePassword, None);
    }

    private static string As(string connectionString, string role) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Username = role, Password = RolePassword, Pooling = false }.ConnectionString;

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(None);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(None);
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(None);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(None))!;
    }
}
