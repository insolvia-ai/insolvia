using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Insolvia.Core;
using Insolvia.Core.Firms;
using Insolvia.Core.Questionnaires;

namespace Insolvia.Data.Storage;

/// <summary>
/// The JSON a <c>jsonb</c> column holds: the domain's nested shapes (an
/// address, a letterhead, a client's editable body) in snake_case with enums
/// as their wire strings — the shape the Python store kept under
/// <c>body</c>. Only documents go here; a value that is keyed, ordered or
/// conditioned on is a real column.
/// </summary>
internal static class StoredJson
{
    /// <summary>The serializer options every stored document is written and read with.</summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(), new ValueCollectionConverterFactory() },
    };

    /// <summary><paramref name="value"/> as a document.</summary>
    public static JsonDocument ToDocument<T>(T value) => JsonSerializer.SerializeToDocument(value, Options);

    /// <summary><paramref name="value"/> as a document, or null.</summary>
    public static JsonDocument? ToNullableDocument<T>(T? value)
        where T : class => value is null ? null : ToDocument(value);

    /// <summary>The document read back as <typeparamref name="T"/>.</summary>
    public static T FromDocument<T>(JsonDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Deserialize<T>(Options) ?? throw new InvalidOperationException($"stored {typeof(T).Name} is null");
    }

    /// <summary>The document read back as <typeparamref name="T"/>, or null.</summary>
    public static T? FromNullableDocument<T>(JsonDocument? document)
        where T : class => document is null ? null : FromDocument<T>(document);

    /// <summary>A permission map as <c>{"feature": "level"}</c>, both in their wire spelling.</summary>
    public static JsonDocument PermissionsToDocument(ValueDictionary<Feature, PermissionLevel> permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        var stored = new JsonObject();
        foreach (var (feature, level) in permissions)
        {
            stored[feature.ToWire()] = level.ToWire();
        }

        return JsonSerializer.SerializeToDocument(stored, Options);
    }

    /// <summary>
    /// A stored permission map, read DEFENSIVELY (Python's
    /// <c>firm_user_from_item</c>): an entry whose feature or level this
    /// version does not know is DROPPED — it is a row from a newer version,
    /// and dropping it is the reading that cannot over-grant. Anything that
    /// is not an object reads as an empty map. Features come back in
    /// declaration order.
    /// </summary>
    public static ValueDictionary<Feature, PermissionLevel> PermissionsFromDocument(JsonDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var known = new Dictionary<Feature, PermissionLevel>();
        if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                if (Wire.TryParse<Feature>(entry.Name, out var feature)
                    && entry.Value.ValueKind == JsonValueKind.String
                    && Wire.TryParse<PermissionLevel>(entry.Value.GetString(), out var level))
                {
                    known[feature] = level;
                }
            }
        }

        return Wire.Members<Feature>()
            .Where(known.ContainsKey)
            .Select(feature => new KeyValuePair<Feature, PermissionLevel>(feature, known[feature]))
            .ToValueDictionary();
    }

    /// <summary>
    /// A questionnaire's sections, read back in catalogue order (jsonb keeps
    /// object keys in its own order, not the writer's), any id the catalogue
    /// lacks after them — kept, as the memory store keeps it.
    /// </summary>
    public static ValueDictionary<string, SectionSetting> SectionsFromDocument(JsonDocument document) =>
        FromDocument<Dictionary<string, SectionSetting>>(document)
            .OrderBy(pair => SectionRank(pair.Key))
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .ToValueDictionary();

    private static int SectionRank(string sectionId)
    {
        for (var index = 0; index < Questionnaire.SectionIds.Count; index++)
        {
            if (Questionnaire.SectionIds[index] == sectionId)
            {
                return index;
            }
        }

        return int.MaxValue;
    }

    // ValueCollection<T> is a JSON array; System.Text.Json does not know its
    // collection builder, so it is read through a list.
    private sealed class ValueCollectionConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) =>
            typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(ValueCollection<>);

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(ValueCollectionConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;
    }

    private sealed class ValueCollectionConverter<T> : JsonConverter<ValueCollection<T>>
    {
        public override ValueCollection<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            (JsonSerializer.Deserialize<List<T>>(ref reader, options) ?? []).ToValueCollection();

        public override void Write(Utf8JsonWriter writer, ValueCollection<T> value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.ToList(), options);
    }
}
