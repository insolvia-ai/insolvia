using System.Text.Json;
using System.Text.Json.Nodes;

namespace Insolvia.Core;

/// <summary>
/// Python's <c>isinstance</c> checks, over a parsed JSON body. The domain's
/// parsers take a request body as <see cref="JsonNode"/> — the C# shape of
/// Python's <c>Mapping[str, object]</c> — and each check here answers exactly
/// what its Python counterpart did: a JSON <c>true</c> is NOT a whole number
/// (Python's <c>bool</c> is an <c>int</c> subclass and every parser excludes
/// it), and <c>1.0</c> is a number but not a whole number.
/// </summary>
public static class Json
{
    /// <summary>The string, when <paramref name="node"/> is a JSON string; otherwise null.</summary>
    public static string? AsString(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    /// <summary>Whether <paramref name="node"/> is a JSON string.</summary>
    public static bool IsString(JsonNode? node) => AsString(node) is not null;

    /// <summary>The boolean, when <paramref name="node"/> is JSON <c>true</c>/<c>false</c>; otherwise null.</summary>
    public static bool? AsBool(JsonNode? node) => node is JsonValue value
        ? value.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        }
        : null;

    /// <summary>The JSON object, or null.</summary>
    public static JsonObject? AsObject(JsonNode? node) => node as JsonObject;

    /// <summary>The JSON array, or null.</summary>
    public static JsonArray? AsArray(JsonNode? node) => node as JsonArray;

    /// <summary>
    /// A whole number — Python's <c>isinstance(x, int) and not isinstance(x, bool)</c>.
    /// A JSON number written with a fraction or an exponent is a Python float, never an int.
    /// </summary>
    public static bool TryGetWholeNumber(JsonNode? node, out long number)
    {
        number = 0;
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number)
        {
            return false;
        }

        if (value.TryGetValue(out JsonElement element))
        {
            return element.TryGetInt64(out number) && !HasFractionOrExponent(element.GetRawText());
        }

        if (value.TryGetValue(out int small))
        {
            number = small;
            return true;
        }

        if (value.TryGetValue(out long large))
        {
            number = large;
            return true;
        }

        return false;
    }

    /// <summary>Any JSON number (Python's <c>int | float</c>, bool excluded), as a double.</summary>
    public static bool TryGetNumber(JsonNode? node, out double number)
    {
        number = 0;
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number)
        {
            return false;
        }

        if (value.TryGetValue(out JsonElement element))
        {
            number = element.GetDouble();
            return true;
        }

        if (value.TryGetValue(out double d))
        {
            number = d;
            return true;
        }

        if (value.TryGetValue(out float f))
        {
            number = f;
            return true;
        }

        if (TryGetWholeNumber(node, out var whole))
        {
            number = whole;
            return true;
        }

        if (value.TryGetValue(out decimal m))
        {
            number = (double)m;
            return true;
        }

        return false;
    }

    /// <summary>The value at <paramref name="key"/> in <paramref name="node"/> when it is an object; otherwise null.</summary>
    public static JsonNode? Get(JsonNode? node, string key) =>
        node is JsonObject obj && obj.TryGetPropertyValue(key, out var found) ? found : null;

    /// <summary>Whether <paramref name="payload"/> names <paramref name="key"/> at all — Python's <c>key in payload</c>, null values included.</summary>
    public static bool Has(JsonObject payload, string key)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return payload.ContainsKey(key);
    }

    /// <summary>Python's <c>==</c> between two JSON values (absent and null are the same <c>None</c>).</summary>
    public static bool ValueEquals(JsonNode? left, JsonNode? right) => JsonNode.DeepEquals(left, right);

    private static bool HasFractionOrExponent(string raw) =>
        raw.Contains('.', StringComparison.Ordinal) || raw.Contains('e', StringComparison.OrdinalIgnoreCase);
}
