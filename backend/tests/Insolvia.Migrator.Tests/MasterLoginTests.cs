using Insolvia.Data.Connections;
using Insolvia.Data.Tests;
using Npgsql;

namespace Insolvia.Migrator.Tests;

// On RDS a login that is a member of rds_iam — directly or through another
// role, whatever its INHERIT — must sign in with an IAM token; its password is
// refused (28000, "PAM authentication failed"). The staging migrator granted
// the master SET on the migrator role so the seeder could SET ROLE, and so
// locked the master out of the secret's password (2026-10-30). These tests
// pin the three parts of the fix: the provisioner keeps the master out of
// every service role (and repairs a master that joined one), the seeder logs
// in AS migrator, and a locked-out master gets back in once with a token.
//
// Aurora is stood in for as MigratorTests does: a stand-in rds_iam role, and
// a CREATEROLE, non-superuser login that owns the database and administers the
// service roles — as the RDS master does, having created them.
[Collection(PostgresTests.Name)]
public sealed class MasterLoginTests(PostgresFixture postgres)
{
    private const string Password = "insolvia-test-only";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Provisioning_revokes_a_masters_membership_in_the_migrator_role_and_never_grants_one()
    {
        var bootstrap = await postgres.CreateDatabaseAsync();
        await MigrationRunner.RunAsync(NpgsqlDataSource.Create(bootstrap), GrantTable.All, Password, Ct);
        var master = $"aurora_master_{Guid.NewGuid():N}"[..28];
        var database = $"aurora_{Guid.NewGuid():N}";
        await ExecuteAsync(
            postgres.SuperuserConnectionString,
            $"CREATE ROLE {master} LOGIN CREATEROLE PASSWORD '{Password}'",
            $"GRANT api, worker, admin, mcp, filing, migrator TO {master} WITH ADMIN TRUE, INHERIT FALSE, SET FALSE",
            "CREATE ROLE rds_iam NOLOGIN",
            $"GRANT rds_iam TO {master} WITH ADMIN TRUE, INHERIT FALSE, SET FALSE",
            $"CREATE DATABASE {database} OWNER {master}");
        var asMaster = new NpgsqlConnectionStringBuilder(postgres.SuperuserConnectionString) { Database = database, Username = master, Password = Password, Pooling = false }.ConnectionString;
        try
        {
            // What the broken migrator left on staging: the master holds SET
            // on migrator, which holds rds_iam.
            await ExecuteAsync(asMaster, "GRANT migrator TO CURRENT_USER WITH INHERIT FALSE, SET TRUE", "GRANT rds_iam TO migrator");
            Assert.True(await ReachesRdsIamAsync(asMaster, master));

            await using (var dataSource = NpgsqlDataSource.Create(asMaster))
            {
                var report = await MigrationRunner.RunAsync(dataSource, GrantTable.All, Password, Ct);
                Assert.Equal("Iam", report.RoleLogin);
            }

            Assert.False(await ReachesRdsIamAsync(asMaster, master));
            Assert.False(await ScalarAsync<bool>(asMaster, $"SELECT pg_has_role('{master}', 'migrator', 'SET') OR pg_has_role('{master}', 'migrator', 'USAGE')"));
            Assert.True(await ScalarAsync<bool>(asMaster, "SELECT pg_has_role('migrator', 'rds_iam', 'MEMBER')"));

            // A second run finds nothing to revoke and grants nothing back.
            await using (var again = NpgsqlDataSource.Create(asMaster))
            {
                await MigrationRunner.RunAsync(again, GrantTable.All, Password, Ct);
            }

            Assert.False(await ReachesRdsIamAsync(asMaster, master));
        }
        finally
        {
            await ExecuteAsync(postgres.SuperuserConnectionString, "DROP ROLE rds_iam");

            // Give every service role its password back for the tests that follow.
            await MigrationRunner.RunAsync(NpgsqlDataSource.Create(bootstrap), GrantTable.All, Password, Ct);
        }
    }

    [Fact]
    public async Task The_seeder_logs_in_as_the_migrator_role_rather_than_setting_it_on_the_masters_login()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await MigrationRunner.RunAsync(NpgsqlDataSource.Create(connectionString), GrantTable.All, Password, Ct);
        var settings = MigratorSettings.FromEnvironment(name => name switch
        {
            "INSOLVIA_DB_CONNECTION_STRING" => connectionString,
            "INSOLVIA_DB_ROLE_PASSWORD" => Password,
            _ => null,
        });

        await using (var seeding = await settings.CreateDataSourceAsync(Ct, role: ServiceRoles.Migrator))
        {
            await using var whoami = seeding.CreateCommand("SELECT session_user || '/' || current_user");
            Assert.Equal("migrator/migrator", await whoami.ExecuteScalarAsync(Ct));
        }

        var withoutPassword = settings with { RolePassword = null };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => withoutPassword.CreateDataSourceAsync(Ct, role: ServiceRoles.Migrator));
        Assert.Contains("INSOLVIA_DB_ROLE_PASSWORD", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Deployed_the_seeder_logs_in_as_migrator_with_an_iam_token_over_tls()
    {
        var deployed = MigratorSettings.FromEnvironment(name => name switch
        {
            "INSOLVIA_DB_ENDPOINT" => "db.cluster.example",
            "INSOLVIA_DB_DATABASE_NAME" => "insolvia",
            "INSOLVIA_DB_MASTER_SECRET_ARN" => "arn:aws:secretsmanager:us-east-1:000000000000:secret:rds!cluster-example",
            "AWS_REGION" => "us-east-1",
            _ => null,
        });

        var seeder = deployed.IamSettings(ServiceRoles.Migrator);

        Assert.Equal(DatabaseAuthentication.Iam, seeder.Authentication);
        Assert.Equal(("db.cluster.example", 5432, "insolvia", "migrator", "us-east-1"), (seeder.Endpoint, seeder.Port, seeder.DatabaseName, seeder.Username, seeder.Region));
        Assert.Equal(SslMode.Require, seeder.SslMode);
        Assert.Equal(300, seeder.CommandTimeout);
    }

    [Fact]
    public async Task A_master_refused_its_password_by_pam_signs_in_once_with_an_iam_token()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var superuser = new NpgsqlConnectionStringBuilder(connectionString);
        var (host, port) = postgres.Address;
        var tokens = new FixedTokens(superuser.Password!);
        var lines = new List<string>();
        var withPassword = NpgsqlDataSource.Create(connectionString);
        var tokenSources = 0;

        var opened = await MigratorSettings.OpenAsMasterAsync(
            withPassword,
            () =>
            {
                tokenSources++;
                return InsolviaDataSource.Create(
                    new DatabaseSettings { Endpoint = host, Port = port, DatabaseName = superuser.Database, Username = superuser.Username, Region = "us-east-1", SslMode = SslMode.Disable },
                    tokens);
            },
            lines.Add,
            Ct,
            (source, token) => source == withPassword
                ? throw Pam(superuser.Username!)
                : DatabaseWake.OpenAsync(source, token));
        await using (opened)
        {
            Assert.NotSame(withPassword, opened);
            await using var whoami = opened.CreateCommand("SELECT current_user");
            Assert.Equal(superuser.Username, await whoami.ExecuteScalarAsync(Ct));
        }

        Assert.Equal(1, tokenSources);
        Assert.Equal(superuser.Username, tokens.LastUsername);
        var line = Assert.Single(lines);
        Assert.Contains("IAM token", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("28P01", "password authentication failed for user \"insolvia_admin\"")]
    [InlineData("28000", "no pg_hba.conf entry for host \"10.0.0.1\"")]
    public async Task Any_other_refusal_is_thrown_without_trying_a_token(string sqlState, string message)
    {
        var tokenSources = 0;
        var lines = new List<string>();
        await using var withPassword = NpgsqlDataSource.Create("Host=127.0.0.1;Username=x;Password=x");

        var error = await Assert.ThrowsAsync<PostgresException>(() => MigratorSettings.OpenAsMasterAsync(
            withPassword,
            () =>
            {
                tokenSources++;
                return NpgsqlDataSource.Create("Host=127.0.0.1");
            },
            lines.Add,
            Ct,
            (_, _) => throw new PostgresException(message, "FATAL", "FATAL", sqlState)));

        Assert.Equal(sqlState, error.SqlState);
        Assert.Equal(0, tokenSources);
        Assert.Empty(lines);
    }

    [Fact]
    public void Only_a_pam_refusal_counts_as_iam_only()
    {
        Assert.True(MigratorSettings.IsIamOnlyRefusal(Pam("insolvia_admin")));
        Assert.False(MigratorSettings.IsIamOnlyRefusal(new PostgresException("password authentication failed for user \"insolvia_admin\"", "FATAL", "FATAL", "28P01")));
        Assert.False(MigratorSettings.IsIamOnlyRefusal(new PostgresException("no pg_hba.conf entry", "FATAL", "FATAL", "28000")));
    }

    private static PostgresException Pam(string user) =>
        new($"PAM authentication failed for user \"{user}\"", "FATAL", "FATAL", "28000");

    private static Task<bool> ReachesRdsIamAsync(string connectionString, string role) =>
        ScalarAsync<bool>(connectionString, $"SELECT pg_has_role('{role}', 'rds_iam', 'SET') OR pg_has_role('{role}', 'rds_iam', 'USAGE')");

    private static async Task ExecuteAsync(string connectionString, params string[] statements)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        foreach (var sql in statements)
        {
#pragma warning disable CA2100 // Test constants and GUIDs.
            await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
            await command.ExecuteNonQueryAsync(Ct);
        }
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // Test constants.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    private sealed class FixedTokens(string token) : IDatabaseTokenProvider
    {
        public string? LastUsername { get; private set; }

        public ValueTask<string> GetTokenAsync(string host, int port, string username, CancellationToken cancellationToken)
        {
            LastUsername = username;
            return ValueTask.FromResult(token);
        }
    }
}
