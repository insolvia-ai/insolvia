using Insolvia.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Insolvia.Migrator.Purge;

/// <summary>What one purge run planned and did (Python's <c>PurgeReport</c>).</summary>
/// <param name="Planned">The pre-client cases found, sorted.</param>
/// <param name="Deleted">The ones this run deleted.</param>
/// <param name="Stopped">Planned, then saved by a client linked before the delete.</param>
/// <param name="RowsDeleted">Rows that went with the deleted cases (children, by cascade), the case rows included.</param>
/// <param name="BindingsDeleted">Portal binding rows among them.</param>
/// <param name="KeptWithClient">Cases opened for a client — never planned.</param>
public sealed record PurgeReport(
    IReadOnlyList<string> Planned,
    IReadOnlyList<string> Deleted,
    IReadOnlyList<string> Stopped,
    long RowsDeleted,
    long BindingsDeleted,
    int KeptWithClient);

/// <summary>
/// ADR 0022's one release-time deletion — services/admin's
/// <c>purge_pre_client</c>, and the one command that deletes case data on
/// purpose. Dry run unless asked to apply.
/// </summary>
/// <remarks>
/// <para><b>The rule, and what it can never do.</b> A case is pre-client ⇔ it
/// has a row in <c>cases</c> AND none of its debtors carries a
/// <c>client_id</c>. A case opened for a client has a client-linked Debtor 1
/// from its first write (one transaction), so it can never satisfy the rule.
/// Nothing else is looked at — not the fixture version, the opener or the
/// age; soft-deleted and archived cases are cases like any other.</para>
/// <para><b>The fence is the statement.</b> DynamoDB needed three fences — a
/// consistent plan, a re-read per partition, a conditional delete per debtor.
/// Here each delete is ONE statement that re-decides the rule in its own
/// <c>WHERE</c>, so a client linked between the plan and the delete stops that
/// case (it reports as stopped) and nothing half-deletes: every child of a
/// case — assignments, lifecycle history, debtors, sealed tax ids, items,
/// document rows, jobs, packets, filings, the portal binding and its claims —
/// goes with it by the schema's <c>ON DELETE CASCADE</c>, in that statement.</para>
/// <para><b>Not deleted:</b> the case's access-log rows (append-only by design,
/// and keyed by id, not by foreign key) and the documents' bytes (keyed by
/// case id, unreachable with their rows; no principal running this holds an
/// S3 grant, and orphaned synthetic fixtures are not worth one).</para>
/// <para><b>Who runs it.</b> The migrator's own connection (the RDS master;
/// the compose superuser), not an application role: it deletes from tables
/// no application role may delete from (the lifecycle history is append-only),
/// which is exactly the privilege a release-time deletion needs and nothing
/// that serves requests should hold. On a deployed environment the database is
/// private, so it runs inside the migrator Lambda
/// (<c>{"command": "purge-pre-client", "apply": true}</c>).</para>
/// </remarks>
public static class PreClientPurge
{
    private const string PreClient =
        "NOT EXISTS (SELECT 1 FROM insolvia.debtors d WHERE d.case_id = c.id AND d.client_id IS NOT NULL AND d.client_id <> '')";

    /// <summary>Plan, then — with <paramref name="apply"/> — delete each planned case under its own fence.</summary>
    /// <param name="dataSource">Connects as the migrator's (privileged) user.</param>
    /// <param name="apply">Delete; otherwise the report is the plan and nothing is written.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <param name="beforeDelete">A test seam: runs after the plan and before each delete.</param>
    /// <returns>The report.</returns>
    public static async Task<PurgeReport> RunAsync(NpgsqlDataSource dataSource, bool apply, CancellationToken cancellationToken, Func<string, Task>? beforeDelete = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var planned = await ListAsync(connection, $"SELECT c.id FROM insolvia.cases c WHERE {PreClient} ORDER BY c.id", cancellationToken).ConfigureAwait(false);
            var kept = (int)await CountAsync(connection, $"SELECT count(*) FROM insolvia.cases c WHERE NOT {PreClient}", null, cancellationToken).ConfigureAwait(false);
            if (!apply)
            {
                return new PurgeReport(planned, [], [], 0, 0, kept);
            }

            var children = CaseChildTables(dataSource);
            var deleted = new List<string>();
            var stopped = new List<string>();
            long rows = 0;
            long bindings = 0;
            foreach (var caseId in planned)
            {
                if (beforeDelete is not null)
                {
                    await beforeDelete(caseId).ConfigureAwait(false);
                }

                var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await using (transaction.ConfigureAwait(false))
                {
                    // Counted inside the transaction that deletes, so the
                    // report is what this statement actually removed.
                    long caseRows = 0;
                    long caseBindings = 0;
                    foreach (var table in children)
                    {
#pragma warning disable CA2100 // Table names come from the EF model, never from input.
                        var count = await CountAsync(connection, $"SELECT count(*) FROM insolvia.{table} WHERE case_id = @id", caseId, cancellationToken).ConfigureAwait(false);
#pragma warning restore CA2100
                        caseRows += count;
                        if (table == "client_bindings")
                        {
                            caseBindings = count;
                        }
                    }

                    var command = new NpgsqlCommand($"DELETE FROM insolvia.cases c WHERE c.id = @id AND {PreClient}", connection, transaction);
                    await using (command.ConfigureAwait(false))
                    {
                        command.Parameters.AddWithValue("id", caseId);
                        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
                        {
                            // A debtor names a client now: the case stays, and
                            // the next run's plan re-decides it.
                            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                            stopped.Add(caseId);
                            continue;
                        }
                    }

                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    rows += caseRows + 1;
                    bindings += caseBindings;
                    deleted.Add(caseId);
                }
            }

            return new PurgeReport(planned, deleted, stopped, rows, bindings, kept);
        }
    }

    /// <summary>The report as the Python printed it: case ids and counts only, never a name.</summary>
    /// <param name="report">The report.</param>
    /// <param name="environment">The environment it ran in.</param>
    /// <param name="apply">Whether it applied.</param>
    /// <param name="output">Where to write.</param>
    public static void Print(PurgeReport report, string environment, bool apply, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(output);
        output.WriteLine($"{environment}: {report.Planned.Count} pre-client case(s); {report.KeptWithClient} opened for clients, kept");
        foreach (var caseId in apply ? report.Deleted : report.Planned)
        {
            output.WriteLine($"  {(apply ? "deleted" : "would delete")} case {caseId}");
        }

        if (apply)
        {
            output.WriteLine($"  {report.RowsDeleted} row(s) and {report.BindingsDeleted} portal binding row(s) deleted");
            foreach (var caseId in report.Stopped)
            {
                output.WriteLine($"  stopped at case {caseId}: a debtor names a client now");
            }
        }
    }

    // Every table whose rows belong to a case — read from the model, so a
    // child table added later is counted without an edit here.
    private static List<string> CaseChildTables(NpgsqlDataSource dataSource)
    {
        var options = new DbContextOptionsBuilder<InsolviaDbContext>();
        options.UseInsolviaNpgsql(dataSource);
        using var context = new InsolviaDbContext(options.Options);
        var cases = context.Model.GetEntityTypes().Single(type => type.GetTableName() == "cases");
        return [.. context.Model.GetEntityTypes()
            .Where(type => type.GetForeignKeys().Any(key => key.PrincipalEntityType == cases))
            .Select(type => type.GetTableName()!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
    }

    private static async Task<List<string>> ListAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        var command = new NpgsqlCommand(sql, connection);
        await using (command.ConfigureAwait(false))
        {
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                var found = new List<string>();
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    found.Add(reader.GetString(0));
                }

                return found;
            }
        }
    }

    private static async Task<long> CountAsync(NpgsqlConnection connection, string sql, string? caseId, CancellationToken cancellationToken)
    {
        var command = new NpgsqlCommand(sql, connection);
        await using (command.ConfigureAwait(false))
        {
            if (caseId is not null)
            {
                command.Parameters.AddWithValue("id", caseId);
            }

            return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }
    }
}
