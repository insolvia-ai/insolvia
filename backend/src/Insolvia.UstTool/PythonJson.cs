using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Insolvia.UstTool;

/// <summary>
/// JSON written byte for byte as the Python ingester wrote it —
/// <c>json.dumps(value, indent=2, ensure_ascii=False) + "\n"</c> — so a
/// re-ingest of a period already in the registry is a zero-byte diff, and a
/// new release's diff is only its numbers. System.Text.Json's indented writer
/// differs in small ways (it escapes more, and its newline is the platform's),
/// so this writes the format out directly.
/// </summary>
internal static class PythonJson
{
    public static string Dumps(JsonNode? node)
    {
        var builder = new StringBuilder();
        Write(builder, node, 0);
        return builder.Append('\n').ToString();
    }

    private static void Write(StringBuilder builder, JsonNode? node, int depth)
    {
        switch (node)
        {
            case null:
                builder.Append("null");
                break;
            case JsonObject obj when obj.Count == 0:
                builder.Append("{}");
                break;
            case JsonObject obj:
                builder.Append("{\n");
                var first = true;
                foreach (var (key, value) in obj)
                {
                    if (!first)
                    {
                        builder.Append(",\n");
                    }

                    first = false;
                    Indent(builder, depth + 1);
                    String(builder, key);
                    builder.Append(": ");
                    Write(builder, value, depth + 1);
                }

                builder.Append('\n');
                Indent(builder, depth);
                builder.Append('}');
                break;
            case JsonArray array when array.Count == 0:
                builder.Append("[]");
                break;
            case JsonArray array:
                builder.Append("[\n");
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(",\n");
                    }

                    Indent(builder, depth + 1);
                    Write(builder, array[i], depth + 1);
                }

                builder.Append('\n');
                Indent(builder, depth);
                builder.Append(']');
                break;
            case JsonValue value:
                switch (value.GetValueKind())
                {
                    case JsonValueKind.String:
                        String(builder, value.GetValue<string>());
                        break;
                    case JsonValueKind.Number:
                        // The payloads carry integers only (the sequence);
                        // money is a string. A float is not written here.
                        builder.Append(long.Parse(value.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                        break;
                    case JsonValueKind.True:
                        builder.Append("true");
                        break;
                    case JsonValueKind.False:
                        builder.Append("false");
                        break;
                    default:
                        builder.Append("null");
                        break;
                }

                break;
        }
    }

    private static void Indent(StringBuilder builder, int depth) => builder.Append(' ', depth * 2);

    // ensure_ascii=False: only the quote, the backslash and the C0 controls
    // are escaped; everything else is written as itself.
    private static void String(StringBuilder builder, string text)
    {
        builder.Append('"');
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case < ' ': builder.Append(CultureInfo.InvariantCulture, $"\\u{(int)ch:x4}"); break;
                default: builder.Append(ch); break;
            }
        }

        builder.Append('"');
    }
}
