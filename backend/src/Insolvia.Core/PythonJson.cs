using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Insolvia.Core;

/// <summary>
/// Python's <c>json.dumps</c>, for the two places its exact bytes cross a seam
/// the pipeline owns: the SQS message body (<c>separators=(",", ":")</c>) and
/// the review document the model reads (<c>sort_keys=True</c>). Defaults to
/// Python's: <c>", "</c> / <c>": "</c>, insertion order, <c>ensure_ascii</c> —
/// everything outside printable ASCII escaped as lower-case <c>\uXXXX</c>
/// (a surrogate pair as two escapes).
/// </summary>
public static class PythonJson
{
    /// <summary>Serializes <paramref name="node"/> as <c>json.dumps</c> would.</summary>
    /// <param name="node">The value; null is <c>null</c>.</param>
    /// <param name="compact"><c>separators=(",", ":")</c>.</param>
    /// <param name="sortKeys"><c>sort_keys=True</c> (code-point order, as Python sorts str).</param>
    /// <returns>The JSON text.</returns>
    public static string Dumps(JsonNode? node, bool compact = false, bool sortKeys = false)
    {
        var builder = new StringBuilder();
        Write(builder, node, compact ? "," : ", ", compact ? ":" : ": ", sortKeys);
        return builder.ToString();
    }

    private static void Write(StringBuilder builder, JsonNode? node, string item, string key, bool sortKeys)
    {
        switch (node)
        {
            case null:
                builder.Append("null");
                break;
            case JsonObject obj:
                builder.Append('{');
                var first = true;
                IEnumerable<KeyValuePair<string, JsonNode?>> members = sortKeys ? obj.OrderBy(pair => pair.Key, StringComparer.Ordinal) : obj;
                foreach (var (name, member) in members)
                {
                    if (!first)
                    {
                        builder.Append(item);
                    }

                    first = false;
                    WriteString(builder, name);
                    builder.Append(key);
                    Write(builder, member, item, key, sortKeys);
                }

                builder.Append('}');
                break;
            case JsonArray array:
                builder.Append('[');
                for (var index = 0; index < array.Count; index++)
                {
                    if (index > 0)
                    {
                        builder.Append(item);
                    }

                    Write(builder, array[index], item, key, sortKeys);
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
                if (Json.TryGetWholeNumber(value, out var whole))
                {
                    builder.Append(whole.ToString(CultureInfo.InvariantCulture));
                }
                else if (Json.TryGetNumber(value, out var real))
                {
                    builder.Append(FloatRepr(real));
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

    /// <summary>Python's float repr: the shortest round-tripping text, <c>.0</c> on a whole number, <c>1e-05</c> exponents.</summary>
    public static string FloatRepr(double real)
    {
        var text = real.ToString("R", CultureInfo.InvariantCulture);
        var exponent = text.IndexOf('E', StringComparison.Ordinal);
        if (exponent < 0)
        {
            return text.Contains('.', StringComparison.Ordinal) ? text : text + ".0";
        }

        // .NET "1E-05" / "1.5E+20" → Python "1e-05" / "1.5e+20".
        var mantissa = text[..exponent];
        var power = text[(exponent + 1)..];
        var sign = power[0] is '-' or '+' ? power[0] : '+';
        var digits = power.TrimStart('-', '+').PadLeft(2, '0');
        return $"{mantissa}e{sign}{digits}";
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
