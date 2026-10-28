using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Insolvia.Mcp.Views;

/// <summary>
/// Python's <c>json.dumps(value)</c> with its defaults — the bytes of every
/// tool result's text block (api/results.py serialises the structured
/// content that way). Keys in INSERTION order, <c>", "</c> and <c>": "</c>
/// separators, and <c>ensure_ascii</c>: everything outside printable ASCII
/// escaped as a lower-case <c>\uXXXX</c> (surrogate pairs as two escapes).
/// </summary>
public static class PyJson
{
    /// <summary>Serializes <paramref name="node"/> as <c>json.dumps</c> would.</summary>
    /// <param name="node">The value; null is <c>null</c>.</param>
    /// <returns>The JSON text.</returns>
    public static string Dumps(JsonNode? node)
    {
        var builder = new StringBuilder();
        Write(builder, node);
        return builder.ToString();
    }

    private static void Write(StringBuilder builder, JsonNode? node)
    {
        switch (node)
        {
            case null:
                builder.Append("null");
                break;
            case JsonObject obj:
                builder.Append('{');
                var first = true;
                foreach (var (key, member) in obj)
                {
                    if (!first)
                    {
                        builder.Append(", ");
                    }

                    first = false;
                    WriteString(builder, key);
                    builder.Append(": ");
                    Write(builder, member);
                }

                builder.Append('}');
                break;
            case JsonArray array:
                builder.Append('[');
                for (var index = 0; index < array.Count; index++)
                {
                    if (index > 0)
                    {
                        builder.Append(", ");
                    }

                    Write(builder, array[index]);
                }

                builder.Append(']');
                break;
            case JsonValue value:
                WriteValue(builder, value);
                break;
            default:
                throw new ArgumentException("unexpected JSON node", nameof(node));
        }
    }

    private static void WriteValue(StringBuilder builder, JsonValue value)
    {
        switch (value.GetValueKind())
        {
            case JsonValueKind.String:
                WriteString(builder, value.GetValue<string>());
                break;
            case JsonValueKind.True:
                builder.Append("true");
                break;
            case JsonValueKind.False:
                builder.Append("false");
                break;
            case JsonValueKind.Null:
                builder.Append("null");
                break;
            case JsonValueKind.Number:
                if (value.TryGetValue<long>(out var whole))
                {
                    builder.Append(whole.ToString(CultureInfo.InvariantCulture));
                }
                else if (value.TryGetValue<int>(out var small))
                {
                    builder.Append(small.ToString(CultureInfo.InvariantCulture));
                }
                else if (value.TryGetValue<double>(out var real))
                {
                    // Python's float repr: the shortest round-tripping text, with
                    // a ".0" on a whole number.
                    var text = real.ToString("R", CultureInfo.InvariantCulture);
                    builder.Append(text.Contains('.', StringComparison.Ordinal) || text.Contains('E', StringComparison.Ordinal) ? text : text + ".0");
                }
                else
                {
                    builder.Append(value.ToJsonString());
                }

                break;
            default:
                builder.Append(value.ToJsonString());
                break;
        }
    }

    private static void WriteString(StringBuilder builder, string text)
    {
        builder.Append('"');
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                default:
                    if (ch < 0x20 || ch > 0x7e)
                    {
                        builder.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(ch);
                    }

                    break;
            }
        }

        builder.Append('"');
    }
}
