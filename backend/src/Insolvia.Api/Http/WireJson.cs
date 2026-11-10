using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Insolvia.Api.Http;

/// <summary>
/// How a handler's typed result becomes JSON: the one serializer setup every
/// response shares, and the one the OpenAPI document is generated from.
/// </summary>
/// <remarks>
/// <para>A view record is camelCase unless it carries <see cref="SnakeCaseAttribute"/>,
/// and writes a null member as <c>null</c> unless the member says
/// <c>[JsonIgnore(Condition = WhenWritingNull)]</c>: the wire names both,
/// and the clients read both.</para>
/// <para>A <b>domain</b> record (anything in <c>Insolvia.Domain</c>: a case
/// body, a provenance entry, an address) is written as the body serializer
/// writes it (<c>BodyJson</c>): snake_case, and every unanswered member —
/// null, an empty list or map, an object with nothing in it — absent.
/// Bodies are progressive; absent is how they say "not answered yet".</para>
/// <para>A property marked <see cref="FlattenAttribute"/> writes its members
/// beside its owner's, the way a stored body sits at the top of its record.</para>
/// </remarks>
public static class WireJson
{
    private const string DomainNamespace = "Insolvia.Domain";

    /// <summary>The settings, for a serializer outside the HTTP pipeline (a test, a log).</summary>
    public static JsonSerializerOptions Options { get; } = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web), readOnly: true);

    /// <summary>Applies the wire's settings to <paramref name="options"/> — ASP.NET's own, from <c>ConfigureHttpJsonOptions</c>.</summary>
    /// <param name="options">The options to change.</param>
    /// <param name="readOnly">Lock them afterwards.</param>
    /// <returns>The same options.</returns>
    public static JsonSerializerOptions Configure(JsonSerializerOptions options, bool readOnly = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        options.NumberHandling = JsonNumberHandling.Strict;
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new DecimalAsTextConverter());
        options.TypeInfoResolver = (options.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver()).WithAddedModifier(Modify);
        if (readOnly)
        {
            options.MakeReadOnly();
        }

        return options;
    }

    private static void Modify(JsonTypeInfo type)
    {
        if (type.Kind != JsonTypeInfoKind.Object)
        {
            return;
        }

        var domain = type.Type.Namespace?.StartsWith(DomainNamespace, StringComparison.Ordinal) == true;
        var snake = domain || type.Type.GetCustomAttribute<SnakeCaseAttribute>() is not null;
        for (var index = 0; index < type.Properties.Count; index++)
        {
            var property = type.Properties[index];
            if (snake && property.AttributeProvider?.IsDefined(typeof(JsonPropertyNameAttribute), inherit: true) != true)
            {
                property.Name = JsonNamingPolicy.SnakeCaseLower.ConvertName(MemberName(property));
            }

            if (domain)
            {
                property.ShouldSerialize = (_, value) => !IsAbsent(value, type.Options);
            }
            else if (property.AttributeProvider?.GetCustomAttributes(typeof(JsonIgnoreAttribute), inherit: true) is [JsonIgnoreAttribute { Condition: JsonIgnoreCondition.WhenWritingNull }])
            {
                // Said again where the document generator can see it: an optional member.
                property.ShouldSerialize = (_, value) => value is not null;
            }
        }

        Flatten(type);
    }

    // [Flatten]: the property's own members take its place, read through it.
    private static void Flatten(JsonTypeInfo type)
    {
        var flattened = type.Properties.Where(p => p.AttributeProvider?.IsDefined(typeof(FlattenAttribute), inherit: true) == true).ToList();
        foreach (var outer in flattened)
        {
            type.Properties.Remove(outer);
            var owner = outer.Get ?? throw new InvalidOperationException($"{type.Type.Name}.{outer.Name} is flattened but not readable");
            foreach (var inner in type.Options.GetTypeInfo(outer.PropertyType).Properties)
            {
                var read = inner.Get ?? throw new InvalidOperationException($"{outer.PropertyType.Name}.{inner.Name} is not readable");
                var member = type.CreateJsonPropertyInfo(inner.PropertyType, inner.Name);
                member.Get = target => read(owner(target)!);
                member.CustomConverter = inner.CustomConverter;
                if (inner.ShouldSerialize is { } keep)
                {
                    member.ShouldSerialize = (target, value) => keep(owner(target)!, value);
                }

                type.Properties.Add(member);
            }
        }
    }

    // The C# member's name, which the default resolver has already camel-cased.
    private static string MemberName(JsonPropertyInfo property) => property.AttributeProvider is MemberInfo member ? member.Name : property.Name;

    // BodyJson.Prune's rule: null, an empty list or map, or an object whose
    // every member is itself absent. "" and false and 0 are answers.
    private static bool IsAbsent(object? value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case null:
                return true;
            case string:
                return false;
            case JsonObject node:
                return node.Count == 0;
            case JsonArray node:
                return node.Count == 0;
            case IEnumerable items:
                return !items.GetEnumerator().MoveNext();
        }

        var type = options.GetTypeInfo(value.GetType());
        return type.Kind == JsonTypeInfoKind.Object
            && type.Properties.All(p => p.Get is { } read && (p.ShouldSerialize is { } keep ? !keep(value, read(value)) : read(value) is null));
    }

    // Money and rates travel as strings, never JSON numbers.
    private sealed class DecimalAsTextConverter : JsonConverter<decimal>
    {
        public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            decimal.Parse(reader.GetString()!, NumberStyles.Number, CultureInfo.InvariantCulture);

        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }
}

/// <summary>The record's members are snake_case on the wire.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class SnakeCaseAttribute : Attribute;

/// <summary>The property's members are written beside its owner's, not under its name.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class FlattenAttribute : Attribute;

/// <summary>
/// Every refusal's body: the error's code, and either a message or — for a
/// validation error — the fields at fault, each with what is wrong with it.
/// </summary>
/// <param name="Error">The code: <c>ValidationError</c>, <c>NotFound</c>, <c>Forbidden</c>, <c>Conflict</c>, ….</param>
/// <param name="Message">What went wrong, for a person.</param>
/// <param name="Fields">For a validation error: each field at fault, by name, and what is wrong with it.</param>
public sealed record ErrorBody(
    string Error,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, string>? Fields);
