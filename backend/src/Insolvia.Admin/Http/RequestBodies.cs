using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Insolvia.Domain;

namespace Insolvia.Admin.Http;

/// <summary>
/// Reading a request body: refuse one over the route's cap before parsing it,
/// then demand a JSON OBJECT. A missing or non-JSON content type and anything
/// that does not parse are all the same 400, never a 415 or a 422.
/// </summary>
public static class RequestBodies
{
    /// <summary>The body as a JSON object, or a <see cref="ValidationException"/>.</summary>
    /// <param name="context">The request.</param>
    /// <param name="maxBytes">The module's cap.</param>
    /// <returns>The object.</returns>
    public static async Task<JsonObject> ReadObjectAsync(HttpContext context, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.Request;
        if (request.ContentLength is { } length && length > maxBytes)
        {
            throw new ValidationException($"request body exceeds {maxBytes / 1024} KiB");
        }

        if (!IsJson(request.ContentType))
        {
            throw new ValidationException("request body must be a JSON object");
        }

        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        var text = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
        return Parse(text) as JsonObject ?? throw new ValidationException("request body must be a JSON object");
    }

    /// <summary>The JSON in <paramref name="text"/>, or null when it is not JSON.</summary>
    /// <remarks>
    /// Python's json.loads keeps the LAST of a repeated key. A JsonNode parsed
    /// with duplicates allowed only throws later, when first read (a 500), so
    /// the tree is rebuilt here, last value winning — as Insolvia.Api's
    /// RequestBodies does.
    /// </remarks>
    /// <param name="text">The body.</param>
    /// <returns>The tree, or null.</returns>
    public static JsonNode? Parse(string text)
    {
        try
        {
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

    // application/json, or application/*+json.
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
