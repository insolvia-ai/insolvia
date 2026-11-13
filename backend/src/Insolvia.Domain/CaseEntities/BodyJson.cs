using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Insolvia.Domain.Fields;

namespace Insolvia.Domain.CaseEntities;

/// <summary>
/// A case collection's body as JSON — on the wire, in the stored row, and
/// what provenance paths address (ADR 0032): camelCase members, unanswered
/// ones left out, money and rates JSON numbers read into <c>decimal</c>,
/// dates <c>yyyy-MM-dd</c>, enums their spelled names.
/// </summary>
public static class BodyJson
{
    /// <summary>The settings every collection body is written and read with.</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>The body as a JSON object, its unanswered members left out (<see cref="Prune"/>).</summary>
    public static JsonObject ToNode(object body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return Prune(JsonSerializer.SerializeToNode(body, body.GetType(), Options)!.AsObject());
    }

    /// <summary>
    /// A body that arrived from outside a request — a seed, a model's
    /// proposal, a portal answer — read as <paramref name="type"/> and checked
    /// as a request's would be (<see cref="RecordValidator"/>).
    /// </summary>
    /// <exception cref="FieldValidationException">It is not that type, or a field failed.</exception>
    public static object Read(JsonNode? node, Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        object? body;
        try
        {
            body = node.Deserialize(type, Options);
        }
        catch (JsonException error)
        {
            // A list's converter reports its item's path ("$[0].column") on the inner error.
            var at = error.Path ?? "$";
            for (var inner = error.InnerException as JsonException; inner?.Path is { } item; inner = inner.InnerException as JsonException)
            {
                at += item[1..];
            }

            var path = at.StartsWith("$.", StringComparison.Ordinal) ? at[2..] : string.Empty;
            throw new FieldValidationException(new Dictionary<string, string> { [path.Length == 0 ? "body" : path] = "Not the expected type." });
        }

        body = body ?? throw new FieldValidationException(new Dictionary<string, string> { ["body"] = "Must be an object." });
        RecordValidator.ThrowIfInvalid(body, Options);
        return body;
    }

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
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // A SOFA payload's entryType may come anywhere in its object.
            AllowOutOfOrderMetadataProperties = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
