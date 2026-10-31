using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Insolvia.Api.Auth;
using Insolvia.Core;

namespace Insolvia.Api.Http;

/// <summary>
/// The bytes Flask's <c>jsonify</c> writes — the API's wire format, byte for
/// byte (apart from Flask's trailing newline, as <c>/health</c>): keys
/// SORTED at every depth, compact separators, and
/// <c>ensure_ascii</c> — every character outside printable ASCII escaped as
/// a lower-case <c>\uXXXX</c>, and <c>&amp;</c>, <c>&lt;</c>, <c>'</c> left alone
/// (where System.Text.Json's default encoder would escape them).
/// </summary>
public static class FlaskJson
{
    /// <summary>Serializes <paramref name="node"/> as Flask would.</summary>
    /// <param name="node">The body; null is JSON <c>null</c>.</param>
    /// <returns>The JSON text.</returns>
    public static string Serialize(JsonNode? node)
    {
        var builder = new StringBuilder();
        Write(builder, node);
        return builder.ToString();
    }

    /// <summary>A response writing <paramref name="body"/> with Flask's bare <c>application/json</c>.</summary>
    /// <param name="body">The body.</param>
    /// <param name="status">The status code.</param>
    /// <returns>The result.</returns>
    public static IResult Result(JsonNode body, int status = StatusCodes.Status200OK) => new JsonBodyResult(status, Serialize(body));

    /// <summary>
    /// Flask's <c>return "", 204</c>: no body, and Flask's default
    /// <c>text/html</c> content type, which it sends even with nothing to type.
    /// </summary>
    /// <returns>The result.</returns>
    public static IResult NoContent() => EmptyResult.Instance;

    /// <summary>A JSON string value, or JSON <c>null</c> for a null string (Python's <c>None</c>).</summary>
    /// <param name="value">The string.</param>
    /// <returns>The node.</returns>
    public static JsonNode? Text(string? value) => value is null ? null : JsonValue.Create(value);

    /// <summary>
    /// <c>datetime.isoformat(timespec="milliseconds")</c> with a literal Z —
    /// the firm and firm-user stamps (<c>firms._timestamp</c>) and the waitlist's.
    /// </summary>
    /// <param name="value">The instant.</param>
    /// <returns>The wire string.</returns>
    public static string Millis(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// <c>fields.timestamp()</c>: microseconds and a literal Z — every other
    /// firm-scoped record's stamps (clients, the library, the questionnaire,
    /// the checklist).
    /// </summary>
    /// <param name="value">The instant.</param>
    /// <returns>The wire string.</returns>
    public static string Micros(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    /// <summary>A court date as Python's <c>date.isoformat()</c>.</summary>
    /// <param name="value">The date.</param>
    /// <returns>The wire string.</returns>
    public static string Date(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// <c>fields.prune</c>: drop null members from every object, recursively,
    /// and then any member that became an empty object or array. A null
    /// INSIDE an array survives, as does an empty container nested in one;
    /// <c>false</c>, <c>0</c> and <c>""</c> survive everywhere.
    /// </summary>
    /// <param name="node">The value to prune; it is not modified.</param>
    /// <returns>The pruned copy.</returns>
    public static JsonNode? Prune(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var pruned = new JsonObject();
                foreach (var (key, member) in obj)
                {
                    if (member is null)
                    {
                        continue;
                    }

                    var value = Prune(member);
                    if (value is JsonObject { Count: 0 } or JsonArray { Count: 0 })
                    {
                        continue;
                    }

                    pruned[key] = value;
                }

                return pruned;
            case JsonArray array:
                return new JsonArray([.. array.Select(Prune)]);
            default:
                return node?.DeepClone();
        }
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

    // Python's json.encoder ESCAPE_ASCII: `"` and `\` and the five short
    // escapes, and every character outside ' '..'~' as \uXXXX in lower-case
    // hex — a UTF-16 unit at a time, which is how Python writes astral
    // characters too (as a surrogate pair).
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

    private sealed class EmptyResult : IResult
    {
        public static readonly EmptyResult Instance = new();

        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = StatusCodes.Status204NoContent;
            httpContext.Response.ContentType = "text/html; charset=utf-8";
            return Task.CompletedTask;
        }
    }
}

/// <summary>
/// The request half of every route module's <c>_json_body()</c>: refuse a
/// body over the module's cap before parsing it, then demand a JSON OBJECT.
/// Flask's <c>get_json(silent=True)</c> parsed only a JSON content type and
/// answered None for anything it could not parse — every one of those is the
/// same 400 here, never a 415 or a 422.
/// </summary>
public static class RequestBodies
{
    /// <summary>The body as a JSON object, or a <see cref="ValidationException"/>.</summary>
    /// <param name="context">The request.</param>
    /// <param name="maxBytes">The module's cap.</param>
    /// <returns>The object.</returns>
    public static async Task<JsonObject> ReadObjectAsync(HttpContext context, int maxBytes) =>
        await ReadSilentAsync(context, maxBytes).ConfigureAwait(false) as JsonObject
            ?? throw new ValidationException("request body must be a JSON object");

    /// <summary>
    /// Flask's <c>get_json(silent=True)</c> behind the cap: the parsed body,
    /// or null for a body that is not JSON (a route whose domain function
    /// refuses the shape itself, after its own earlier checks).
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="maxBytes">The module's cap.</param>
    /// <returns>The body, or null.</returns>
    public static async Task<JsonNode?> ReadSilentAsync(HttpContext context, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.Request;
        if (request.ContentLength is { } length && length > maxBytes)
        {
            throw new ValidationException($"request body exceeds {maxBytes / 1024} KiB");
        }

        if (!IsJson(request.ContentType))
        {
            return null;
        }

        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        var text = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
        try
        {
            // Python's json.loads keeps the LAST of a repeated key. A JsonNode
            // parsed with duplicates allowed only throws later, when first
            // read (a 500), so the tree is rebuilt here, last value winning.
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowDuplicateProperties = true });
            return LastWins(document.RootElement);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static JsonNode? LastWins(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new JsonObject();
                foreach (var property in element.EnumerateObject())
                {
                    obj[property.Name] = LastWins(property.Value);
                }

                return obj;
            case JsonValueKind.Array:
                return new JsonArray([.. element.EnumerateArray().Select(LastWins)]);
            case JsonValueKind.Null:
                return null;
            default:
                return JsonValue.Create(element.Clone());
        }
    }

    // Werkzeug's Request.is_json: application/json, or application/*+json.
    private static bool IsJson(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType))
        {
            return false;
        }

        var mime = contentType.Split(';', 2)[0].Trim();
        return mime.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || (mime.StartsWith("application/", StringComparison.OrdinalIgnoreCase) && mime.EndsWith("+json", StringComparison.OrdinalIgnoreCase));
    }
}
