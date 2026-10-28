using Insolvia.Api.Configuration;

namespace Insolvia.Api.Http;

/// <summary>
/// The exact-origin CORS allowlist (issue #68; services/api core/cors.py and
/// app_factory.py's <c>finalize_response</c>). Deliberately NOT ASP.NET's CORS
/// middleware: Flask's rule is simpler and stricter, and the two must answer
/// identically — a matched <c>Origin</c> is echoed exactly on EVERY response
/// (preflight, 401, 404, 500 alike) with the fixed method and header lists;
/// anything else, a missing <c>Origin</c> included, gets no
/// <c>Access-Control-*</c> header at all. Never a wildcard.
/// </summary>
public static class Cors
{
    /// <summary>
    /// Every method a browser may send. A non-simple method missing here is
    /// the request never being sent — adding a route without adding its
    /// method is a two-file change that looks like a one-file change.
    /// </summary>
    public const string AllowMethods = "GET, POST, PATCH, PUT, DELETE, OPTIONS";

    /// <summary>The request headers a browser may send.</summary>
    public const string AllowHeaders = "Content-Type, Authorization";

    /// <summary>How long a browser may cache a preflight, in seconds.</summary>
    public const string MaxAge = "600";

    // Loopback hosts a browser dev server may serve from, on any port.
    private static readonly HashSet<string> LocalhostNames = new(StringComparer.Ordinal) { "localhost", "127.0.0.1", "::1" };

    /// <summary>
    /// Whether a browser at <paramref name="origin"/> may read this API's
    /// responses: one of the environment's exact origins, or — outside
    /// production — an http(s) loopback origin with no path, query or fragment.
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <param name="origin">The request's <c>Origin</c> header.</param>
    /// <returns>Whether it is allowed.</returns>
    public static bool OriginAllowed(ApiConfig config, string origin)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(origin);
        if (config.CorsAllowedOrigins.Contains(origin, StringComparer.Ordinal))
        {
            return true;
        }

        if (!config.CorsAllowLocalhost)
        {
            return false;
        }

        return UrlSplit.TryParse(origin, out var parts)
            && parts.Scheme is "http" or "https"
            && parts.Hostname is not null
            && LocalhostNames.Contains(parts.Hostname)
            && parts.Path.Length == 0
            && parts.Query.Length == 0
            && parts.Fragment.Length == 0;
    }

    /// <summary>Adds the CORS headers to every response as it starts (Flask's <c>after_request</c>).</summary>
    /// <param name="app">The application.</param>
    /// <returns>The same application.</returns>
    public static IApplicationBuilder UseInsolviaCors(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use((context, next) =>
        {
            var config = context.RequestServices.GetRequiredService<ApiConfig>();
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers.Append("Vary", "Origin");

                // Flask's headers.get: the first value when the header repeats.
                var origin = context.Request.Headers.Origin.Count > 0 ? context.Request.Headers.Origin[0] : null;
                if (!string.IsNullOrEmpty(origin) && OriginAllowed(config, origin))
                {
                    headers.AccessControlAllowOrigin = origin;
                    headers.AccessControlAllowMethods = AllowMethods;
                    headers.AccessControlAllowHeaders = AllowHeaders;
                    headers.AccessControlMaxAge = MaxAge;
                }

                return Task.CompletedTask;
            });
            return next(context);
        });
    }
}

/// <summary>
/// Python's <c>urllib.parse.urlsplit</c>, as far as <see cref="Cors.OriginAllowed"/>
/// reads it: the scheme (lower-cased), the hostname (lower-cased, IPv6
/// brackets removed), and whether a path, query or fragment follows.
/// </summary>
internal readonly record struct UrlSplit(string Scheme, string? Hostname, string Path, string Query, string Fragment)
{
    public static bool TryParse(string url, out UrlSplit parts)
    {
        parts = default;
        var colon = url.IndexOf(':', StringComparison.Ordinal);

        // urlsplit's scheme: a letter, then letters, digits, + - . — else no scheme.
        if (colon <= 0 || !char.IsAsciiLetter(url[0]) || !url[..colon].All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.'))
        {
            return false;
        }

        var scheme = url[..colon].ToLowerInvariant();
        var rest = url[(colon + 1)..];

        var fragmentAt = rest.IndexOf('#', StringComparison.Ordinal);
        var fragment = fragmentAt >= 0 ? rest[(fragmentAt + 1)..] : string.Empty;
        if (fragmentAt >= 0)
        {
            rest = rest[..fragmentAt];
        }

        var queryAt = rest.IndexOf('?', StringComparison.Ordinal);
        var query = queryAt >= 0 ? rest[(queryAt + 1)..] : string.Empty;
        if (queryAt >= 0)
        {
            rest = rest[..queryAt];
        }

        string? hostname = null;
        if (rest.StartsWith("//", StringComparison.Ordinal))
        {
            rest = rest[2..];
            var slash = rest.IndexOf('/', StringComparison.Ordinal);
            var netloc = slash >= 0 ? rest[..slash] : rest;
            rest = slash >= 0 ? rest[slash..] : string.Empty;
            hostname = HostnameOf(netloc);
        }

        parts = new UrlSplit(scheme, hostname, rest, query, fragment);
        return true;
    }

    // urlsplit's .hostname: after any userinfo, without the port, IPv6
    // brackets stripped, lower-cased; None when empty.
    private static string? HostnameOf(string netloc)
    {
        var at = netloc.LastIndexOf('@');
        var hostPort = at >= 0 ? netloc[(at + 1)..] : netloc;
        string host;
        if (hostPort.StartsWith('['))
        {
            var close = hostPort.IndexOf(']', StringComparison.Ordinal);
            host = close >= 0 ? hostPort[1..close] : hostPort[1..];
        }
        else
        {
            var portAt = hostPort.IndexOf(':', StringComparison.Ordinal);
            host = portAt >= 0 ? hostPort[..portAt] : hostPort;
        }

        return host.Length == 0 ? null : host.ToLowerInvariant();
    }
}
