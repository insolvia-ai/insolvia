using System.Net;
using System.Text;

namespace Insolvia.Filing.Drivers;

/// <summary>One form on a screen: its action, method, field names (first appearance order) and hidden values.</summary>
/// <param name="Action">The form's action.</param>
/// <param name="Method">Lower-case method.</param>
/// <param name="Fields">Field names.</param>
/// <param name="Hidden">Hidden inputs' values — posted back unchanged (a token).</param>
public sealed record ScreenForm(string Action, string Method, IReadOnlyList<string> Fields, IReadOnlyDictionary<string, string> Hidden);

/// <summary>
/// A screen a driver knows: its title, and each form's action and SORTED
/// field names. Two specs are equal when their <see cref="Key"/> is.
/// </summary>
public sealed class ScreenSpec : IEquatable<ScreenSpec>
{
    /// <summary>Creates a spec.</summary>
    /// <param name="title">The page title.</param>
    /// <param name="forms">Each form: action, then its field names.</param>
    public ScreenSpec(string title, params (string Action, string[] Fields)[] forms)
    {
        ArgumentNullException.ThrowIfNull(forms);
        Title = title ?? string.Empty;
        Forms = [.. forms.Select(form => (form.Action, (IReadOnlyList<string>)[.. form.Fields.Order(StringComparer.Ordinal)]))];
        var key = new StringBuilder(Title);
        foreach (var (action, fields) in Forms)
        {
            key.Append('\u0001').Append(action).Append('\u0002').AppendJoin('\u0003', fields);
        }

        Key = key.ToString();
    }

    /// <summary>The title.</summary>
    public string Title { get; }

    /// <summary>Each form's action and sorted fields.</summary>
    public IReadOnlyList<(string Action, IReadOnlyList<string> Fields)> Forms { get; }

    /// <summary>The canonical text the equality compares.</summary>
    public string Key { get; }

    /// <inheritdoc />
    public bool Equals(ScreenSpec? other) => other is not null && Key == other.Key;

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as ScreenSpec);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Key);
}

/// <summary>A parsed court screen.</summary>
/// <param name="Title">Whitespace-collapsed title.</param>
/// <param name="Forms">The forms, in order.</param>
/// <param name="ById">Text of every element with an id — where the court puts the facts.</param>
/// <param name="Messages">Text of every element with class <c>message</c> — the court's own messages.</param>
/// <param name="Entries">Items of the list with id <c>entries</c>.</param>
public sealed record Screen(string Title, IReadOnlyList<ScreenForm> Forms, IReadOnlyDictionary<string, string> ById, IReadOnlyList<string> Messages, IReadOnlyList<string> Entries)
{
    /// <summary>The fingerprint: title and each form's action and sorted fields — structure, not words.</summary>
    public ScreenSpec Fingerprint => new(Title, [.. Forms.Select(form => (form.Action, form.Fields.ToArray()))]);
}

/// <summary>
/// Reading a court's HTML screen, and recognising it (from
/// <c>drivers/screens.py</c>; ADR 0024: "Every screen a driver will act on is
/// fingerprinted; a screen it does not recognise is a stop, never a guess").
/// A small tokenizer with Python <c>html.parser</c>'s semantics for what the
/// screens use — tags, attributes, text with character references — and no
/// network or script engine: one less thing that could fetch something.
/// </summary>
public static class Screens
{
    /// <summary>Parse <paramref name="html"/>.</summary>
    public static Screen Parse(string html)
    {
        var parser = new Parser();
        parser.Feed(html ?? string.Empty);
        return new Screen(Collapse(parser.Title.ToString()), parser.Forms, parser.ById, parser.Messages, parser.Entries);
    }

    /// <summary>Which known screen this is, by fingerprint, or null.</summary>
    public static string? Recognise(Screen screen, IReadOnlyDictionary<string, ScreenSpec> known)
    {
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentNullException.ThrowIfNull(known);
        var fingerprint = screen.Fingerprint;
        return known.FirstOrDefault(pair => pair.Value.Equals(fingerprint)).Key;
    }

    /// <summary>A court message as the record keeps it: whitespace-collapsed and bounded.</summary>
    public static string CourtText(string text, int limit = 300)
    {
        var collapsed = Collapse(text ?? string.Empty);
        return collapsed.Length <= limit ? collapsed : collapsed[..(limit - 1)] + "…";
    }

    /// <summary>Python's <c>" ".join(text.split())</c>.</summary>
    internal static string Collapse(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private sealed class Parser
    {
        private readonly List<(string Tag, string Id, StringBuilder Text)> _openIds = [];
        private readonly List<(string Tag, StringBuilder Text)> _messageDepth = [];
        private bool _inTitle;
        private (string Action, string Method, List<string> Fields, Dictionary<string, string> Hidden)? _form;
        private bool _inEntries;
        private StringBuilder? _entry;

        public StringBuilder Title { get; } = new();

        public List<ScreenForm> Forms { get; } = [];

        public Dictionary<string, string> ById { get; } = new(StringComparer.Ordinal);

        public List<string> Messages { get; } = [];

        public List<string> Entries { get; } = [];

        public void Feed(string html)
        {
            var i = 0;
            var text = new StringBuilder();
            while (i < html.Length)
            {
                if (html[i] == '<' && i + 1 < html.Length && (html[i + 1] == '/' || html[i + 1] == '!' || html[i + 1] == '?' || char.IsAsciiLetter(html[i + 1])))
                {
                    Flush(text);
                    if (html.AsSpan(i).StartsWith("<!--", StringComparison.Ordinal))
                    {
                        var end = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                        i = end < 0 ? html.Length : end + 3;
                        continue;
                    }

                    var close = FindTagEnd(html, i + 1);
                    var inner = html[(i + 1)..close];
                    i = Math.Min(close + 1, html.Length);
                    if (inner.StartsWith('!') || inner.StartsWith('?'))
                    {
                        continue;
                    }

                    if (inner.StartsWith('/'))
                    {
                        EndTag(inner[1..].Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant() ?? string.Empty);
                        continue;
                    }

                    var selfClosing = inner.EndsWith('/');
                    var (tag, attributes) = Tag(selfClosing ? inner[..^1] : inner);
                    StartTag(tag, attributes);
                    if (selfClosing)
                    {
                        EndTag(tag);
                    }

                    continue;
                }

                text.Append(html[i]);
                i++;
            }

            Flush(text);
        }

        private static int FindTagEnd(string html, int from)
        {
            char? quote = null;
            for (var i = from; i < html.Length; i++)
            {
                var c = html[i];
                if (quote is not null)
                {
                    if (c == quote)
                    {
                        quote = null;
                    }
                }
                else if (c is '"' or '\'')
                {
                    quote = c;
                }
                else if (c == '>')
                {
                    return i;
                }
            }

            return html.Length;
        }

        private static (string Tag, Dictionary<string, string> Attributes) Tag(string inner)
        {
            var i = 0;
            while (i < inner.Length && !char.IsWhiteSpace(inner[i]))
            {
                i++;
            }

            var tag = inner[..i].ToLowerInvariant();
            var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
            while (i < inner.Length)
            {
                while (i < inner.Length && (char.IsWhiteSpace(inner[i]) || inner[i] == '/'))
                {
                    i++;
                }

                var start = i;
                while (i < inner.Length && !char.IsWhiteSpace(inner[i]) && inner[i] != '=')
                {
                    i++;
                }

                var name = inner[start..i].ToLowerInvariant();
                while (i < inner.Length && char.IsWhiteSpace(inner[i]))
                {
                    i++;
                }

                var value = string.Empty;
                if (i < inner.Length && inner[i] == '=')
                {
                    i++;
                    while (i < inner.Length && char.IsWhiteSpace(inner[i]))
                    {
                        i++;
                    }

                    if (i < inner.Length && inner[i] is '"' or '\'')
                    {
                        var quote = inner[i];
                        var end = inner.IndexOf(quote, i + 1);
                        end = end < 0 ? inner.Length : end;
                        value = inner[(i + 1)..end];
                        i = Math.Min(end + 1, inner.Length);
                    }
                    else
                    {
                        var vstart = i;
                        while (i < inner.Length && !char.IsWhiteSpace(inner[i]))
                        {
                            i++;
                        }

                        value = inner[vstart..i];
                    }
                }

                if (name.Length > 0)
                {
                    attributes.TryAdd(name, WebUtility.HtmlDecode(value));
                }
            }

            return (tag, attributes);
        }

        private void StartTag(string tag, Dictionary<string, string> values)
        {
            if (tag == "title")
            {
                _inTitle = true;
            }

            if (tag == "form")
            {
                _form = (values.GetValueOrDefault("action", string.Empty), values.GetValueOrDefault("method", "get").ToLowerInvariant(), [], new Dictionary<string, string>(StringComparer.Ordinal));
            }

            if (tag is "input" or "select" or "textarea" && _form is { } form && values.TryGetValue("name", out var name) && name.Length > 0)
            {
                if (!form.Fields.Contains(name))
                {
                    form.Fields.Add(name);
                }

                if (tag == "input" && values.GetValueOrDefault("type") == "hidden")
                {
                    form.Hidden[name] = values.GetValueOrDefault("value", string.Empty);
                }
            }

            if (values.TryGetValue("id", out var id) && id.Length > 0)
            {
                _openIds.Add((tag, id, new StringBuilder()));
                if (id == "entries")
                {
                    _inEntries = true;
                }
            }

            if (values.GetValueOrDefault("class", string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains("message"))
            {
                _messageDepth.Add((tag, new StringBuilder()));
            }

            if (tag == "li" && _inEntries)
            {
                _entry = new StringBuilder();
            }
        }

        private void EndTag(string tag)
        {
            if (tag == "title")
            {
                _inTitle = false;
            }

            if (tag == "form" && _form is { } form)
            {
                Forms.Add(new ScreenForm(form.Action, form.Method, [.. form.Fields], form.Hidden));
                _form = null;
            }

            if (tag == "li" && _entry is not null)
            {
                Entries.Add(Collapse(_entry.ToString()));
                _entry = null;
            }

            if (_openIds.Count > 0 && _openIds[^1].Tag == tag)
            {
                var (_, id, text) = _openIds[^1];
                _openIds.RemoveAt(_openIds.Count - 1);
                ById[id] = Collapse(text.ToString());
                if (id == "entries")
                {
                    _inEntries = false;
                }
            }

            if (_messageDepth.Count > 0 && _messageDepth[^1].Tag == tag)
            {
                var message = Collapse(_messageDepth[^1].Text.ToString());
                _messageDepth.RemoveAt(_messageDepth.Count - 1);
                if (message.Length > 0)
                {
                    Messages.Add(message);
                }
            }
        }

        private void Flush(StringBuilder raw)
        {
            if (raw.Length == 0)
            {
                return;
            }

            var data = WebUtility.HtmlDecode(raw.ToString());
            raw.Clear();
            if (_inTitle)
            {
                Title.Append(data);
            }

            foreach (var (_, _, text) in _openIds)
            {
                text.Append(data);
            }

            foreach (var (_, text) in _messageDepth)
            {
                text.Append(data);
            }

            _entry?.Append(data);
        }
    }
}
