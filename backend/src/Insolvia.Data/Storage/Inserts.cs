using System.Linq.Expressions;
using Insolvia.Data.Concurrency;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Insolvia.Data.Storage;

/// <summary>The once-only insert every store's <c>Create…</c> is, and the conditions shared by the stores.</summary>
internal static class Inserts
{
    /// <summary>
    /// Inserts <paramref name="row"/>, translating a violation of
    /// <paramref name="primaryKey"/> into <paramref name="whenExists"/> —
    /// DynamoDB's <c>attribute_not_exists</c> put. The row is detached
    /// afterwards either way: the stores read untracked and write with
    /// <c>UPDATE … WHERE</c>, so nothing may linger in the change tracker.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Outside a transaction the insert is retried on a transient failure, and
    /// a commit whose acknowledgement was lost replays into its own key —
    /// "already exists" for a row this call wrote, which for an append-only
    /// log is an error a user sees for an event that was in fact recorded.
    /// Two ways to recognise it (<see cref="RetrySafety"/>):
    /// </para>
    /// <list type="bullet">
    /// <item><b>A key nobody else writes</b> (a generated id, an event id; the
    /// default): a violation of <paramref name="primaryKey"/> on a
    /// <em>replay</em> can only be this call's own earlier attempt, so the
    /// insert counts as done. No read is needed — which matters, because
    /// some writers may not read their table (the waitlist, the admin
    /// audit: <c>INSERT</c> only).</item>
    /// <item><b>A key callers compete for</b> (a claim, a natural key whose
    /// conflict is an answer): pass <paramref name="alsoMatching"/> with
    /// something only this call wrote. After a transient failure the row is
    /// read back by its key and that predicate; present means landed, and a
    /// replay's conflict is a rival's row and stays a conflict.</item>
    /// </list>
    /// <para>Inside a transaction the insert runs once and the transaction's
    /// own check applies; a conflict there is always the caller's.</para>
    /// </remarks>
    public static async Task InsertOnceAsync<TRow>(
        this InsolviaDbContext db,
        TRow row,
        string primaryKey,
        Func<Exception> whenExists,
        CancellationToken cancellationToken,
        Expression<Func<TRow, bool>>? alsoMatching = null)
        where TRow : class
    {
        var entry = db.Add(row);
        try
        {
            if (alsoMatching is not null)
            {
                var key = SameKey(entry);
                var mine = Expression.Lambda<Func<TRow, bool>>(
                    Expression.AndAlso(key.Body, new Rebind(alsoMatching.Parameters[0], key.Parameters[0]).Visit(alsoMatching.Body)),
                    key.Parameters);
                await db.WriteOnceAsync(
                        token => db.SaveChangesOrConflictAsync(whenExists, primaryKey, token),
                        token => db.Set<TRow>().AsNoTracking().AnyAsync(mine, token),
                        whenLanded: 1,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var attempts = 0;
            await db.Database.CreateExecutionStrategy().ExecuteAsync(
                    async token =>
                    {
                        attempts++;
                        try
                        {
                            return await db.SaveChangesAsync(token).ConfigureAwait(false);
                        }
                        catch (DbUpdateException error) when (ConditionalWrite.IsUniqueViolation(error, primaryKey))
                        {
                            // Only a replay can meet its own row: the attempt
                            // before it committed and lost its acknowledgement.
                            return attempts > 1 ? 1 : throw whenExists();
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            entry.State = EntityState.Detached;
        }
    }

    // stored => stored.K1 == v1 && stored.K2 == v2 …, over the entity's
    // primary key, with the values the pending insert carries.
    private static Expression<Func<TRow, bool>> SameKey<TRow>(EntityEntry<TRow> entry)
        where TRow : class
    {
        var key = entry.Metadata.FindPrimaryKey() ?? throw new InvalidOperationException($"{typeof(TRow).Name} has no primary key");
        var stored = Expression.Parameter(typeof(TRow), "stored");
        Expression? body = null;
        foreach (var property in key.Properties)
        {
            var member = property.PropertyInfo ?? throw new InvalidOperationException($"{typeof(TRow).Name}.{property.Name} is a shadow key");
            var equal = Expression.Equal(
                Expression.Property(stored, member),
                Expression.Constant(entry.Property(property.Name).CurrentValue, property.ClrType));
            body = body is null ? equal : Expression.AndAlso(body, equal);
        }

        return Expression.Lambda<Func<TRow, bool>>(body!, stored);
    }

    // One lambda's parameter swapped for another's, so two predicates over
    // the same row combine into one EF can translate.
    private sealed class Rebind(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }

    /// <summary>A timestamp as Npgsql accepts it for <c>timestamptz</c> through plain SQL: UTC.</summary>
    public static DateTimeOffset Utc(DateTimeOffset value) => value.ToUniversalTime();
}

/// <summary>
/// Thrown inside a store's transaction when one of its conditional writes
/// matched no row, to roll the whole unit back; the store catches it and
/// answers the port's "refused" value (false / null). Never escapes a store.
/// </summary>
internal sealed class ConditionNotMetException : Exception
{
    public ConditionNotMetException()
        : base("a conditional write matched no row")
    {
    }

    public ConditionNotMetException(string message)
        : base(message)
    {
    }

    public ConditionNotMetException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
