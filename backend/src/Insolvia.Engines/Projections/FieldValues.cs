using System.Collections;
using System.Text.Json.Nodes;
using Insolvia.Core;

namespace Insolvia.Engines.Projections;

// What a projection hands the fill engine (from core/form_fill.py's value
// types): one fill per single-widget field, or per-PDF-name fills for a
// repeated field (two debtor columns, table rows). The engine itself — the
// PDF half — is Insolvia.Forms'; these are plain values.

/// <summary>What a field id maps to: one fill, or one fill per PDF name.</summary>
public abstract record FieldEntry
{
    /// <summary>The canonical JSON form the goldens and the parity check compare.</summary>
    public abstract JsonNode ToJson();
}

/// <summary>One widget's value (Python's <c>FieldFill</c> union).</summary>
public abstract record FieldFill : FieldEntry;

/// <summary>A free-entry value for a text or choice widget, written verbatim (Python's <c>Text</c>).</summary>
public sealed record TextFill(string Value) : FieldFill
{
    /// <inheritdoc/>
    public override JsonNode ToJson() => new JsonObject { ["text"] = Value };
}

/// <summary>Tick a single checkbox (Python's <c>Check</c>). An unticked box is an absent field.</summary>
public sealed record CheckFill(bool On = true) : FieldFill
{
    /// <inheritdoc/>
    public override JsonNode ToJson() => new JsonObject { ["check"] = On };
}

/// <summary>Select one export state of a radio group by the PDF's exact export (Python's <c>Option</c>).</summary>
public sealed record OptionFill(string Export) : FieldFill
{
    /// <inheritdoc/>
    public override JsonNode ToJson() => new JsonObject { ["option"] = Export };
}

/// <summary>
/// Set widget appearance states directly — the escape hatch for the official
/// PDFs' broken button groups (Python's <c>WidgetStates</c>): by on-state
/// name, or by widget position.
/// </summary>
public sealed record WidgetStatesFill(ValueCollection<string> States, ValueCollection<int> Indexes) : FieldFill
{
    /// <summary>Turn on the widgets whose on-state is named.</summary>
    public static WidgetStatesFill ByStates(params string[] states) => new([.. states], []);

    /// <summary>Turn on the widgets at these positions.</summary>
    public static WidgetStatesFill ByIndexes(params int[] indexes) => new([], [.. indexes]);

    /// <inheritdoc/>
    public override JsonNode ToJson() => new JsonObject
    {
        ["states"] = new JsonArray([.. States.Select(state => (JsonNode)state)]),
        ["indexes"] = new JsonArray([.. Indexes.Select(index => (JsonNode)index)]),
    };
}

/// <summary>Per-PDF-name fills for a repeated field, in the order they were set.</summary>
public sealed record RowFills : FieldEntry, IEnumerable<KeyValuePair<string, FieldFill>>
{
    private readonly OrderedDictionary<string, FieldFill> _fills = new(StringComparer.Ordinal);

    /// <summary>How many widgets are set.</summary>
    public int Count => _fills.Count;

    /// <summary>The fill for one PDF name.</summary>
    public FieldFill this[string pdfName]
    {
        get => _fills[pdfName];
        set => _fills[pdfName] = value;
    }

    /// <summary>Whether a widget is set.</summary>
    public bool ContainsKey(string pdfName) => _fills.ContainsKey(pdfName);

    /// <summary>The fill for one PDF name, if set.</summary>
    public bool TryGetValue(string pdfName, out FieldFill fill) => _fills.TryGetValue(pdfName, out fill!);

    /// <inheritdoc/>
    public IEnumerator<KeyValuePair<string, FieldFill>> GetEnumerator() => _fills.GetEnumerator();

    /// <inheritdoc/>
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc/>
    public bool Equals(RowFills? other) =>
        other is not null && _fills.Count == other._fills.Count && _fills.All(pair => other._fills.TryGetValue(pair.Key, out var fill) && fill.Equals(pair.Value));

    /// <inheritdoc/>
    public override int GetHashCode() => _fills.Count;

    /// <inheritdoc/>
    public override JsonNode ToJson()
    {
        var rows = new JsonObject();
        foreach (var (name, fill) in _fills)
        {
            rows[name] = fill.ToJson();
        }

        return new JsonObject { ["rows"] = rows };
    }
}

/// <summary>A projection's output: field id to fill, in the order the mapping set them (Python's <c>FieldValues</c>).</summary>
public sealed class FieldValues : IEnumerable<KeyValuePair<string, FieldEntry>>
{
    private readonly OrderedDictionary<string, FieldEntry> _entries = new(StringComparer.Ordinal);

    /// <summary>How many field ids are set.</summary>
    public int Count => _entries.Count;

    /// <summary>The field ids, in the order set.</summary>
    public IEnumerable<string> Keys => _entries.Keys;

    /// <summary>The entry for a field id; setting an existing id keeps its position (a Python dict's assignment).</summary>
    public FieldEntry this[string fieldId]
    {
        get => _entries[fieldId];
        set => _entries[fieldId] = value;
    }

    /// <summary>Whether a field id is set.</summary>
    public bool ContainsKey(string fieldId) => _entries.ContainsKey(fieldId);

    /// <summary>The entry for a field id, if set.</summary>
    public bool TryGetValue(string fieldId, out FieldEntry entry) => _entries.TryGetValue(fieldId, out entry!);

    /// <summary>The per-row fills for a repeated field, created on first use (Python's <c>values.setdefault(field_id, {})</c>).</summary>
    public RowFills Rows(string fieldId)
    {
        if (_entries.TryGetValue(fieldId, out var existing))
        {
            return existing as RowFills ?? throw new InvalidOperationException($"{fieldId} holds a single fill, not rows");
        }

        var rows = new RowFills();
        _entries[fieldId] = rows;
        return rows;
    }

    /// <summary>The canonical JSON form: <c>{field id: fill}</c>.</summary>
    public JsonObject ToJson()
    {
        var json = new JsonObject();
        foreach (var (fieldId, entry) in _entries)
        {
            json[fieldId] = entry.ToJson();
        }

        return json;
    }

    /// <inheritdoc/>
    public IEnumerator<KeyValuePair<string, FieldEntry>> GetEnumerator() => _entries.GetEnumerator();

    /// <inheritdoc/>
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// A stored fact that cannot land on this revision of the form — an
/// overflow the printed rows cannot hold, a narrative longer than its
/// printed lines. Carries every problem.
/// </summary>
public sealed class FormProjectionException : Exception
{
    /// <summary>Creates the exception from the problems found.</summary>
    public FormProjectionException(IReadOnlyList<string> problems)
        : base(string.Join("; ", problems ?? []))
    {
        Problems = [.. problems ?? []];
    }

    /// <summary>Creates the exception with no problems.</summary>
    public FormProjectionException()
        : this([])
    {
    }

    /// <summary>Creates the exception with one problem.</summary>
    public FormProjectionException(string message)
        : this([message])
    {
    }

    /// <summary>Creates the exception with one problem and its cause.</summary>
    public FormProjectionException(string message, Exception innerException)
        : base(message, innerException)
    {
        Problems = [message];
    }

    /// <summary>Every problem.</summary>
    public IReadOnlyList<string> Problems { get; }
}

/// <summary>Helvetica's advance widths — the estimate the projection layer wraps flat-form narratives against.</summary>
public static class Helvetica
{
    /// <summary>The overlay's default point size.</summary>
    public const double OverlayFontSize = 10.0;

    // Helvetica's advance widths for ASCII 32-126, per the Adobe AFM (1/1000 em).
    private const string Afm =
        "278 278 355 556 556 889 667 191 333 333 389 584 278 333 278 278 "
        + "556 556 556 556 556 556 556 556 556 556 278 278 584 584 584 556 "
        + "1015 667 667 722 722 667 611 778 722 278 500 667 556 833 722 778 "
        + "667 778 722 667 611 722 667 944 667 667 611 278 278 278 469 556 "
        + "333 556 556 500 556 556 278 556 556 222 222 500 222 833 556 556 "
        + "556 556 333 500 278 556 500 722 500 500 500 334 260 334 584";

    private static readonly int[] Widths = [.. Afm.Split(' ').Select(width => int.Parse(width, System.Globalization.CultureInfo.InvariantCulture))];

    /// <summary>The width in points <paramref name="value"/> takes at <paramref name="size"/> (Python's <c>helvetica_width</c>).</summary>
    public static double Width(string value, double size = OverlayFontSize)
    {
        ArgumentNullException.ThrowIfNull(value);
        var total = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            total += rune.Value is >= 32 and <= 126 ? Widths[rune.Value - 32] : 556;
        }

        return total / 1000.0 * size;
    }
}
