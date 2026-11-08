using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Insolvia.Data.Concurrency;

/// <summary>
/// One database transaction around a unit of work: what DynamoDB's
/// <c>TransactWriteItems</c> was (create a case with its history, bind a
/// client, merge clients, swap the approval pointer). The work commits
/// together or not at all; an exception rolls it back and propagates.
/// </summary>
public static class Transactions
{
    /// <summary>
    /// Runs <paramref name="work"/> in a transaction and commits it. Called
    /// inside a transaction already open on this context, it joins that one
    /// instead (the outer caller commits).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The transaction runs through the context's execution strategy, so the
    /// retrying strategy (<see cref="InsolviaNpgsql.UseInsolviaNpgsql"/>)
    /// replays the whole unit, never half of it. <paramref name="work"/> must
    /// therefore be safe to run again from the start: do the reads inside it,
    /// not before, and leave nothing tracked (the stores read untracked and
    /// <c>InsertOnceAsync</c> detaches what it added).
    /// </para>
    /// <para>
    /// A commit can succeed and its acknowledgement be lost; the replay then
    /// runs against the unit's own result, and a conditional write in it
    /// refuses. Pass <paramref name="landed"/> when that refusal would reach
    /// the caller: after a transient failure EF asks it whether the unit had
    /// in fact committed, and its result is the answer (see
    /// <see cref="RetrySafety"/> for what such a check must recognise).
    /// </para>
    /// </remarks>
    /// <param name="context">The context to run on.</param>
    /// <param name="work">The unit of work; may run more than once.</param>
    /// <param name="cancellationToken">Cancels the unit.</param>
    /// <param name="isolation">The transaction's isolation level.</param>
    /// <param name="landed">Whether a failed attempt had committed, and if so
    /// what <paramref name="work"/> returned. <see langword="null"/>: a
    /// replay's own answer stands.</param>
    public static async Task<T> InTransactionAsync<T>(
        this DbContext context,
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken,
        IsolationLevel isolation = IsolationLevel.ReadCommitted,
        Func<CancellationToken, Task<ExecutionResult<T>>>? landed = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(work);

        if (context.Database.CurrentTransaction is not null)
        {
            return await work(cancellationToken).ConfigureAwait(false);
        }

        var strategy = context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(
            (context, work, isolation, landed),
            static async (_, state, token) =>
            {
                var transaction = await state.context.Database.BeginTransactionAsync(state.isolation, token).ConfigureAwait(false);
                await using (transaction.ConfigureAwait(false))
                {
                    var result = await state.work(token).ConfigureAwait(false);
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                    return result;
                }
            },
            landed is null ? null : static (_, state, token) => state.landed!(token),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <paramref name="work"/> in a transaction and commits it; the
    /// unit-returning form of <see cref="InTransactionAsync{T}"/>, whose
    /// remarks apply.
    /// </summary>
    /// <param name="context">The context to run on.</param>
    /// <param name="work">The unit of work; may run more than once.</param>
    /// <param name="cancellationToken">Cancels the unit.</param>
    /// <param name="isolation">The transaction's isolation level.</param>
    /// <param name="landed">Whether a failed attempt had in fact committed.</param>
    public static Task InTransactionAsync(
        this DbContext context,
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken,
        IsolationLevel isolation = IsolationLevel.ReadCommitted,
        Func<CancellationToken, Task<bool>>? landed = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        return context.InTransactionAsync(
            async token =>
            {
                await work(token).ConfigureAwait(false);
                return true;
            },
            cancellationToken,
            isolation,
            landed is null ? null : async token => new ExecutionResult<bool>(await landed(token).ConfigureAwait(false), true));
    }
}
