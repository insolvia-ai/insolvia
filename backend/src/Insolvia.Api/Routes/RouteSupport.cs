using System.Text.Json.Nodes;
using Insolvia.Api.Http;
using Insolvia.Api.Logging;
using Insolvia.Core.Ports;

namespace Insolvia.Api.Routes;

/// <summary>
/// What every route module's private helpers did in Python (<c>_firm_store()</c>,
/// <c>_json_body()</c>, its module logger), written once.
/// </summary>
/// <remarks>
/// A handler takes <see cref="CancellationToken"/> beside <see cref="HttpContext"/>
/// on purpose: a handler shaped exactly <c>HttpContext → Task</c> binds as a raw
/// <c>RequestDelegate</c>, and a raw delegate silently skips the endpoint
/// filters — <c>RequireAuth</c> among them.
/// </remarks>
internal static class RouteSupport
{
    /// <summary>
    /// The firm store, or a 500 — a composition bug, never "no firm":
    /// a deployment that cannot read firms must not look like an empty one.
    /// </summary>
    public static IFirmStore FirmStore(this HttpContext context) =>
        context.RequestServices.GetService<IFirmStore>() ?? throw new InvalidOperationException("firm store is not composed");

    /// <summary>The access log, or a 500.</summary>
    public static IAccessLog AccessLog(this HttpContext context) =>
        context.RequestServices.GetService<IAccessLog>() ?? throw new InvalidOperationException("access log is not composed");

    /// <summary>The server clock (the system clock unless a test pinned one).</summary>
    public static TimeProvider Clock(this HttpContext context) =>
        context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;

    /// <summary>The request body as a JSON object, refused over <paramref name="maxBytes"/>.</summary>
    public static Task<JsonObject> BodyAsync(this HttpContext context, int maxBytes) => RequestBodies.ReadObjectAsync(context, maxBytes);

    /// <summary>One metadata log line under the module's logger name — never a body, a name or an address.</summary>
    public static void Log(this HttpContext context, string category, string message, params (string Key, object? Value)[] fields) =>
        context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(category).Info(message, fields);
}
