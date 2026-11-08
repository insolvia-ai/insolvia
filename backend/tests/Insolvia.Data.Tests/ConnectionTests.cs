using Insolvia.Data.Connections;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insolvia.Data.Tests;

[Collection(PostgresTests.Name)]
public sealed class ConnectionTests(PostgresFixture postgres)
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public void Local_settings_are_a_connection_string()
    {
        var settings = DatabaseSettings.FromEnvironment(Variables(("INSOLVIA_DB_CONNECTION_STRING", "Host=postgres")));

        Assert.Equal(DatabaseAuthentication.ConnectionString, settings.Authentication);
    }

    [Fact]
    public void Deployed_settings_read_the_ssm_contract_names_and_take_the_iam_path()
    {
        var settings = DatabaseSettings.FromEnvironment(Variables(
            ("INSOLVIA_DB_ENDPOINT", "db.cluster.example"),
            ("INSOLVIA_DB_PORT", "5433"),
            ("INSOLVIA_DB_DATABASE_NAME", "insolvia"),
            ("INSOLVIA_DB_USERNAME", "api"),
            ("AWS_REGION", "us-east-1")));

        Assert.Equal(DatabaseAuthentication.Iam, settings.Authentication);
        Assert.Equal(("db.cluster.example", 5433, "insolvia", "api"), (settings.Endpoint, settings.Port, settings.DatabaseName, settings.Username));
        Assert.Equal(SslMode.Require, settings.SslMode);
        Assert.Equal(DatabaseSettings.LambdaMaxPoolSize, settings.MaxPoolSize);
    }

    [Fact]
    public void Missing_deployed_settings_name_every_missing_variable()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            DatabaseSettings.FromEnvironment(Variables(("INSOLVIA_DB_ENDPOINT", "db.cluster.example"))));

        Assert.Contains("INSOLVIA_DB_DATABASE_NAME, INSOLVIA_DB_USERNAME, AWS_REGION", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_iam_path_asks_the_token_provider_for_each_connections_password()
    {
        // A stand-in for RDS: a role whose password is the "token" the
        // provider hands out, on a server without TLS (the real path requires it).
        var connectionString = await postgres.CreateDatabaseAsync();
        await using (var admin = new NpgsqlConnection(connectionString))
        {
            await admin.OpenAsync(None);
            await using var create = new NpgsqlCommand("CREATE ROLE iam_stand_in LOGIN PASSWORD 'token-1'", admin);
            await create.ExecuteNonQueryAsync(None);
        }

        var (host, port) = postgres.Address;
        var tokens = new FixedTokens("token-1");
        var settings = new DatabaseSettings
        {
            Endpoint = host,
            Port = port,
            DatabaseName = new NpgsqlConnectionStringBuilder(connectionString).Database,
            Username = "iam_stand_in",
            Region = "us-east-1",
            SslMode = SslMode.Disable,
        };

        await using var dataSource = InsolviaDataSource.Create(settings, tokens);
        await using var connection = await dataSource.OpenConnectionAsync(None);
        await using var whoami = new NpgsqlCommand("SELECT current_user", connection);

        Assert.Equal("iam_stand_in", await whoami.ExecuteScalarAsync(None));
        Assert.Equal((host, port, "iam_stand_in"), tokens.LastRequest);
        Assert.Equal(DatabaseSettings.LambdaMaxPoolSize, new NpgsqlConnectionStringBuilder(dataSource.ConnectionString).MaxPoolSize);
    }

    [Fact]
    public async Task The_iam_path_keeps_npgsqls_command_timeout_unless_one_is_given()
    {
        var settings = new DatabaseSettings { Endpoint = "db.cluster.example", DatabaseName = "insolvia", Username = "migrator", Region = "us-east-1" };

        await using var plain = InsolviaDataSource.Create(settings, new FixedTokens("unused"));
        await using var raised = InsolviaDataSource.Create(settings with { CommandTimeout = 300 }, new FixedTokens("unused"));

        Assert.Equal(30, new NpgsqlConnectionStringBuilder(plain.ConnectionString).CommandTimeout);
        Assert.Equal(300, new NpgsqlConnectionStringBuilder(raised.ConnectionString).CommandTimeout);
    }

    [Fact]
    public async Task A_host_registers_the_data_tier_and_resolves_a_working_context()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var services = new ServiceCollection()
            .AddInsolviaData(DatabaseSettings.FromEnvironment(Variables(("INSOLVIA_DB_CONNECTION_STRING", connectionString)), "insolvia-test"))
            .BuildServiceProvider();
        await using (services)
        {
            await using var scope = services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<InsolviaDbContext>();

            Assert.True(await context.Database.CanConnectAsync(None));
            Assert.Equal(
                "insolvia-test",
                await context.Database.SqlQuery<string>($"SELECT current_setting('application_name') AS \"Value\"").SingleAsync(None));
        }
    }

    private static Func<string, string?> Variables(params (string Name, string Value)[] values)
    {
        var map = values.ToDictionary(pair => pair.Name, pair => pair.Value);
        return name => map.GetValueOrDefault(name);
    }

    private sealed class FixedTokens(string token) : IDatabaseTokenProvider
    {
        public (string Host, int Port, string Username)? LastRequest { get; private set; }

        public ValueTask<string> GetTokenAsync(string host, int port, string username, CancellationToken cancellationToken)
        {
            LastRequest = (host, port, username);
            return ValueTask.FromResult(token);
        }
    }
}
