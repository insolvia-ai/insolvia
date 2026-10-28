using System.Text.RegularExpressions;
using Insolvia.Data;
using Npgsql;

namespace Insolvia.Migrator;

/// <summary>How the service roles log in.</summary>
public enum RoleLogin
{
    /// <summary>IAM tokens: the role is granted <c>rds_iam</c> and has no password (Aurora).</summary>
    Iam,

    /// <summary>A password (local Postgres, tests).</summary>
    Password,
}

/// <summary>What one provisioning run did.</summary>
/// <param name="Login">How the roles log in.</param>
/// <param name="Roles">The roles that now exist.</param>
/// <param name="Tables">The tables whose grants were (re)applied.</param>
public sealed record ProvisioningResult(RoleLogin Login, IReadOnlyList<string> Roles, IReadOnlyList<string> Tables);

/// <summary>
/// Makes the database's roles and table privileges match
/// <see cref="ServiceRoles"/> and a grant table. Idempotent: a second run
/// with the same inputs changes nothing, and a run with a grant removed
/// revokes it.
/// </summary>
public static partial class RoleProvisioner
{
    // Serialises concurrent provisioning runs (two migrator invocations racing
    // a CREATE ROLE). The value is arbitrary and only has to be stable.
    private const long AdvisoryLock = 0x1E50_1A00;

    /// <summary>
    /// Creates or updates every service role, grants schema usage, and
    /// applies <paramref name="grants"/> — all in one transaction, so no
    /// table is ever visible with its grants half-applied.
    /// </summary>
    /// <param name="connection">An open connection as a user that may create roles (the RDS master, or the local superuser).</param>
    /// <param name="grants">Usually <see cref="GrantTable.All"/>.</param>
    /// <param name="localPassword">The password every role gets when the
    /// database has no <c>rds_iam</c> role (local). Ignored on Aurora.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <remarks>
    /// Aurora is recognised by its <c>rds_iam</c> role rather than by
    /// configuration: the one fact that decides whether IAM login is possible
    /// is the one checked.
    /// </remarks>
    public static async Task<ProvisioningResult> ApplyAsync(
        NpgsqlConnection connection,
        IReadOnlyList<TableGrant> grants,
        string? localPassword,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(grants);
        foreach (var grant in grants)
        {
            Validate(grant);
        }

        var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            await ExecuteAsync(connection, $"SELECT pg_advisory_xact_lock({AdvisoryLock})", cancellationToken).ConfigureAwait(false);

            var login = await ScalarAsync(connection, "SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'rds_iam')", cancellationToken).ConfigureAwait(false)
                ? RoleLogin.Iam
                : RoleLogin.Password;
            if (login == RoleLogin.Password && string.IsNullOrEmpty(localPassword))
            {
                throw new InvalidOperationException(
                    "This database has no rds_iam role, so the service roles log in with a password, and none was given (INSOLVIA_DB_ROLE_PASSWORD).");
            }

            foreach (var role in ServiceRoles.All)
            {
                await EnsureRoleAsync(connection, role, login, localPassword, cancellationToken).ConfigureAwait(false);
            }

            // The seeder takes the migrator role on THIS login (SET ROLE), which
            // needs membership unless the login is a superuser (local, tests).
            // Aurora's master is not one; as the role's creator it may grant
            // itself membership — SET only, never INHERIT, so the master's own
            // sessions gain nothing from the migrator's grants.
            if (!await ScalarAsync(connection, "SELECT rolsuper FROM pg_roles WHERE rolname = current_user", cancellationToken).ConfigureAwait(false)
                && !await ScalarAsync(connection, "SELECT pg_has_role(current_user, @name, 'SET')", cancellationToken, ServiceRoles.Migrator).ConfigureAwait(false))
            {
                await ExecuteAsync(connection, $"GRANT {Quote(ServiceRoles.Migrator)} TO CURRENT_USER WITH INHERIT FALSE, SET TRUE", cancellationToken).ConfigureAwait(false);
            }

            var everyRole = string.Join(", ", ServiceRoles.All.Select(Quote));
            var schema = Quote(InsolviaDbContext.Schema);
            await ExecuteAsync(connection, $"GRANT USAGE ON SCHEMA {schema} TO {everyRole}", cancellationToken).ConfigureAwait(false);

            var tables = await TablesAsync(connection, cancellationToken).ConfigureAwait(false);
            var unknown = grants.Select(grant => grant.Table).Distinct(StringComparer.Ordinal).Except(tables, StringComparer.Ordinal).ToList();
            if (unknown.Count > 0)
            {
                throw new InvalidOperationException(
                    $"The grant table names tables that do not exist in schema {InsolviaDbContext.Schema}: {string.Join(", ", unknown)}.");
            }

            foreach (var table in tables)
            {
                var qualified = $"{schema}.{Quote(table)}";
                await ExecuteAsync(connection, $"REVOKE ALL ON TABLE {qualified} FROM {everyRole}", cancellationToken).ConfigureAwait(false);
                foreach (var grant in grants.Where(grant => grant.Table == table))
                {
                    if (grant.Privileges != TablePrivileges.None)
                    {
                        await ExecuteAsync(
                            connection,
                            $"GRANT {PrivilegeList(grant.Privileges)} ON TABLE {qualified} TO {Quote(grant.Role)}",
                            cancellationToken).ConfigureAwait(false);
                    }

                    // REVOKE ALL ON TABLE above also revoked every column
                    // privilege, so a column dropped from the list is revoked.
                    if (grant.SelectColumns.Count > 0)
                    {
                        await ExecuteAsync(
                            connection,
                            $"GRANT SELECT ({string.Join(", ", grant.SelectColumns.Select(Quote))}) ON TABLE {qualified} TO {Quote(grant.Role)}",
                            cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ProvisioningResult(login, ServiceRoles.All, tables);
        }
    }

    private static async Task EnsureRoleAsync(
        NpgsqlConnection connection,
        string role,
        RoleLogin login,
        string? localPassword,
        CancellationToken cancellationToken)
    {
        var exists = await ScalarAsync(
            connection,
            "SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = @name)",
            cancellationToken,
            role).ConfigureAwait(false);
        if (!exists)
        {
            // CREATE ROLE's defaults are the ones wanted: not a superuser, no
            // CREATEDB, no CREATEROLE. They are not restated, because naming
            // SUPERUSER/REPLICATION/BYPASSRLS at all needs a true superuser,
            // which Aurora's master user is not.
            await ExecuteAsync(connection, $"CREATE ROLE {Quote(role)} LOGIN", cancellationToken).ConfigureAwait(false);
        }

        if (login == RoleLogin.Iam)
        {
            await ExecuteAsync(connection, $"ALTER ROLE {Quote(role)} WITH LOGIN PASSWORD NULL", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, $"GRANT rds_iam TO {Quote(role)}", cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var literal = localPassword!.Replace("'", "''", StringComparison.Ordinal);
            await ExecuteAsync(connection, $"ALTER ROLE {Quote(role)} WITH LOGIN PASSWORD '{literal}'", cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<List<string>> TablesAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var command = new NpgsqlCommand(
            "SELECT tablename FROM pg_tables WHERE schemaname = @schema AND tablename <> @history ORDER BY tablename",
            connection);
        await using (command.ConfigureAwait(false))
        {
            command.Parameters.AddWithValue("schema", InsolviaDbContext.Schema);
            command.Parameters.AddWithValue("history", InsolviaDbContext.MigrationsHistoryTable);
            var tables = new List<string>();
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    tables.Add(reader.GetString(0));
                }
            }

            return tables;
        }
    }

    private static string PrivilegeList(TablePrivileges privileges)
    {
        var names = new List<string>();
        if (privileges.HasFlag(TablePrivileges.Select))
        {
            names.Add("SELECT");
        }

        if (privileges.HasFlag(TablePrivileges.Insert))
        {
            names.Add("INSERT");
        }

        if (privileges.HasFlag(TablePrivileges.Update))
        {
            names.Add("UPDATE");
        }

        if (privileges.HasFlag(TablePrivileges.Delete))
        {
            names.Add("DELETE");
        }

        if (privileges.HasFlag(TablePrivileges.Truncate))
        {
            names.Add("TRUNCATE");
        }

        return string.Join(", ", names);
    }

    private static void Validate(TableGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        if (!Identifier().IsMatch(grant.Table))
        {
            throw new ArgumentException($"'{grant.Table}' is not a snake_case table name.", nameof(grant));
        }

        if (!ServiceRoles.All.Contains(grant.Role, StringComparer.Ordinal))
        {
            throw new ArgumentException($"'{grant.Role}' is not a service role ({string.Join(", ", ServiceRoles.All)}).", nameof(grant));
        }

        ArgumentNullException.ThrowIfNull(grant.SelectColumns);
        if (grant.SelectColumns.Count > 0 && grant.Privileges.HasFlag(TablePrivileges.Select))
        {
            throw new ArgumentException($"'{grant.Table}' grants {grant.Role} both SELECT and a column list; a column list only narrows a role without SELECT.", nameof(grant));
        }

        foreach (var column in grant.SelectColumns)
        {
            if (!Identifier().IsMatch(column))
            {
                throw new ArgumentException($"'{column}' is not a snake_case column name.", nameof(grant));
            }
        }
    }

    // Identifiers are interpolated into DDL (Postgres cannot parameterise
    // them), so only plain snake_case names are accepted, then quoted.
    private static string Quote(string identifier) =>
        Identifier().IsMatch(identifier)
            ? $"\"{identifier}\""
            : throw new ArgumentException($"'{identifier}' is not a plain identifier.", nameof(identifier));

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
#pragma warning disable CA2100 // The SQL is built from validated identifiers and constants only; see Quote.
        var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        await using (command.ConfigureAwait(false))
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<bool> ScalarAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken, string? name = null)
    {
#pragma warning disable CA2100 // As ExecuteAsync: constants only; a value travels as @name.
        var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        await using (command.ConfigureAwait(false))
        {
            if (name is not null)
            {
                command.Parameters.AddWithValue("name", name);
            }

            return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]*$")]
    private static partial Regex Identifier();
}
