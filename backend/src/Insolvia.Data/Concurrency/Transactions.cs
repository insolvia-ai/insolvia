using System.Data;
using Microsoft.EntityFrameworkCore;

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
    /// The transaction runs through the context's execution strategy, so a
    /// retrying strategy replays the whole unit, never half of it.
    /// <paramref name="work"/> must therefore be safe to run again from the
    /// start: do the reads inside it, not before.
    /// </remarks>
    public static async Task<T> InTransactionAsync<T>(
        this DbContext context,
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken,
        IsolationLevel isolation = IsolationLevel.ReadCommitted)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(work);

        if (context.Database.CurrentTransaction is not null)
        {
            return await work(cancellationToken).ConfigureAwait(false);
        }

        var strategy = context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(
            (context, work, isolation),
            async (_, state, token) =>
            {
                var transaction = await state.context.Database.BeginTransactionAsync(state.isolation, token).ConfigureAwait(false);
                await using (transaction.ConfigureAwait(false))
                {
                    var result = await state.work(token).ConfigureAwait(false);
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                    return result;
                }
            },
            verifySucceeded: null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="InTransactionAsync{T}(DbContext, Func{CancellationToken, Task{T}}, CancellationToken, IsolationLevel)"/>
    public static Task InTransactionAsync(
        this DbContext context,
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken,
        IsolationLevel isolation = IsolationLevel.ReadCommitted)
    {
        ArgumentNullException.ThrowIfNull(work);
        return context.InTransactionAsync(
            async token =>
            {
                await work(token).ConfigureAwait(false);
                return true;
            },
            cancellationToken,
            isolation);
    }
}
