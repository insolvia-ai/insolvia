using System.Collections;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace Insolvia.Core;

/// <summary>
/// An immutable list with VALUE equality — the C# shape of a Python tuple on
/// a frozen dataclass. A record holding a plain array or <c>List</c> compares
/// it by reference, so two records describing the same client would be
/// unequal; the stores' "round-trips" and "unchanged since" checks need
/// equality by contents.
/// </summary>
[CollectionBuilder(typeof(ValueCollections), nameof(ValueCollections.Create))]
public sealed class ValueCollection<T> : IReadOnlyList<T>, IEquatable<ValueCollection<T>>
{
    private readonly ImmutableArray<T> _items;

    /// <summary>Creates the collection from <paramref name="items"/>, in order.</summary>
    public ValueCollection(IEnumerable<T> items)
    {
        _items = [.. items];
    }

    /// <inheritdoc />
    public int Count => _items.Length;

    /// <inheritdoc />
    public T this[int index] => _items[index];

    /// <summary>Equality by elements, in order.</summary>
    public static bool operator ==(ValueCollection<T>? left, ValueCollection<T>? right) =>
        left is null ? right is null : left.Equals(right);

    /// <summary>Inequality by elements, in order.</summary>
    public static bool operator !=(ValueCollection<T>? left, ValueCollection<T>? right) => !(left == right);

    /// <inheritdoc />
    public bool Equals(ValueCollection<T>? other) =>
        other is not null && _items.AsSpan().SequenceEqual(other._items.AsSpan(), EqualityComparer<T>.Default);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ValueCollection<T> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in _items)
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc />
    public override string ToString() => "[" + string.Join(", ", _items) + "]";
}

/// <summary>
/// An immutable map with VALUE equality and insertion order — the C# shape of
/// a Python dict on a frozen dataclass (a permission map, a provenance map).
/// </summary>
public sealed class ValueDictionary<TKey, TValue> : IReadOnlyDictionary<TKey, TValue>, IEquatable<ValueDictionary<TKey, TValue>>
    where TKey : notnull
{
    private readonly KeyValuePair<TKey, TValue>[] _ordered;
    private readonly Dictionary<TKey, TValue> _lookup;

    /// <summary>Creates the map from <paramref name="pairs"/>; a later duplicate key wins, as in a dict literal.</summary>
    public ValueDictionary(IEnumerable<KeyValuePair<TKey, TValue>> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        _lookup = [];
        var order = new List<TKey>();
        foreach (var (key, value) in pairs)
        {
            if (!_lookup.ContainsKey(key))
            {
                order.Add(key);
            }

            _lookup[key] = value;
        }

        _ordered = [.. order.Select(key => new KeyValuePair<TKey, TValue>(key, _lookup[key]))];
    }

    /// <inheritdoc />
    public int Count => _ordered.Length;

    /// <inheritdoc />
    public IEnumerable<TKey> Keys => _ordered.Select(pair => pair.Key);

    /// <inheritdoc />
    public IEnumerable<TValue> Values => _ordered.Select(pair => pair.Value);

    /// <inheritdoc />
    public TValue this[TKey key] => _lookup[key];

    /// <summary>Equality by contents; order does not matter (Python dict equality).</summary>
    public static bool operator ==(ValueDictionary<TKey, TValue>? left, ValueDictionary<TKey, TValue>? right) =>
        left is null ? right is null : left.Equals(right);

    /// <summary>Inequality by contents.</summary>
    public static bool operator !=(ValueDictionary<TKey, TValue>? left, ValueDictionary<TKey, TValue>? right) =>
        !(left == right);

    /// <inheritdoc />
    public bool ContainsKey(TKey key) => _lookup.ContainsKey(key);

    /// <inheritdoc />
    public bool TryGetValue(TKey key, out TValue value) => _lookup.TryGetValue(key, out value!);

    /// <inheritdoc />
    public bool Equals(ValueDictionary<TKey, TValue>? other)
    {
        if (other is null || other.Count != Count)
        {
            return false;
        }

        foreach (var (key, value) in _ordered)
        {
            if (!other._lookup.TryGetValue(key, out var theirs) || !EqualityComparer<TValue>.Default.Equals(value, theirs))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ValueDictionary<TKey, TValue> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        // Order-independent, to agree with Equals.
        var hash = 0;
        foreach (var (key, value) in _ordered)
        {
            hash ^= HashCode.Combine(key, value);
        }

        return hash;
    }

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => ((IEnumerable<KeyValuePair<TKey, TValue>>)_ordered).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Construction helpers for <see cref="ValueCollection{T}"/> and <see cref="ValueDictionary{TKey,TValue}"/>.</summary>
public static class ValueCollections
{
    /// <summary>The collection-expression builder: <c>[a, b]</c> as a <see cref="ValueCollection{T}"/>.</summary>
    public static ValueCollection<T> Create<T>(ReadOnlySpan<T> items) => new(items.ToArray());

    /// <summary>The empty map.</summary>
    public static ValueDictionary<TKey, TValue> EmptyDictionary<TKey, TValue>()
        where TKey : notnull => new([]);

    /// <summary>A value-equal list of <paramref name="items"/>.</summary>
    public static ValueCollection<T> ToValueCollection<T>(this IEnumerable<T> items) => new(items);

    /// <summary>A value-equal map of <paramref name="pairs"/>.</summary>
    public static ValueDictionary<TKey, TValue> ToValueDictionary<TKey, TValue>(this IEnumerable<KeyValuePair<TKey, TValue>> pairs)
        where TKey : notnull => new(pairs);
}
