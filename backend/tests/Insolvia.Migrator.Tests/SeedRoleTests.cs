using Insolvia.Data.Tests;
using Npgsql;

namespace Insolvia.Migrator.Tests;

// The seeder takes the migrator role with SET ROLE on the migrator's own
// login. Locally (and in every other test) that login is a superuser, which
// may SET ROLE to anything; Aurora's master user is NOT one. This test stands
// in for Aurora: a CREATEROLE, non-superuser login that owns the database and
// administers the service roles — as the master does, having created them —
// runs the provisioner, which must grant it SET (and not INHERIT) on the
// migrator role, after which a session can take the role.
[Collection(PostgresTests.Name)]
public sealed class SeedRoleTests(PostgresFixture postgres)
{
    private const string Master = "aurora_like_master";
    private const string Password = "insolvia-test-only";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_non_superuser_master_gains_set_but_not_inherit_on_the_migrator_role()
    {
        // The service roles exist cluster-wide once any provisioning has run.
        await MigrationRunner.RunAsync(NpgsqlDataSource.Create(await postgres.CreateDatabaseAsync()), GrantTable.All, Password, Ct);
        var database = $"aurora_{Guid.NewGuid():N}";
        await using (var superuser = new NpgsqlConnection(postgres.SuperuserConnectionString))
        {
            await superuser.OpenAsync(Ct);
            foreach (var sql in new[]
            {
                $"DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{Master}') THEN CREATE ROLE {Master} LOGIN CREATEROLE PASSWORD '{Password}'; END IF; END $$",
                $"REVOKE migrator FROM {Master}",
                $"GRANT api, worker, admin, mcp, filing, migrator TO {Master} WITH ADMIN TRUE, INHERIT FALSE, SET FALSE",
                $"CREATE DATABASE {database} OWNER {Master}",
            })
            {
#pragma warning disable CA2100 // Test constants and a GUID.
                await using var command = new NpgsqlCommand(sql, superuser);
#pragma warning restore CA2100
                await command.ExecuteNonQueryAsync(Ct);
            }
        }

        var master = new NpgsqlConnectionStringBuilder(postgres.SuperuserConnectionString) { Database = database, Username = Master, Password = Password };
        await using (var dataSource = NpgsqlDataSource.Create(master.ConnectionString))
        {
            await MigrationRunner.RunAsync(dataSource, GrantTable.All, Password, Ct);
            await using var check = dataSource.CreateCommand(
                $"SELECT pg_has_role('{Master}', 'migrator', 'SET'), pg_has_role('{Master}', 'migrator', 'USAGE')");
            await using var reader = await check.ExecuteReaderAsync(Ct);
            Assert.True(await reader.ReadAsync(Ct));
            Assert.True(reader.GetBoolean(0));
            Assert.False(reader.GetBoolean(1));
        }

        var settings = MigratorSettings.FromEnvironment(name => name switch
        {
            "INSOLVIA_DB_CONNECTION_STRING" => master.ConnectionString,
            _ => null,
        });
        await using (var seeding = await settings.CreateDataSourceAsync(Ct, role: ServiceRoles.Migrator))
        {
            await using var whoami = seeding.CreateCommand("SELECT current_user");
            Assert.Equal("migrator", await whoami.ExecuteScalarAsync(Ct));
        }

        // A second provisioning run finds the membership and changes nothing.
        await using (var again = NpgsqlDataSource.Create(master.ConnectionString))
        {
            await MigrationRunner.RunAsync(again, GrantTable.All, Password, Ct);
        }
    }
}
