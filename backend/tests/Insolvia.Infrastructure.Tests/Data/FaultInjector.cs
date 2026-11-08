using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Insolvia.Data.Tests;

/// <summary>What <see cref="FaultInjector"/> does to the next matching operation.</summary>
internal enum Fault
{
    /// <summary>The connection does not open — a paused Aurora dropping the connect.</summary>
    RefuseConnection,

    /// <summary>The command fails before it reaches the server.</summary>
    FailCommand,

    /// <summary>The command runs (and, outside a transaction, commits); then its reply is lost.</summary>
    LoseCommandAck,

    /// <summary>The transaction commits; then the commit's reply is lost.</summary>
    LoseCommitAck,
}

/// <summary>
/// An EF interceptor that injects one transient Npgsql failure per planned
/// fault, in order, then lets everything through. The failure is the one a
/// dropped socket produces (<see cref="NpgsqlException"/> wrapping an
/// <see cref="IOException"/>), which Npgsql reports as transient and EF's
/// retrying strategy therefore replays.
/// </summary>
internal sealed class FaultInjector : DbCommandInterceptor, IDbConnectionInterceptor, IDbTransactionInterceptor
{
    private readonly Lock _gate = new();
    private readonly Queue<(Fault Kind, string? SqlContains)> _planned = new();
    private readonly List<string> _injected = [];

    /// <summary>What was injected, in order: the fault and, for a lost reply, whether it had committed.</summary>
    public IReadOnlyList<string> Injected
    {
        get
        {
            lock (_gate)
            {
                return [.. _injected];
            }
        }
    }

    /// <summary>Plans <paramref name="fault"/> for the next operation it applies to (whose SQL contains <paramref name="sqlContains"/>, for a command).</summary>
    public FaultInjector Plan(Fault fault, string? sqlContains = null)
    {
        lock (_gate)
        {
            _planned.Enqueue((fault, sqlContains));
        }

        return this;
    }

    public ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        Inject(Fault.RefuseConnection, sql: null, "connection refused");
        return ValueTask.FromResult(result);
    }

    public InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        Inject(Fault.RefuseConnection, sql: null, "connection refused");
        return result;
    }

    public Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Inject(Fault.LoseCommitAck, sql: null, "commit acknowledgement lost (committed)");
        return Task.CompletedTask;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Inject(Fault.FailCommand, command.CommandText, "command failed before reaching the server");
        return ValueTask.FromResult(result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Inject(Fault.FailCommand, command.CommandText, "command failed before reaching the server");
        return ValueTask.FromResult(result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        Inject(Fault.FailCommand, command.CommandText, "command failed before reaching the server");
        return ValueTask.FromResult(result);
    }

    public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        if (Take(Fault.LoseCommandAck, command.CommandText))
        {
            // Drain and close the reader first, so the statement has run to
            // completion on the server — and committed, outside a transaction.
            while (await result.NextResultAsync(cancellationToken).ConfigureAwait(false))
            {
            }

            await result.DisposeAsync().ConfigureAwait(false);
            throw Record(Fault.LoseCommandAck, command.Transaction is null ? "reply lost (committed)" : "reply lost (in a transaction)");
        }

        return result;
    }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (Take(Fault.LoseCommandAck, command.CommandText))
        {
            throw Record(Fault.LoseCommandAck, command.Transaction is null ? "reply lost (committed)" : "reply lost (in a transaction)");
        }

        return ValueTask.FromResult(result);
    }

    private void Inject(Fault fault, string? sql, string what)
    {
        if (Take(fault, sql))
        {
            throw Record(fault, what);
        }
    }

    private bool Take(Fault fault, string? sql)
    {
        lock (_gate)
        {
            if (_planned.Count == 0)
            {
                return false;
            }

            var (kind, sqlContains) = _planned.Peek();
            if (kind != fault || (sqlContains is not null && (sql is null || !sql.Contains(sqlContains, StringComparison.Ordinal))))
            {
                return false;
            }

            _planned.Dequeue();
            return true;
        }
    }

    private NpgsqlException Record(Fault fault, string what)
    {
        lock (_gate)
        {
            _injected.Add($"{fault}: {what}");
        }

        return new NpgsqlException($"injected: {what}", new IOException("the connection was reset"));
    }
}
