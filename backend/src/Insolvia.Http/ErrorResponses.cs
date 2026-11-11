using Insolvia.Domain;
using Insolvia.Domain.Auth;
using Microsoft.AspNetCore.Http;

namespace Insolvia.Http;

/// <summary>
/// Every error body an HTTP host writes, in one place, each an <see cref="ErrorBody"/>:
/// the domain's refusals (<see cref="ForApiException"/>), the framework's own
/// (<see cref="ForStatus"/> — a route that does not exist, a method it does not
/// take, an unreadable body), the one 401 and the one 500. The clients map
/// the <c>error</c> code to its exception classes.
/// </summary>
public static class ErrorResponses
{
    /// <summary>The one body every 401 carries — the caller learns "unauthorized", never which check failed.</summary>
    public static readonly ErrorBody Unauthorized = new("Unauthorized", "authentication required", null);

    /// <summary>The body of every unexpected failure: no type, no message, no trace.</summary>
    public static readonly ErrorBody InternalError = new("InternalError", "request failed", null);

    // The refusals the framework makes before a handler runs.
    private static readonly Dictionary<int, ErrorBody> HttpErrors = new()
    {
        [400] = new("BadRequestError", "The request could not be read.", null),
        [404] = new("NotFoundError", "No such route.", null),
        [405] = new("MethodNotAllowedError", "This route does not take that method.", null),
        [413] = new("PayloadTooLargeError", "The request body is too large.", null),
        [415] = new("UnsupportedMediaTypeError", "The request body must be JSON.", null),
    };

    /// <summary>
    /// The status and body for an expected (domain) failure, or null when
    /// <paramref name="error"/> is not one — most specific first.
    /// </summary>
    /// <param name="error">The exception a route raised.</param>
    /// <returns>The status and body, or null for an unexpected failure.</returns>
    public static (int Status, ErrorBody Body)? ForApiException(Exception error) => error switch
    {
        FieldValidationException fields => (400, new ErrorBody("ValidationError", null, fields.Fields)),
        ValidationException validation => (400, Of("ValidationError", validation)),
        ConflictException conflict => (409, Of("ConflictError", conflict)),

        // A ForbiddenException with its own code: its remedy is a fresh
        // sign-in, not a permission (ADR 0024).
        ReauthenticationRequiredException reauthenticate => (403, Of("ReauthenticationRequired", reauthenticate)),
        ForbiddenException forbidden => (403, Of("ForbiddenError", forbidden)),
        NotFoundException notFound => (404, Of("NotFoundError", notFound)),

        // The wire code is the error's class name, ending in Error where
        // these classes end in Exception.
        ApiException other => (400, Of(ErrorCode(other), other)),
        _ => null,
    };

    /// <summary>The body for a refusal the framework makes, or null when this host never makes it.</summary>
    /// <param name="status">An HTTP status code.</param>
    /// <returns>The body.</returns>
    public static ErrorBody? ForStatus(int status) => HttpErrors.GetValueOrDefault(status);

    /// <summary>Writes <paramref name="body"/> with <paramref name="status"/>.</summary>
    /// <param name="context">The request.</param>
    /// <param name="status">The status.</param>
    /// <param name="body">The body.</param>
    /// <returns>A task that completes when the body is written.</returns>
    public static Task WriteAsync(HttpContext context, int status, ErrorBody body)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(body, WireJson.Options, context.RequestAborted);
    }

    /// <summary>
    /// The exception's message, and the empty string for one built with none
    /// (where .NET substitutes "Exception of type … was thrown.").
    /// </summary>
    /// <param name="error">The exception.</param>
    /// <returns>Its message as the wire carries it.</returns>
    public static string MessageOf(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.Message == $"Exception of type '{error.GetType().FullName}' was thrown." ? string.Empty : error.Message;
    }

    private static ErrorBody Of(string code, Exception error) => new(code, MessageOf(error), null);

    private static string ErrorCode(Exception error)
    {
        var name = error.GetType().Name;
        return name.EndsWith("Exception", StringComparison.Ordinal) ? name[..^"Exception".Length] + "Error" : name;
    }
}
