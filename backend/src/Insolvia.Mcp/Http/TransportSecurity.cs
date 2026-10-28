using System.Text.Json;
using Insolvia.Mcp.Configuration;
using Insolvia.Mcp.Logging;

namespace Insolvia.Mcp.Http;

/// <summary>
/// The Python SDK's transport checks, in its order, in front of the C# SDK —
/// which has none of them (research 2026-10-27 § 2.4). They run AFTER
/// authentication, as Python's did (its auth middleware wraps the transport),
/// so an unauthenticated request is the 401 challenge whatever else is wrong.
/// </summary>
/// <list type="number">
/// <item><b>Host</b> — the spec's DNS-rebinding MUST: a deployed environment
/// answers exactly its own hostname; local also loopback on any port. 421
/// <c>Invalid Host header</c> (ASP.NET's HostFiltering would answer 400, a
/// wire difference).</item>
/// <item><b>Origin</b> — absent is fine (harnesses are not browsers);
/// PRESENT is refused, 403 <c>Invalid Origin header</c>: the Python server
/// was configured with an empty allowlist, so even its own origin is refused.</item>
/// <item><b>Accept</b> — a POST must accept <c>application/json</c> (406):
/// the server answers JSON, never a stream.</item>
/// <item><b>Content-Type</b> — a POST must be <c>application/json</c>
/// (400 <c>Invalid Content-Type header</c>).</item>
/// <item><b>Parse</b> — a body that is not JSON is JSON-RPC's <c>-32700</c>
/// (the C# SDK would say <c>-32600</c>).</item>
/// <item><b>Size</b> — a body over 256 KiB (mcp-surface.md § Limits, the
/// debtor route's ceiling) is 413 <c>Request body too large</c> — counted
/// here, not left to Kestrel, because under Lambda there is no Kestrel.</item>
/// </list>
public sealed class TransportSecurity
{
    /// <summary>mcp-surface.md § Limits: request body ≤ 256 KiB.</summary>
    public const int MaxRequestBytes = 256 * 1024;

    private readonly RequestDelegate _next;
    private readonly IReadOnlyList<string> _allowedHosts;
    private readonly ILogger _log;

    /// <summary>Creates the middleware.</summary>
    /// <param name="next">The rest of the pipeline.</param>
    /// <param name="config">The configuration (the resource URL names the host).</param>
    /// <param name="loggerFactory">The log.</param>
    public TransportSecurity(RequestDelegate next, McpConfig config, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _next = next;
        _allowedHosts = AllowedHosts(config);
        _log = loggerFactory.CreateLogger(StructuredLog.Categories.Transport);
    }

    /// <summary>
    /// The Host values this environment answers: the resource URL's own
    /// authority, and — locally only — loopback on whatever port dev-up chose.
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <returns>Exact values, and <c>name:*</c> patterns.</returns>
    public static IReadOnlyList<string> AllowedHosts(McpConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var resource = new Uri(config.ResourceUrl);
        var authority = resource.IsDefaultPort ? resource.Host : $"{resource.Host}:{resource.Port}";
        List<string> hosts = [authority];
        if (config.Environment == "local")
        {
            hosts.AddRange(["127.0.0.1:*", "localhost:*", "127.0.0.1", "localhost"]);
        }

        return hosts;
    }

    /// <summary>Runs the checks, then the transport.</summary>
    /// <param name="context">The request.</param>
    /// <returns>A task.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.Request;
        if (!_allowedHosts.Any(pattern => HostMatches(pattern, request.Host.Value ?? string.Empty)))
        {
            _log.Info("transport refused", ("reason", "invalid_host"));
            await Refuse(context, StatusCodes.Status421MisdirectedRequest, "Invalid Host header").ConfigureAwait(false);
            return;
        }

        if (request.Headers.Origin.Count > 0)
        {
            _log.Info("transport refused", ("reason", "invalid_origin"));
            await Refuse(context, StatusCodes.Status403Forbidden, "Invalid Origin header").ConfigureAwait(false);
            return;
        }

        if (HttpMethods.IsPost(request.Method))
        {
            if (!AcceptsJson(request.Headers.Accept.ToString()))
            {
                context.Response.StatusCode = StatusCodes.Status406NotAcceptable;
                return;
            }

            if (!(request.ContentType ?? string.Empty).StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            {
                await Refuse(context, StatusCodes.Status400BadRequest, "Invalid Content-Type header").ConfigureAwait(false);
                return;
            }

            if (request.ContentLength > MaxRequestBytes)
            {
                await Refuse(context, StatusCodes.Status413PayloadTooLarge, "Request body too large").ConfigureAwait(false);
                return;
            }

            // Counted, not trusted: a chunked body has no Content-Length.
            var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await request.Body.ReadAsync(chunk, context.RequestAborted).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxRequestBytes)
                {
                    await Refuse(context, StatusCodes.Status413PayloadTooLarge, "Request body too large").ConfigureAwait(false);
                    return;
                }
            }

            // Python's transport answered a body that is not JSON with
            // JSON-RPC's own parse error; the C# SDK says -32600 instead.
            if (!IsJson(buffer))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{\"code\":-32700,\"message\":\"Parse error\"}}", context.RequestAborted).ConfigureAwait(false);
                return;
            }

            buffer.Position = 0;
            request.Body = buffer;
            request.ContentLength = buffer.Length;

            // The C# SDK insists on both media types; we answer JSON either way
            // (JsonResponseShim), so a JSON-only client is served, as Python served it.
            request.Headers.Accept = "application/json, text/event-stream";
        }

        await _next(context).ConfigureAwait(false);
    }

    private static bool IsJson(MemoryStream buffer)
    {
        try
        {
            using var document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HostMatches(string pattern, string host) =>
        pattern.EndsWith(":*", StringComparison.Ordinal)
            ? host.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(host, pattern, StringComparison.OrdinalIgnoreCase);

    private static bool AcceptsJson(string accept) =>
        accept.Split(',').Select(part => part.Split(';')[0].Trim()).Any(media =>
            media.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || media.Equals("application/*", StringComparison.OrdinalIgnoreCase)
            || media == "*/*");

    private static Task Refuse(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsync(message, context.RequestAborted);
    }
}
