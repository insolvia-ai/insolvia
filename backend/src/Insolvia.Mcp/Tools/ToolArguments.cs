using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Insolvia.Mcp.Tools;

/// <summary>The Python type a tool parameter was declared with.</summary>
public enum ArgumentKind
{
    /// <summary><c>str</c>, required.</summary>
    Text,

    /// <summary><c>str | None = None</c>.</summary>
    OptionalText,

    /// <summary><c>int | None = None</c>.</summary>
    OptionalInteger,

    /// <summary><c>list[str] | None = None</c>.</summary>
    OptionalTextList,

    /// <summary><c>list[dict[str, Any]]</c>, required.</summary>
    ObjectList,
}

/// <summary>One declared parameter: its wire name (camelCase — the surface's contract) and its type.</summary>
/// <param name="Name">The property name in the input schema.</param>
/// <param name="Kind">The declared type.</param>
public sealed record ToolParameter(string Name, ArgumentKind Kind)
{
    /// <summary>Whether the schema lists it under <c>required</c>.</summary>
    public bool Required => Kind is ArgumentKind.Text or ArgumentKind.ObjectList;

    /// <summary>pydantic's field title: <c>str.title()</c> of the name (<c>caseId</c> → <c>Caseid</c>).</summary>
    public string Title => Name.Length == 0 ? Name : char.ToUpperInvariant(Name[0]) + Name[1..].ToLowerInvariant();
}

/// <summary>
/// The arguments of one call, validated by pydantic's rules in lax mode over
/// the tool function's declared signature — BEFORE the tool runs. That
/// validation is part of the wire: a wrong type is not a domain refusal
/// with an <c>error</c> envelope but a text-only
/// <c>isError</c> result (<c>Error executing tool …</c>), and lax mode
/// ACCEPTS some inputs a strict reading would not (<c>"5"</c>, <c>2.0</c>
/// and <c>true</c> are all an integer limit). Unknown arguments are ignored.
/// </summary>
/// <remarks>
/// The message keeps pydantic's layout — the count line, then per error the
/// location, the message, <c>[type=…, input_value=…, input_type=…]</c> and
/// the documentation link — with the Python <c>repr</c> of the input. A
/// harness reads it as prose; the parity that matters is <c>isError</c> with
/// no <c>structuredContent</c>, which the protocol tests pin.
/// </remarks>
public sealed class ToolArguments
{
    private const string PydanticDocs = "https://errors.pydantic.dev/2.14/v/";

    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

    private ToolArguments()
    {
    }

    /// <summary>A text argument (null when optional and absent).</summary>
    /// <param name="name">The parameter.</param>
    /// <returns>The value.</returns>
    public string? Text(string name) => (string?)_values.GetValueOrDefault(name);

    /// <summary>An integer argument (null when absent).</summary>
    /// <param name="name">The parameter.</param>
    /// <returns>The value.</returns>
    public long? WholeNumber(string name) => (long?)_values.GetValueOrDefault(name);

    /// <summary>A list-of-text argument (null when absent).</summary>
    /// <param name="name">The parameter.</param>
    /// <returns>The value.</returns>
    public IReadOnlyList<string>? TextList(string name) => (IReadOnlyList<string>?)_values.GetValueOrDefault(name);

    /// <summary>A list-of-objects argument, as the JSON array it arrived as.</summary>
    /// <param name="name">The parameter.</param>
    /// <returns>The value.</returns>
    public JsonArray? ObjectList(string name) => (JsonArray?)_values.GetValueOrDefault(name);

    /// <summary>Validate <paramref name="arguments"/> against <paramref name="parameters"/>.</summary>
    /// <param name="tool">The tool's name (the error message names its argument model).</param>
    /// <param name="parameters">The declared parameters, in signature order.</param>
    /// <param name="arguments">The call's arguments (null is none).</param>
    /// <param name="parsed">The validated arguments.</param>
    /// <param name="error">The pydantic-shaped message when validation failed.</param>
    /// <returns>Whether they were valid.</returns>
    public static bool TryParse(string tool, IReadOnlyList<ToolParameter> parameters, JsonObject? arguments, out ToolArguments parsed, out string? error)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        arguments ??= [];
        parsed = new ToolArguments();
        var errors = new List<string>();
        foreach (var parameter in parameters)
        {
            var present = arguments.TryGetPropertyValue(parameter.Name, out var value);
            if (!present)
            {
                if (parameter.Required)
                {
                    errors.Add(Error(parameter.Name, "Field required", "missing", arguments));
                }

                parsed._values[parameter.Name] = null;
                continue;
            }

            if (value is null && !parameter.Required)
            {
                parsed._values[parameter.Name] = null;
                continue;
            }

            switch (parameter.Kind)
            {
                case ArgumentKind.Text or ArgumentKind.OptionalText:
                    if (IsString(value, out var text))
                    {
                        parsed._values[parameter.Name] = text;
                    }
                    else
                    {
                        errors.Add(Error(parameter.Name, "Input should be a valid string", "string_type", value));
                    }

                    break;

                case ArgumentKind.OptionalInteger:
                    var integerError = ParseWholeNumber(parameter.Name, value, out var integer);
                    if (integerError is null)
                    {
                        parsed._values[parameter.Name] = integer;
                    }
                    else
                    {
                        errors.Add(integerError);
                    }

                    break;

                case ArgumentKind.OptionalTextList:
                    if (value is JsonArray texts)
                    {
                        var items = new List<string>();
                        var before = errors.Count;
                        for (var index = 0; index < texts.Count; index++)
                        {
                            if (IsString(texts[index], out var item))
                            {
                                items.Add(item!);
                            }
                            else
                            {
                                errors.Add(Error($"{parameter.Name}.{index}", "Input should be a valid string", "string_type", texts[index]));
                            }
                        }

                        if (errors.Count == before)
                        {
                            parsed._values[parameter.Name] = items;
                        }
                    }
                    else
                    {
                        errors.Add(Error(parameter.Name, "Input should be a valid list", "list_type", value));
                    }

                    break;

                case ArgumentKind.ObjectList:
                    if (value is JsonArray objects)
                    {
                        var before = errors.Count;
                        for (var index = 0; index < objects.Count; index++)
                        {
                            if (objects[index] is not JsonObject)
                            {
                                errors.Add(Error($"{parameter.Name}.{index}", "Input should be a valid dictionary", "dict_type", objects[index]));
                            }
                        }

                        if (errors.Count == before)
                        {
                            parsed._values[parameter.Name] = objects;
                        }
                    }
                    else
                    {
                        errors.Add(Error(parameter.Name, "Input should be a valid list", "list_type", value));
                    }

                    break;
            }
        }

        if (errors.Count == 0)
        {
            error = null;
            return true;
        }

        var noun = errors.Count == 1 ? "error" : "errors";
        error = $"Error executing tool {tool}: {errors.Count} validation {noun} for {tool}_toolArguments\n" + string.Join("\n", errors);
        return false;
    }

    // pydantic's lax int: an integral JSON number, a bool (True is 1), a float
    // with no fractional part, or a string that parses as an integer.
    private static string? ParseWholeNumber(string name, JsonNode? value, out long result)
    {
        result = 0;
        switch (value?.GetValueKind())
        {
            case JsonValueKind.True:
                result = 1;
                return null;
            case JsonValueKind.False:
                return null;
            case JsonValueKind.Number:
                var number = value.AsValue();
                if (number.TryGetValue<long>(out result))
                {
                    return null;
                }

                var real = number.GetValue<double>();
                if (Math.Floor(real) == real && Math.Abs(real) < 9.2e18)
                {
                    result = (long)real;
                    return null;
                }

                return Error(name, "Input should be a valid integer, got a number with a fractional part", "int_from_float", value);
            case JsonValueKind.String:
                var text = value.GetValue<string>().Trim();
                if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result))
                {
                    return null;
                }

                return Error(name, "Input should be a valid integer, unable to parse string as an integer", "int_parsing", value);
            default:
                return Error(name, "Input should be a valid integer", "int_type", value);
        }
    }

    private static bool IsString(JsonNode? value, out string? text)
    {
        if (value?.GetValueKind() == JsonValueKind.String)
        {
            text = value.GetValue<string>();
            return true;
        }

        text = null;
        return false;
    }

    private static string Error(string location, string message, string type, JsonNode? input) =>
        $"{location}\n  {message} [type={type}, input_value={Repr(input)}, input_type={TypeName(input)}]\n    For further information visit {PydanticDocs}{type}";

    private static string TypeName(JsonNode? value) => value?.GetValueKind() switch
    {
        null or JsonValueKind.Null => "NoneType",
        JsonValueKind.String => "str",
        JsonValueKind.True or JsonValueKind.False => "bool",
        JsonValueKind.Number => value.AsValue().TryGetValue<long>(out _) ? "int" : "float",
        JsonValueKind.Array => "list",
        _ => "dict",
    };

    // Python's repr() of the value as json.loads would have built it.
    private static string Repr(JsonNode? value)
    {
        switch (value?.GetValueKind())
        {
            case null or JsonValueKind.Null:
                return "None";
            case JsonValueKind.True:
                return "True";
            case JsonValueKind.False:
                return "False";
            case JsonValueKind.Number:
                return value.AsValue().TryGetValue<long>(out var whole)
                    ? whole.ToString(CultureInfo.InvariantCulture)
                    : value.AsValue().GetValue<double>().ToString("R", CultureInfo.InvariantCulture);
            case JsonValueKind.String:
                return QuoteText(value.GetValue<string>());
            case JsonValueKind.Array:
                return "[" + string.Join(", ", value.AsArray().Select(Repr)) + "]";
            default:
                return "{" + string.Join(", ", value.AsObject().Select(pair => QuoteText(pair.Key) + ": " + Repr(pair.Value))) + "}";
        }
    }

    private static string QuoteText(string text)
    {
        var quote = text.Contains('\'', StringComparison.Ordinal) && !text.Contains('"', StringComparison.Ordinal) ? '"' : '\'';
        var builder = new StringBuilder().Append(quote);
        foreach (var ch in text)
        {
            builder.Append(ch switch
            {
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when ch == quote => "\\" + ch,
                _ => ch.ToString(),
            });
        }

        return builder.Append(quote).ToString();
    }
}
