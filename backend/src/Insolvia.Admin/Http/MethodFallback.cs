using Microsoft.AspNetCore.Routing.Template;

namespace Insolvia.Admin.Http;

/// <summary>
/// What Flask's router does for free and ASP.NET Core's does not, so the two
/// backends answer the same outside the routes themselves:
/// <list type="bullet">
/// <item><c>HEAD</c> on a <c>GET</c> route is that route, without the body.</item>
/// <item><c>OPTIONS</c> on any existing path is a 200 with an empty body and an
/// <c>Allow</c> header — and runs NO view, so a preflight never meets an auth
/// filter (it gets its CORS headers from <see cref="Cors"/> like any response).</item>
/// <item>A path no route has is Werkzeug's HTML 404; a method the path does not
/// take is Werkzeug's HTML 405 with <c>Allow</c>.</item>
/// </list>
/// </summary>
public static class MethodFallback
{
    /// <summary>Before routing: serve a HEAD as the GET it mirrors.</summary>
    /// <param name="app">The application.</param>
    /// <returns>The same application.</returns>
    public static IApplicationBuilder UseHeadAsGet(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use((context, next) =>
        {
            if (HttpMethods.IsHead(context.Request.Method))
            {
                context.Request.Method = HttpMethods.Get;
                context.Response.Body = Stream.Null;
            }

            return next(context);
        });
    }

    /// <summary>After routing: answer the requests no route claimed.</summary>
    /// <param name="app">The application.</param>
    /// <returns>The same application.</returns>
    public static IApplicationBuilder UseMethodFallback(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var endpoints = app.ApplicationServices.GetRequiredService<EndpointDataSource>();
        return app.Use((context, next) =>
        {
            var endpoint = context.GetEndpoint();
            if (endpoint is RouteEndpoint)
            {
                return next(context);
            }

            if (endpoint is null)
            {
                return ErrorResponses.WriteHtmlAsync(context, StatusCodes.Status404NotFound);
            }

            // Routing's own 405 endpoint: the path exists under other methods.
            var allowed = AllowedMethods(endpoints, context.Request.Path);
            if (allowed.Count == 0)
            {
                return next(context);
            }

            context.Response.Headers.Allow = string.Join(", ", allowed);
            if (HttpMethods.IsOptions(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "text/html; charset=utf-8";
                return Task.CompletedTask;
            }

            return ErrorResponses.WriteHtmlAsync(context, StatusCodes.Status405MethodNotAllowed);
        });
    }

    /// <summary>
    /// Every method some route takes at <paramref name="path"/>, plus Flask's
    /// implicit ones: <c>HEAD</c> beside a <c>GET</c>, and <c>OPTIONS</c> always.
    /// </summary>
    /// <param name="endpoints">The application's endpoints.</param>
    /// <param name="path">The request path.</param>
    /// <returns>The methods, implicit ones first, as Flask listed them.</returns>
    public static IReadOnlyList<string> AllowedMethods(EndpointDataSource endpoints, PathString path)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var methods = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var route in endpoints.Endpoints.OfType<RouteEndpoint>())
        {
            var matcher = new TemplateMatcher(new RouteTemplate(route.RoutePattern), new RouteValueDictionary(route.RoutePattern.Defaults));
            if (!matcher.TryMatch(path, new RouteValueDictionary()))
            {
                continue;
            }

            foreach (var method in route.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [])
            {
                methods.Add(method);
            }
        }

        if (methods.Count == 0)
        {
            return [];
        }

        List<string> implicitMethods = methods.Contains(HttpMethods.Get) ? [HttpMethods.Head, HttpMethods.Options] : [HttpMethods.Options];
        return [.. implicitMethods, .. methods.Where(method => !implicitMethods.Contains(method, StringComparer.Ordinal))];
    }
}
