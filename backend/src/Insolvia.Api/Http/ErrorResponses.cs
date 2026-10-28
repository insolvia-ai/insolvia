using System.Text.Json.Nodes;
using Insolvia.Core;
using Insolvia.Core.Auth;

namespace Insolvia.Api.Http;

/// <summary>
/// Every error body the API writes, in one place: the JSON bodies of
/// services/api app_factory.py's handlers, and the HTML bodies Flask
/// (Werkzeug) answers for an HTTP-level refusal — a route that does not
/// exist, a method a route does not take. Both shapes are pinned by tests,
/// because the API client reads the JSON ones and the contract suite compares
/// the two backends.
/// </summary>
public static class ErrorResponses
{
    /// <summary>The one body every 401 carries — the caller learns "unauthorized", never which check failed.</summary>
    public const string UnauthorizedJson = """{"error":"Unauthorized","message":"authentication required"}""";

    /// <summary>The body of every unexpected failure: no type, no message, no trace.</summary>
    public const string InternalErrorJson = """{"error":"InternalError","message":"request failed"}""";

    // Werkzeug's HTTPException.get_body, for the codes this host can produce
    // without a handler: (name, description).
    private static readonly Dictionary<int, (string Name, string Description)> HttpErrors = new()
    {
        [400] = ("Bad Request", "The browser (or proxy) sent a request that this server could not understand."),
        [404] = ("Not Found", "The requested URL was not found on the server. If you entered the URL manually please check your spelling and try again."),
        [405] = ("Method Not Allowed", "The method is not allowed for the requested URL."),
        [413] = ("Request Entity Too Large", "The data value transmitted exceeds the capacity limit."),
        [415] = ("Unsupported Media Type", "The server does not support the media type transmitted in the request."),
    };

    /// <summary>
    /// The status and JSON body for an expected (Core) failure, or null when
    /// <paramref name="error"/> is not one — app_factory.py's handlers, most
    /// specific first, exactly as Flask dispatched them.
    /// </summary>
    /// <param name="error">The exception a route raised.</param>
    /// <returns>The status and body, or null for an unexpected failure.</returns>
    public static (int Status, string Body)? ForApiException(Exception error) => error switch
    {
        // The shape the marketing action surfaces per-field: {error, fields}.
        FieldValidationException fields => (400, FlaskJson.Serialize(new JsonObject
        {
            ["error"] = "ValidationError",
            ["fields"] = new JsonObject([.. fields.Fields.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)pair.Value))]),
        })),
        ValidationException validation => (400, ErrorJson("ValidationError", validation)),
        ConflictException conflict => (409, ErrorJson("ConflictError", conflict)),

        // A ForbiddenException with its own code: its remedy is a fresh
        // sign-in, not a permission (ADR 0024).
        ReauthenticationRequiredException reauthenticate => (403, ErrorJson("ReauthenticationRequired", reauthenticate)),
        ForbiddenException forbidden => (403, ErrorJson("ForbiddenError", forbidden)),
        NotFoundException notFound => (404, ErrorJson("NotFoundError", notFound)),

        // Python answered `error.__class__.__name__`; Python's classes end
        // in Error where these end in Exception.
        ApiException other => (400, ErrorJson(PythonClassName(other), other)),
        _ => null,
    };

    /// <summary>Werkzeug's HTML body for <paramref name="status"/>, or null when this host never produces it.</summary>
    /// <param name="status">An HTTP status code.</param>
    /// <returns>The HTML, ending in a newline as Werkzeug's does.</returns>
    public static string? WerkzeugHtml(int status) => HttpErrors.TryGetValue(status, out var error)
        ? "<!doctype html>\n<html lang=en>\n"
            + $"<title>{status} {error.Name}</title>\n"
            + $"<h1>{error.Name}</h1>\n"
            + $"<p>{error.Description}</p>\n"
        : null;

    /// <summary>Writes a JSON body with Flask's bare <c>application/json</c> content type.</summary>
    /// <param name="context">The request.</param>
    /// <param name="status">The status.</param>
    /// <param name="body">The serialized body.</param>
    /// <returns>A task that completes when the body is written.</returns>
    public static Task WriteJsonAsync(HttpContext context, int status, string body)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(body, context.RequestAborted);
    }

    /// <summary>Writes Werkzeug's HTML body for <paramref name="status"/>.</summary>
    /// <param name="context">The request.</param>
    /// <param name="status">A status <see cref="WerkzeugHtml"/> knows.</param>
    /// <returns>A task that completes when the body is written.</returns>
    public static Task WriteHtmlAsync(HttpContext context, int status)
    {
        ArgumentNullException.ThrowIfNull(context);
        var body = WerkzeugHtml(status) ?? throw new ArgumentOutOfRangeException(nameof(status), status, "no Werkzeug body for this status");
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/html; charset=utf-8";
        return context.Response.WriteAsync(body, context.RequestAborted);
    }

    /// <summary>
    /// What Python's <c>str(error)</c> was: the message the exception was
    /// built with, and the empty string for one built with none (where .NET
    /// substitutes "Exception of type … was thrown.").
    /// </summary>
    /// <param name="error">The exception.</param>
    /// <returns>Its message as Python rendered it.</returns>
    public static string PythonMessage(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.Message == $"Exception of type '{error.GetType().FullName}' was thrown." ? string.Empty : error.Message;
    }

    private static string PythonClassName(Exception error)
    {
        var name = error.GetType().Name;
        return name.EndsWith("Exception", StringComparison.Ordinal) ? name[..^"Exception".Length] + "Error" : name;
    }

    // Flask's jsonify: sorted keys, and ensure_ascii — "doesn't" keeps its
    // apostrophe and an em dash is \u2014, where Utf8JsonWriter's default
    // encoder would write \u0027 and the raw character.
    private static string ErrorJson(string code, Exception error) =>
        FlaskJson.Serialize(new JsonObject { ["error"] = code, ["message"] = PythonMessage(error) });
}
