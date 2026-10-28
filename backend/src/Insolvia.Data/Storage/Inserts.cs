using Insolvia.Data.Concurrency;
using Microsoft.EntityFrameworkCore;

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
    public static async Task InsertOnceAsync<TRow>(
        this InsolviaDbContext db,
        TRow row,
        string primaryKey,
        Func<Exception> whenExists,
        CancellationToken cancellationToken)
        where TRow : class
    {
        var entry = db.Add(row);
        try
        {
            await db.SaveChangesOrConflictAsync(whenExists, primaryKey, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            entry.State = EntityState.Detached;
        }
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
