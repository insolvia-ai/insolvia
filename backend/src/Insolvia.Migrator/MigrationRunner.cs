using Insolvia.Data;
using Insolvia.Data.Connections;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Insolvia.Migrator;

/// <summary>What one migrator run did: the JSON a Lambda invocation returns.</summary>
/// <param name="AppliedMigrations">Migrations this run applied (empty when the database was current).</param>
/// <param name="Migrations">Every migration the database now has.</param>
/// <param name="RoleLogin">How the service roles log in: <c>Iam</c> or <c>Password</c>.</param>
/// <param name="Roles">The service roles that exist.</param>
/// <param name="Tables">The tables whose grants were applied.</param>
public sealed record MigrationReport(
    IReadOnlyList<string> AppliedMigrations,
    IReadOnlyList<string> Migrations,
    string RoleLogin,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Tables);

/// <summary>
/// One migrator run: apply EF migrations, then make roles and grants match.
/// Safe to run any number of times — a run against a current database
/// applies nothing and re-asserts the same grants.
/// </summary>
public static class MigrationRunner
{
    /// <summary>Migrates the database behind <paramref name="dataSource"/> and provisions its roles.</summary>
    /// <param name="dataSource">Connects as a user that may run DDL and create roles.</param>
    /// <param name="grants">Usually <see cref="GrantTable.All"/>.</param>
    /// <param name="localPassword">The service roles' password where IAM login is unavailable.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    public static async Task<MigrationReport> RunAsync(
        NpgsqlDataSource dataSource,
        IReadOnlyList<TableGrant> grants,
        string? localPassword,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        // Wake the database first: a paused Aurora drops connections until it
        // has resumed, and EF's first open would otherwise time out on it.
        var wake = await DatabaseWake.OpenAsync(dataSource, cancellationToken).ConfigureAwait(false);
        await wake.DisposeAsync().ConfigureAwait(false);

        var options = new DbContextOptionsBuilder<InsolviaDbContext>();
        options.UseInsolviaNpgsql(dataSource);
        IReadOnlyList<string> pending;
        IReadOnlyList<string> applied;
        var context = new InsolviaDbContext(options.Options);
        await using (context.ConfigureAwait(false))
        {
            pending = [.. await context.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)];

            // MigrateAsync takes EF's migration lock, so two concurrent runs
            // apply each migration once; it also refuses to run when the model
            // has changes no migration captures.
            await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            applied = [.. await context.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false)];
        }

        var connection = await DatabaseWake.OpenAsync(dataSource, cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var provisioned = await RoleProvisioner.ApplyAsync(connection, grants, localPassword, cancellationToken).ConfigureAwait(false);
            return new MigrationReport(pending, applied, provisioned.Login.ToString(), provisioned.Roles, provisioned.Tables);
        }
    }
}
