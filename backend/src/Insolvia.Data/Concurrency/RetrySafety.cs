using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Insolvia.Data.Concurrency;

/// <summary>
/// Writes that stay correct under the retrying execution strategy
/// (<see cref="InsolviaNpgsql.UseInsolviaNpgsql"/>): a transient failure
/// replays the write, and a replay must neither apply it twice nor answer the
/// caller with a conflict its own first attempt caused.
/// </summary>
/// <remarks>
/// <para>
/// <b>The lost acknowledgement.</b> A statement can commit on the server and
/// its reply be lost with the connection. The strategy sees a transient
/// error and runs the write again — against a row that already holds it. A
/// conditional write then matches nothing (a spurious "refused"), an insert
/// hits its own primary key (a spurious "already exists"). Each helper here
/// takes a <c>landed</c> check: after a transient failure, and before any
/// replay, EF asks it whether the first attempt's write is already in the
/// database (EF Core's <c>verifySucceeded</c>), and when it is, the call
/// answers as if the attempt had succeeded.
/// </para>
/// <para>
/// A <c>landed</c> check must recognise <em>this call's</em> write, not just
/// "a row like it": compare on something only this call wrote (the row's
/// generated id, the attempt id, the <c>updated_at</c> it set). It also runs
/// when the first attempt never reached the server, so it must answer
/// <see langword="false"/> then.
/// </para>
/// <para>
/// Inside a transaction (an <see cref="Transactions.InTransactionAsync{T}"/>
/// delegate) every helper runs the write once and ignores <c>landed</c>: the
/// transaction is the unit EF replays, and its own check covers it.
/// </para>
/// </remarks>
public static class RetrySafety
{
    /// <summary>
    /// Runs <paramref name="write"/> through the context's execution strategy;
    /// after a transient failure, <paramref name="landed"/> decides whether the
    /// failed attempt had in fact committed, in which case
    /// <paramref name="whenLanded"/> is the answer and nothing is replayed.
    /// </summary>
    public static Task<T> WriteOnceAsync<T>(
        this DbContext context,
        Func<CancellationToken, Task<T>> write,
        Func<CancellationToken, Task<bool>> landed,
        T whenLanded,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(landed);

        return context.Database.CreateExecutionStrategy().ExecuteAsync(
            (write, landed, whenLanded),
            static (_, state, token) => state.write(token),
            static async (_, state, token) => await state.landed(token).ConfigureAwait(false)
                ? new ExecutionResult<T>(true, state.whenLanded)
                : new ExecutionResult<T>(false, default!),
            cancellationToken);
    }

    /// <summary>
    /// <c>DELETE … WHERE</c> over <paramref name="rows"/>, true when it removed
    /// a row. A replay after a lost acknowledgement finds nothing to delete;
    /// rather than answer "there was none" for a row this call removed, a
    /// transient failure is followed by a check that the rows are gone.
    /// </summary>
    /// <remarks>The one imprecision: a row that never existed, plus a
    /// transient failure on the only attempt, answers true. Both outcomes
    /// leave the row absent, which is what the caller asked for.</remarks>
    public static Task<bool> DeleteOnceAsync<TRow>(this DbContext context, IQueryable<TRow> rows, CancellationToken cancellationToken)
        where TRow : class
    {
        ArgumentNullException.ThrowIfNull(rows);
        return context.WriteOnceAsync(
            async token => await rows.ExecuteDeleteAsync(token).ConfigureAwait(false) > 0,
            async token => !await rows.AnyAsync(token).ConfigureAwait(false),
            whenLanded: true,
            cancellationToken);
    }

    /// <summary>
    /// A raw SQL write that is idempotent by construction (an upsert of fixed
    /// values), run through the execution strategy so a transient failure is
    /// retried. EF does not retry <c>ExecuteSql…</c> on its own — it cannot
    /// know the SQL is safe to repeat — so a bare one fails on a paused
    /// database.
    /// </summary>
    /// <returns>The number of rows affected by the attempt that succeeded.</returns>
    public static Task<int> ExecuteIdempotentSqlAsync(this DbContext context, FormattableString sql, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(sql);
        return context.Database.CreateExecutionStrategy().ExecuteAsync(
            (context, sql),
            static (_, state, token) => state.context.Database.ExecuteSqlInterpolatedAsync(state.sql, token),
            verifySucceeded: null,
            cancellationToken);
    }
}
