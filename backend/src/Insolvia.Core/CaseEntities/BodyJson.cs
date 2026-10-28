using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Insolvia.Core.CaseEntities;

/// <summary>
/// A record body as plain nested JSON — Python's <c>dataclasses.asdict</c>
/// over a body dataclass, which is what provenance paths address and what
/// gets stored. Members are the snake_case field names the parsers read
/// (a member whose name carries digits pins its spelling with
/// <see cref="JsonPropertyNameAttribute"/>); enums are their wire strings;
/// money and percentages are the decimal's own text (<c>"1200.00"</c>,
/// <c>"8.50"</c>) exactly as Python stored its <c>str</c>; dates are
/// <c>YYYY-MM-DD</c>. Absent members are written as null, as <c>asdict</c>
/// wrote <c>None</c> — <see cref="Prune"/> drops them.
/// </summary>
public static class BodyJson
{
    /// <summary>The serializer settings every body shares.</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>The body as a JSON object.</summary>
    public static JsonObject ToNode<TBody>(TBody body)
        where TBody : notnull =>
        JsonSerializer.SerializeToNode(body, body.GetType(), Options)!.AsObject();

    /// <summary>
    /// Drop absent members, recursively (Python's <c>fields.prune</c>):
    /// null, and — inside a map — an empty list or map. A null INSIDE a list
    /// survives, and <c>false</c> and <c>0</c> survive everywhere: they are answers.
    /// </summary>
    public static JsonObject Prune(JsonObject body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return (JsonObject)PruneNode(body)!;
    }

    private static JsonNode? PruneNode(JsonNode? value)
    {
        switch (value)
        {
            case JsonObject map:
                var pruned = new JsonObject();
                foreach (var (key, member) in map)
                {
                    if (member is null)
                    {
                        continue;
                    }

                    var kept = PruneNode(member);
                    if (kept is JsonObject { Count: 0 } or JsonArray { Count: 0 })
                    {
                        continue;
                    }

                    pruned[key] = kept;
                }

                return pruned;

            case JsonArray list:
                return new JsonArray([.. list.Select(PruneNode)]);

            default:
                return value?.DeepClone();
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new DecimalAsTextConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>A decimal as its own text — money and rates travel as strings, never JSON numbers.</summary>
    private sealed class DecimalAsTextConverter : JsonConverter<decimal>
    {
        public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            decimal.Parse(reader.GetString()!, NumberStyles.Number, CultureInfo.InvariantCulture);

        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// Serializes a value through its RUNTIME type — for a body member declared
/// as an abstract base (a SOFA entry's payload), which System.Text.Json would
/// otherwise write as the base's (empty) member set.
/// </summary>
/// <typeparam name="TBase">The declared base type.</typeparam>
public sealed class RuntimeTypeConverter<TBase> : JsonConverter<TBase>
    where TBase : class
{
    /// <inheritdoc />
    public override TBase Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("a body is parsed by its kind's parser, never deserialized");

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, TBase value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }
}
