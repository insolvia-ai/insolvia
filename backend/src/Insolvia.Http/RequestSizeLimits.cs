using Insolvia.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;

namespace Insolvia.Http;

/// <summary>
/// A route's declared byte cap (<c>.WithMetadata(new RequestSizeLimitAttribute(n))</c>)
/// is enforced here, before the body is read: only Kestrel honours that
/// metadata, and the Lambda host and the test server do not. A body over it
/// is a 400 <c>ValidationError</c>, "request body exceeds N KiB".
/// </summary>
public static class RequestSizeLimits
{
    /// <summary>After routing: refuse a body larger than its route allows.</summary>
    /// <param name="app">The application.</param>
    /// <returns>The same application.</returns>
    public static IApplicationBuilder UseRequestSizeLimits(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use((context, next) =>
        {
            // shortcut: checks Content-Length only, as the old reader did; API Gateway always sends it.
            if (context.GetEndpoint()?.Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize is { } max
                && context.Request.ContentLength > max)
            {
                throw new ValidationException($"request body exceeds {max / 1024} KiB");
            }

            return next(context);
        });
    }
}
