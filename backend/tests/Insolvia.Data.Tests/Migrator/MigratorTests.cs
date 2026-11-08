using Insolvia.Core;
using Insolvia.Core.AdminAudit;
using Insolvia.Core.FilingAuthorizations;
using Insolvia.Core.FilingCredentials;
using Insolvia.Core.Firms;
using Insolvia.Core.TaxIds;
using Insolvia.Data;
using Insolvia.Data.AdminAuditing;
using Insolvia.Data.Connections;
using Insolvia.Data.Firms;
using Insolvia.Data.Tests;
using Insolvia.Data.Vault;
using Microsoft.EntityFrameworkCore;
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

        Assert.Contains("note_demo", report.Tables);
        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            ScalarAsync<long>(As(connectionString, ServiceRoles.Api), "SELECT count(*) FROM insolvia.note_demo"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    [Fact]
    public async Task The_access_log_takes_inserts_from_its_writers_and_an_update_or_delete_from_no_service_role()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await RunAsync(connectionString, GrantTable.All);
        string[] writers = [ServiceRoles.Api, ServiceRoles.Mcp, ServiceRoles.Admin, ServiceRoles.Worker];

        foreach (var role in writers)
        {
            await ExecuteAsync(
                As(connectionString, role),
                $"INSERT INTO insolvia.access_log (event_id, subject_kind, subject_id, principal, action, outcome, recorded_at) "
                + $"VALUES ('event-{role}', 'CASE', 'case-1', 'sub-{role}', 'case.read', 'allowed', now())");
        }

        Assert.Equal((long)writers.Length, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM insolvia.access_log"));
        foreach (var role in ServiceRoles.All)
        {
            foreach (var rewrite in new[]
            {
                "UPDATE insolvia.access_log SET outcome = 'denied'",
                "DELETE FROM insolvia.access_log",
                "TRUNCATE insolvia.access_log",
            })
            {
                var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(As(connectionString, role), rewrite));
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
            }
        }

        Assert.Equal((long)writers.Length, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM insolvia.access_log WHERE outcome = 'allowed'"));
    }

    [Fact]
    public async Task Every_table_the_migrations_create_is_named_by_the_grant_table()
    {
        // A table no grant names is reachable by no service: a store that
        // forgot its row would fail on its first deployed request.
        var connectionString = await postgres.CreateDatabaseAsync();
        var report = await RunAsync(connectionString, GrantTable.All);

        Assert.Equal(report.Tables.Order(StringComparer.Ordinal), GrantTable.All.Select(grant => grant.Table).Distinct().Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_firm_tables_are_granted_by_service_least_privilege_first()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await RunAsync(connectionString, GrantTable.All);

        // The API never creates or deletes a firm; the admin service provisions them.
        Assert.False(await HasPrivilegeAsync(connectionString, ServiceRoles.Api, "firms", "INSERT"));
        Assert.True(await HasPrivilegeAsync(connectionString, ServiceRoles.Api, "firms", "UPDATE"));
        Assert.True(await HasPrivilegeAsync(connectionString, ServiceRoles.Admin, "firms", "INSERT"));
        Assert.True(await HasPrivilegeAsync(connectionString, ServiceRoles.Admin, "firm_users", "INSERT"));

        // The MCP server reads the firm side and writes none of it.
        foreach (var table in new[] { "firms", "firm_users", "firm_clients" })
        {
            Assert.True(await HasPrivilegeAsync(connectionString, ServiceRoles.Mcp, table, "SELECT"));
            Assert.False(await HasPrivilegeAsync(connectionString, ServiceRoles.Mcp, table, "UPDATE"));
        }

        // No delete where no port deletes; the filing worker holds nothing here.
        Assert.False(await HasPrivilegeAsync(connectionString, ServiceRoles.Api, "firm_clients", "DELETE"));
        Assert.False(await HasPrivilegeAsync(connectionString, ServiceRoles.Api, "client_bindings", "DELETE"));
        Assert.True(await HasPrivilegeAsync(connectionString, ServiceRoles.Api, "case_client_role_claims", "DELETE"));
        Assert.False(await HasPrivilegeAsync(connectionString, ServiceRoles.Filing, "firm_users", "SELECT"));
    }

    [Fact]
    public async Task The_waitlist_takes_the_apis_inserts_and_gives_nothing_back()
    {
        // POST /v1/waitlist is public: the role behind it may add a row and
        // may not read the list, change a row, or remove one.
        var connectionString = await postgres.CreateDatabaseAsync();
        await RunAsync(connectionString, GrantTable.All);

        await ExecuteAsync(
            As(connectionString, ServiceRoles.Api),
            "INSERT INTO insolvia.waitlist_entries (id, submitted_at, status, name, firm, email) "
            + "VALUES ('entry-1', now(), 'new', 'Ada Example', 'Example LLP', 'ada@example.test')");
        Assert.Equal(1L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM insolvia.waitlist_entries"));

        foreach (var privilege in new[] { "SELECT", "UPDATE", "DELETE", "TRUNCATE" })
        {
            Assert.False(await HasPrivilegeAsync(connectionString, ServiceRoles.Api, "waitlist_entries", privilege));
        }

        foreach (var role in ServiceRoles.All.Where(role => role != ServiceRoles.Api))
        {
            Assert.False(await HasPrivilegeAsync(connectionString, role, "waitlist_entries", "INSERT"));
        }
    }

    [Fact]
    public async Task The_case_tables_are_granted_by_service_least_privilege_first()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await RunAsync(connectionString, GrantTable.All);

        // A case is soft-deleted: nobody deletes its row.
        foreach (var role in ServiceRoles.All)
        {
            Assert.False(await HasPrivilegeAsync(connectionString, role, "cases", "DELETE"));
        }

        // The lifecycle history is append-only for its writers, and nobody
        // rewrites or removes an event (ADR 0027).
        foreach (var role in new[] { ServiceRoles.Api, ServiceRoles.Filing })
        {
            Assert.True(await HasPrivilegeAsync(connectionString, role, "case_lifecycle_events", "INSERT"));
        }

        foreach (var role in ServiceRoles.All)
        {
            Assert.False(await HasPrivilegeAsync(connectionString, role, "case_lifecycle_events", "UPDATE"));
            Assert.False(await HasPrivilegeAsync(connectionString, role, "case_lifecycle_events", "DELETE"));
        }

        // The MCP server's only case-side write is a candidate (ADR 0016).
        foreach (var table in new[] { "cases", "case_assignments", "case_lifecycle_events", "debtors", "case_entities", "documents" })
        {
            Assert.True(await HasPrivilegeAsync(connectionString, ServiceRoles.Mcp, table, "SELECT"));
            Assert.False(await HasPrivilegeAsync(connectionString, ServiceRoles.Mcp, table, "INSERT"));
            Assert.False(await HasPrivilegeAsync(connectionString, ServiceRoles.Mcp, table, "UPDATE"));
        }

        Assert.True(await HasPrivilegeAsync(connectionString, ServiceRoles.Mcp, "candidates", "UPDATE"));

        // The filing worker files a case and writes its record; it never
        // creates a case, and the admin service touches no case table.
        Assert.True(await HasPrivilegeAsync(connectionString, ServiceRoles.Filing, "cases", "UPDATE"));
        Assert.False(await HasPrivilegeAsync(connectionString, ServiceRoles.Filing, "cases", "INSERT"));
        Assert.True(await HasPrivilegeAsync(connectionString, ServiceRoles.Filing, "filings", "INSERT"));
        Assert.False(await HasPrivilegeAsync(connectionString, ServiceRoles.Api, "filings", "INSERT"));
        Assert.False(await HasPrivilegeAsync(connectionString, ServiceRoles.Admin, "cases", "SELECT"));
        Assert.False(await HasPrivilegeAsync(connectionString, ServiceRoles.Admin, "tax_ids", "SELECT"));
        Assert.False(await HasPrivilegeAsync(connectionString, ServiceRoles.Mcp, "tax_ids", "SELECT"));
    }

    [Fact]
    public async Task The_api_reads_a_credentials_metadata_and_no_envelope_column()
    {
        // ADR 0028 § Data, the second layer of the vault: whatever the API's
        // code does, its role cannot read a ciphertext, a nonce or a wrapped key.
        var connectionString = await postgres.CreateDatabaseAsync();
        await RunAsync(connectionString, GrantTable.All);
        await SeedVaultAsync(connectionString);
        var api = As(connectionString, ServiceRoles.Api);

        foreach (var column in FilingCredentialColumns.Metadata)
        {
            Assert.True(await HasColumnPrivilegeAsync(connectionString, ServiceRoles.Api, column, "SELECT"), column);
        }

        foreach (var column in FilingCredentialColumns.Envelope)
        {
            Assert.False(await HasColumnPrivilegeAsync(connectionString, ServiceRoles.Api, column, "SELECT"), column);
            var error = await Assert.ThrowsAsync<PostgresException>(() =>
                ScalarAsync<long>(api, $"SELECT count({column}) FROM insolvia.filing_credentials"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }

        var everything = await Assert.ThrowsAsync<PostgresException>(() => ScalarAsync<long>(api, "SELECT count(*) FROM (SELECT * FROM insolvia.filing_credentials) AS t"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, everything.SqlState);
        Assert.Equal(1L, await ScalarAsync<long>(api, "SELECT count(login) FROM insolvia.filing_credentials"));

        // A rotation is revoke-then-enrol: the API can never rewrite a sealed secret.
        var rewrite = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(api, "UPDATE insolvia.filing_credentials SET login = 'someone-else'"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, rewrite.SqlState);
        Assert.False(await HasPrivilegeAsync(connectionString, ServiceRoles.Api, "filing_credentials", "TRUNCATE"));
    }

    [Fact]
    public async Task The_api_store_enrols_lists_and_revokes_and_cannot_open_the_vault()
    {
        // The real store, connected AS the API: the three calls the API makes
        // work under the column grant; the read that returns an envelope is
        // refused by the database itself.
        var connectionString = await postgres.CreateDatabaseAsync();
        await RunAsync(connectionString, GrantTable.All);
        await SeedVaultAsync(connectionString);
        await using var api = OwnedContext.Open(As(connectionString, ServiceRoles.Api));
        var store = new PostgresFilingCredentialStore(api.Context);

        var second = Credential("credential-2") with { Login = "second-login", CreatedAt = VaultAt.AddMinutes(1) };
        await store.CreateAsync(second, None);
        var listed = await store.ListForAttorneyAsync(VaultFirm, VaultAttorney, None);
        Assert.Equal(["credential-1", "credential-2"], listed.Select(credential => credential.CredentialId));
        Assert.All(listed, credential => Assert.Equal(PostgresFilingCredentialStore.Withheld("kms-aes256-gcm/1"), credential.Envelope));

        var open = await Assert.ThrowsAsync<PostgresException>(() => store.GetAsync(VaultFirm, VaultAttorney, "credential-1", None));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, open.SqlState);

        Assert.True(await store.DeleteAsync(VaultFirm, VaultAttorney, "credential-2", None));
        Assert.False(await store.DeleteAsync(VaultFirm, VaultAttorney, "credential-2", None));
    }

    [Fact]
    public async Task The_filing_worker_reads_the_vault_and_cannot_write_a_row_of_it()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await RunAsync(connectionString, GrantTable.All);
        await SeedVaultAsync(connectionString);
        await using (var filing = OwnedContext.Open(As(connectionString, ServiceRoles.Filing)))
        {
            var opened = await new PostgresFilingCredentialStore(filing.Context).GetAsync(VaultFirm, VaultAttorney, "credential-1", None);
            Assert.Equal(Credential("credential-1"), opened);
            Assert.NotNull(await new PostgresFilingAuthorizationStore(filing.Context).GetCurrentAsync(VaultFirm, VaultAttorney, None));
        }

        var worker = As(connectionString, ServiceRoles.Filing);
        foreach (var write in new[]
        {
            "INSERT INTO insolvia.filing_credentials (firm_id, attorney_id, credential_id, login, courts, status, scheme, ciphertext, nonce, wrapped_key, created_at, updated_at) "
                + $"VALUES ('{VaultFirm}', '{VaultAttorney}', 'credential-9', 'x', '{{}}', 'active', 's', '\\x00', '\\x00', '\\x00', now(), now())",
            "UPDATE insolvia.filing_credentials SET login = 'someone-else'",
            "DELETE FROM insolvia.filing_credentials",
            "TRUNCATE insolvia.filing_credentials",
            "DELETE FROM insolvia.current_filing_authorizations",
            "UPDATE insolvia.filing_authorizations SET status = 'withdrawn', withdrawn_at = now()",
        })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(worker, write));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }

        // Nobody else holds anything on the vault.
        foreach (var role in new[] { ServiceRoles.Worker, ServiceRoles.Admin, ServiceRoles.Mcp, ServiceRoles.Migrator })
        {
            foreach (var table in new[] { "filing_credentials", "filing_authorizations", "current_filing_authorizations" })
            {
                foreach (var privilege in new[] { "SELECT", "INSERT", "UPDATE", "DELETE" })
                {
                    Assert.False(await HasPrivilegeAsync(connectionString, role, table, privilege), $"{role} {privilege} {table}");
                }
            }
        }

        // The signature history is never deleted, by anyone.
        foreach (var role in ServiceRoles.All)
        {
            Assert.False(await HasPrivilegeAsync(connectionString, role, "filing_authorizations", "DELETE"));
        }
    }

    [Fact]
    public async Task The_admin_audit_takes_the_admin_services_inserts_and_nobody_reads_rewrites_or_erases_a_row()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await RunAsync(connectionString, GrantTable.All);
        await SeedVaultAsync(connectionString);
        await using (var admin = OwnedContext.Open(As(connectionString, ServiceRoles.Admin)))
        {
            await new PostgresAdminAuditLog(admin.Context).RecordAsync(
                AdminEvents.Record(VaultFirm, AdminAction.FirmProvision, "google-sub-1", "staff@example.test", "Vault & Partners"),
                None);
        }

        Assert.Equal(1L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM insolvia.admin_audit"));
        foreach (var role in ServiceRoles.All)
        {
            foreach (var rewrite in new[]
            {
                "SELECT count(*) FROM insolvia.admin_audit",
                "UPDATE insolvia.admin_audit SET detail = 'nothing happened'",
                "DELETE FROM insolvia.admin_audit",
                "TRUNCATE insolvia.admin_audit",
            })
            {
                var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(As(connectionString, role), rewrite));
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
            }

            Assert.Equal(role == ServiceRoles.Admin, await HasPrivilegeAsync(connectionString, role, "admin_audit", "INSERT"));
        }

        Assert.Equal(1L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM insolvia.admin_audit WHERE detail = 'Vault & Partners'"));
    }

    [Fact]
    public async Task A_column_grant_is_applied_narrowed_and_revoked_with_the_table()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await RunAsync(connectionString, []);
        await ExecuteAsync(connectionString, "CREATE TABLE insolvia.split_demo (id int PRIMARY KEY, public_part text, secret_part text)");
        await ExecuteAsync(connectionString, "INSERT INTO insolvia.split_demo VALUES (1, 'shown', 'hidden')");
        var api = As(connectionString, ServiceRoles.Api);

        await RunAsync(connectionString, [new TableGrant("split_demo", ServiceRoles.Api, TablePrivileges.None) { SelectColumns = ["id", "public_part", "secret_part"] }]);
        Assert.Equal("hidden", await ScalarAsync<string>(api, "SELECT secret_part FROM insolvia.split_demo"));

        await RunAsync(connectionString, [new TableGrant("split_demo", ServiceRoles.Api, TablePrivileges.None) { SelectColumns = ["id", "public_part"] }]);
        Assert.Equal("shown", await ScalarAsync<string>(api, "SELECT public_part FROM insolvia.split_demo"));
        var narrowed = await Assert.ThrowsAsync<PostgresException>(() => ScalarAsync<string>(api, "SELECT secret_part FROM insolvia.split_demo"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, narrowed.SqlState);

        await RunAsync(connectionString, []);
        var revoked = await Assert.ThrowsAsync<PostgresException>(() => ScalarAsync<string>(api, "SELECT public_part FROM insolvia.split_demo"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, revoked.SqlState);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            RunAsync(connectionString, [new TableGrant("split_demo", ServiceRoles.Api, TablePrivileges.Select) { SelectColumns = ["id"] }]));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            RunAsync(connectionString, [new TableGrant("split_demo", ServiceRoles.Api, TablePrivileges.None) { SelectColumns = ["id); DROP TABLE x; --"] }]));
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

    private const string VaultFirm = "00000000-0000-4000-8000-00000000f18a";
    private const string VaultAttorney = "00000000-0000-4000-8000-00000000a11c";
    private const string VaultAuthorization = "00000000-0000-4000-8000-0000000a0701";

    private static readonly string[] Courts = ["flmb"];

    private static readonly DateTimeOffset VaultAt = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A firm, a signature and one sealed credential — synthetic bytes, never a real secret.</summary>
    private static async Task SeedVaultAsync(string connectionString)
    {
        await using var owner = OwnedContext.Open(connectionString);
        await new PostgresFirmStore(owner.Context).CreateFirmAsync(new Firm(VaultFirm, "Vault & Partners", FirmStatus.Active, VaultAt, VaultAt), None);
        await new PostgresFilingAuthorizationStore(owner.Context).PutCurrentAsync(
            new FilingAuthorization(VaultAuthorization, VaultFirm, VaultAttorney, "2026-10-07-draft", new string('0', 64), VaultAt, 1_793_000_000),
            replacing: null,
            None);
        await new PostgresFilingCredentialStore(owner.Context).CreateAsync(Credential("credential-1"), None);
    }

    private static FilingCredential Credential(string credentialId) => new(
        credentialId,
        VaultFirm,
        VaultAttorney,
        "example-login",
        Courts.ToValueCollection(),
        CredentialStatus.Active,
        new Envelope("kms-aes256-gcm/1", "c3ludGhldGljLWNpcGhlcnRleHQ=", "AAAAAAAAAAAAAAAA", "c3ludGhldGljLXdyYXBwZWQta2V5"),
        VaultAt,
        VaultAt,
        VaultAuthorization);

    private static Task<bool> HasColumnPrivilegeAsync(string connectionString, string role, string column, string privilege) =>
        ScalarAsync<bool>(connectionString, $"SELECT has_column_privilege('{role}', 'insolvia.filing_credentials', '{column}', '{privilege}')");

    /// <summary>A context over its own data source, as one service's connection.</summary>
    private sealed class OwnedContext(NpgsqlDataSource dataSource) : IAsyncDisposable
    {
        public InsolviaDbContext Context { get; } = new(Options(dataSource));

        public static OwnedContext Open(string connectionString) =>
            new(InsolviaDataSource.Create(new DatabaseSettings { ConnectionString = connectionString }));

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await dataSource.DisposeAsync();
        }

        private static DbContextOptions<InsolviaDbContext> Options(NpgsqlDataSource source)
        {
            var options = new DbContextOptionsBuilder<InsolviaDbContext>();
            options.UseInsolviaNpgsql(source);
            return options.Options;
        }
    }

    private static Task<bool> HasPrivilegeAsync(string connectionString, string role, string table, string privilege) =>
        ScalarAsync<bool>(connectionString, $"SELECT has_table_privilege('{role}', 'insolvia.{table}', '{privilege}')");

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
