using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Insolvia.Api.Tests.Support;

/// <summary>
/// JSON text with keys sorted at every depth and non-ASCII escaped: a stable
/// string for a test to compare or print. Not the wire format — the wire is
/// WireJson's, and a test of a response compares parsed JSON (HttpAssert.Json).
/// </summary>
public static class SortedJson
{
    /// <summary>Serializes <paramref name="node"/> with sorted keys.</summary>
    /// <param name="node">The body; null is JSON <c>null</c>.</param>
    /// <returns>The JSON text.</returns>
    public static string Serialize(JsonNode? node)
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
                foreach (var (key, member) in obj.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    first = false;
                    WriteString(builder, key);
                    builder.Append(':');
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
                        builder.Append(',');
                    }

                    Write(builder, array[index]);
                }

                builder.Append(']');
                break;
            case JsonValue value:
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
                    default:
                        // Whole numbers only reach a body (chapters, epoch
                        // seconds, counts); System.Text.Json writes them as json.dumps does.
                        builder.Append(value.ToJsonString());
                        break;
                }

                break;
            default:
                throw new InvalidOperationException("unknown JSON node " + node.GetType().Name);
        }
    }

    private static void WriteString(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (var c in value)
        {
            switch (c)
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
                case >= ' ' and <= '~':
                    builder.Append(c);
                    break;
                default:
                    builder.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:x4}");
                    break;
            }
        }

        builder.Append('"');
    }
}
