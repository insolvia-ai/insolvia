using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Insolvia.Domain;

namespace Insolvia.Api.Http;

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
