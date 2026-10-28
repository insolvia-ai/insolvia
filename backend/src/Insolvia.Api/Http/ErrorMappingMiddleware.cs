using Insolvia.Api.Logging;

namespace Insolvia.Api.Http;

/// <summary>
/// app_factory.py's error handlers: a Core exception becomes its status and
/// JSON body (<see cref="ErrorResponses.ForApiException"/>); an HTTP-level
/// refusal from the framework (an unreadable body) becomes Werkzeug's HTML,
/// as Flask's HTTPException passed through; ANYTHING else is logged with its
/// stack and answered with the one 500 body that names nothing.
/// </summary>
public sealed class ErrorMappingMiddleware(RequestDelegate next, ILoggerFactory loggers)
{
    private readonly ILogger _logger = loggers.CreateLogger(StructuredLog.Categories.AppFactory);

    /// <summary>Runs the rest of the pipeline under the handlers.</summary>
    /// <param name="context">The request.</param>
    /// <returns>A task that completes with the response.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (Exception error) when (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
        {
            context.Response.Clear();
            if (ErrorResponses.ForApiException(error) is { } mapped)
            {
                await ErrorResponses.WriteJsonAsync(context, mapped.Status, mapped.Body).ConfigureAwait(false);
                return;
            }

            if (error is BadHttpRequestException badRequest && ErrorResponses.WerkzeugHtml(badRequest.StatusCode) is not null)
            {
                await ErrorResponses.WriteHtmlAsync(context, badRequest.StatusCode).ConfigureAwait(false);
                return;
            }

            _logger.Write(LogLevel.Error, "unexpected Insolvia API failure", error);
            await ErrorResponses.WriteJsonAsync(context, StatusCodes.Status500InternalServerError, ErrorResponses.InternalErrorJson).ConfigureAwait(false);
        }
    }
}
