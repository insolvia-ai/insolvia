using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Npgsql;

namespace Insolvia.Data.Concurrency;

/// <summary>
/// The Postgres shape of DynamoDB's conditional write. A store writes
/// <c>UPDATE … WHERE &lt;key&gt; AND &lt;precondition&gt;</c> as one statement;
/// zero rows affected means the precondition did not hold (or the row is
/// gone), and the caller's domain error is thrown — the error a DynamoDB
/// store raised on <c>ConditionalCheckFailedException</c> before the port.
/// </summary>
/// <remarks>
/// Never read-then-write: a <c>SELECT</c> followed by an unconditional
/// <c>UPDATE</c> is the race these helpers exist to close. The precondition
/// goes in the statement's <c>WHERE</c>, where the row lock makes it atomic.
/// </remarks>
/// <example>
/// <code>
/// await db.Set&lt;CaseRow&gt;()
///     .Where(c => c.FirmId == firmId &amp;&amp; c.Id == caseId &amp;&amp; c.Status == expectedStatus)
///     .ExecuteConditionalUpdateAsync(
///         set => set.SetProperty(c => c.Status, next).SetProperty(c => c.UpdatedAt, now),
///         () => new ConflictError(CaseErrors.StatusMoved),
///         cancellationToken);
/// </code>
/// </example>
public static class ConditionalWrite
{
    /// <summary>
    /// Runs <c>UPDATE … WHERE</c> over the rows <paramref name="rowsMeetingPrecondition"/>
    /// selects, throwing <paramref name="whenNoRowMatches"/>'s exception when it updates none.
    /// </summary>
    /// <returns>The number of rows updated (at least one).</returns>
    public static async Task<int> ExecuteConditionalUpdateAsync<T>(
        this IQueryable<T> rowsMeetingPrecondition,
        Action<UpdateSettersBuilder<T>> setters,
        Func<Exception> whenNoRowMatches,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(whenNoRowMatches);
        var rows = await rowsMeetingPrecondition.ExecuteUpdateAsync(setters, cancellationToken).ConfigureAwait(false);
        return RequireRowsAffected(rows, whenNoRowMatches);
    }

    /// <summary>
    /// Runs <c>DELETE … WHERE</c> over the rows <paramref name="rowsMeetingPrecondition"/>
    /// selects, throwing <paramref name="whenNoRowMatches"/>'s exception when it deletes none.
    /// </summary>
    /// <returns>The number of rows deleted (at least one).</returns>
    public static async Task<int> ExecuteConditionalDeleteAsync<T>(
        this IQueryable<T> rowsMeetingPrecondition,
        Func<Exception> whenNoRowMatches,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(whenNoRowMatches);
        var rows = await rowsMeetingPrecondition.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        return RequireRowsAffected(rows, whenNoRowMatches);
    }

    /// <summary>
    /// Runs a parameterised SQL write (the interpolation holes become
    /// parameters), throwing <paramref name="whenNoRowMatches"/>'s exception
    /// when it affects no row. For a write that reads better as SQL.
    /// </summary>
    /// <returns>The number of rows affected (at least one).</returns>
    public static async Task<int> ExecuteConditionalSqlAsync(
        this DatabaseFacade database,
        FormattableString sql,
        Func<Exception> whenNoRowMatches,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(whenNoRowMatches);
        var rows = await database.ExecuteSqlInterpolatedAsync(sql, cancellationToken).ConfigureAwait(false);
        return RequireRowsAffected(rows, whenNoRowMatches);
    }

    /// <summary>
    /// The zero-rows rule on its own, for a plain <see cref="NpgsqlCommand"/>'s
    /// <see cref="System.Data.Common.DbCommand.ExecuteNonQueryAsync()"/> result.
    /// </summary>
    public static int RequireRowsAffected(int rows, Func<Exception> whenNoRowMatches)
    {
        ArgumentNullException.ThrowIfNull(whenNoRowMatches);
        return rows > 0 ? rows : throw whenNoRowMatches();
    }

    /// <summary>
    /// <see cref="DbContext.SaveChangesAsync(CancellationToken)"/>, translating
    /// a unique-key violation into <paramref name="whenKeyExists"/>'s
    /// exception: DynamoDB's <c>attribute_not_exists</c> put (approval
    /// single-use, a record that may be created once).
    /// </summary>
    /// <param name="context">The context with the pending inserts.</param>
    /// <param name="whenKeyExists">The domain error for "it already exists".</param>
    /// <param name="constraintName">Translate only this constraint's violation;
    /// <see langword="null"/> translates any unique violation.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <remarks>After a conflict the context still tracks the rejected entity;
    /// a request-scoped context is discarded with the request, which is the
    /// intended use.</remarks>
    public static async Task<int> SaveChangesOrConflictAsync(
        this DbContext context,
        Func<Exception> whenKeyExists,
        string? constraintName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(whenKeyExists);
        try
        {
            return await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException error) when (IsUniqueViolation(error, constraintName))
        {
            throw whenKeyExists();
        }
    }

    /// <summary>Whether <paramref name="error"/> is (or wraps) a Postgres unique-key violation.</summary>
    public static bool IsUniqueViolation(Exception error, string? constraintName = null)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres)
            {
                return constraintName is null || postgres.ConstraintName == constraintName;
            }
        }

        return false;
    }
}
