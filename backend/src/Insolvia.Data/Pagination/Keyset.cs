using Microsoft.EntityFrameworkCore;

namespace Insolvia.Data.Pagination;

/// <summary>One page of a listing, and the cursor for the next (none on the last page).</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor);

/// <summary>
/// How one listing pages: its name (what a cursor is bound to), its total
/// order, the keyset values of a row, and the "strictly after this key"
/// filter. The order must be total — end it on a unique column — or rows that
/// tie on the sort key can be skipped or repeated across pages.
/// </summary>
/// <typeparam name="TRow">The row type the query reads.</typeparam>
/// <typeparam name="TKey">The keyset values: a small record, serialised into the cursor.</typeparam>
/// <example>
/// <code>
/// record CaseKey(DateTimeOffset SortKey, Guid Id);
/// var byFirm = new Keyset&lt;CaseRow, CaseKey&gt;(
///     $"cases.by-firm{(archived ? ":archived" : "")}",
///     q => q.OrderByDescending(c => c.SortKey).ThenByDescending(c => c.Id),
///     c => new CaseKey(c.SortKey, c.Id),
///     (q, k) => q.Where(c => c.SortKey &lt; k.SortKey || (c.SortKey == k.SortKey &amp;&amp; c.Id &lt; k.Id)));
/// </code>
/// </example>
public sealed class Keyset<TRow, TKey>
    where TKey : notnull
{
    /// <summary>Defines a listing's paging.</summary>
    /// <param name="query">The listing's name; a cursor minted here is refused by every other name.</param>
    /// <param name="order">The listing's total order.</param>
    /// <param name="keyOf">The keyset values of a row, in the order's columns.</param>
    /// <param name="after">Rows strictly after a key, in that order (the keyset predicate).</param>
    public Keyset(
        string query,
        Func<IQueryable<TRow>, IOrderedQueryable<TRow>> order,
        Func<TRow, TKey> keyOf,
        Func<IQueryable<TRow>, TKey, IQueryable<TRow>> after)
    {
        ArgumentException.ThrowIfNullOrEmpty(query);
        Query = query;
        Order = order ?? throw new ArgumentNullException(nameof(order));
        KeyOf = keyOf ?? throw new ArgumentNullException(nameof(keyOf));
        After = after ?? throw new ArgumentNullException(nameof(after));
    }

    /// <summary>The name a cursor is bound to.</summary>
    public string Query { get; }

    /// <summary>The listing's total order.</summary>
    public Func<IQueryable<TRow>, IOrderedQueryable<TRow>> Order { get; }

    /// <summary>The keyset values of a row.</summary>
    public Func<TRow, TKey> KeyOf { get; }

    /// <summary>Rows strictly after a key.</summary>
    public Func<IQueryable<TRow>, TKey, IQueryable<TRow>> After { get; }
}

/// <summary>Runs a <see cref="Keyset{TRow, TKey}"/> page against a query.</summary>
public static class KeysetPaging
{
    /// <summary>The largest page a caller may ask for.</summary>
    public const int MaxLimit = 100;

    /// <summary>
    /// Reads the page after <paramref name="cursor"/> (the first page when it
    /// is <see langword="null"/>) from <paramref name="source"/>, which carries
    /// the listing's scope (firm, subject, view) but not its order.
    /// </summary>
    /// <exception cref="InvalidCursorException">The cursor is not one this listing minted.</exception>
    public static async Task<Page<TRow>> ToKeysetPageAsync<TRow, TKey>(
        this IQueryable<TRow> source,
        Keyset<TRow, TKey> keyset,
        CursorCodec codec,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(keyset);
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxLimit);

        var query = source;
        if (cursor is not null)
        {
            query = keyset.After(query, codec.Decode<TKey>(keyset.Query, cursor));
        }

        // One row more than the page: its presence is what says a next page
        // exists, without a second COUNT query.
        var rows = await keyset.Order(query).Take(limit + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (rows.Count <= limit)
        {
            return new Page<TRow>(rows, null);
        }

        rows.RemoveAt(limit);
        return new Page<TRow>(rows, codec.Encode(keyset.Query, keyset.KeyOf(rows[^1])));
    }
}
