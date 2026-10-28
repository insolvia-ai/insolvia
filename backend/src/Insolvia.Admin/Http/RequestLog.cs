using System.Diagnostics;
using Insolvia.Admin.Logging;

namespace Insolvia.Admin.Http;

/// <summary>
/// Exactly one JSON line per request (issue #69; app_factory.py's
/// <c>finalize_response</c>): <c>method</c>, <c>path</c>, <c>status</c>,
/// <c>duration_ms</c>. Request METADATA only — never a body, a query string,
/// a header or a token: this API carries GLBA-protected financial data, and a
/// waitlist submission's field values are PII.
/// </summary>
public static class RequestLog
{
    /// <summary>The line's message.</summary>
    public const string Message = "request handled";

    /// <summary>
    /// Logs every request once it has completed — including one that threw,
    /// so a failure is never the request that left no line.
    /// </summary>
    /// <param name="app">The application.</param>
    /// <returns>The same application.</returns>
    public static IApplicationBuilder UseRequestLog(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger(StructuredLog.Categories.Request);
        return app.Use(async (context, next) =>
        {
            // Read before the rest of the pipeline can rewrite it (a HEAD is
            // served as a GET).
            var method = context.Request.Method;
            var path = context.Request.PathBase.Add(context.Request.Path).Value ?? string.Empty;
            var started = Stopwatch.GetTimestamp();
            try
            {
                await next(context).ConfigureAwait(false);
            }
            finally
            {
                var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                logger.Info(
                    Message,
                    ("method", method),
                    ("path", path),
                    ("status", context.Response.StatusCode),
                    ("duration_ms", Math.Round(elapsed, 1)));
            }
        });
    }
}
