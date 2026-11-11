using Insolvia.Api.Logging;

namespace Insolvia.Api.Http;

/// <summary>
/// The error handler: a domain exception becomes its status and body
/// (<see cref="ErrorResponses.ForApiException"/>); a refusal from the
/// framework (an unreadable body) becomes its own (<see cref="ErrorResponses.ForStatus"/>);
/// ANYTHING else is logged with its stack and answered with the one 500 body
/// that names nothing.
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
                await ErrorResponses.WriteAsync(context, mapped.Status, mapped.Body).ConfigureAwait(false);
                return;
            }

            if (error is BadHttpRequestException badRequest && ErrorResponses.ForBadRequest(badRequest) is { } refusal)
            {
                await ErrorResponses.WriteAsync(context, refusal.Status, refusal.Body).ConfigureAwait(false);
                return;
            }

            _logger.Write(LogLevel.Error, "unexpected Insolvia API failure", error);
            await ErrorResponses.WriteAsync(context, StatusCodes.Status500InternalServerError, ErrorResponses.InternalError).ConfigureAwait(false);
        }
    }
}
