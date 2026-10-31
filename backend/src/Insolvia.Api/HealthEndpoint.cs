namespace Insolvia.Api;

/// <summary>
/// <c>GET /health</c>: the service's identity, unauthenticated. The body and
/// status are the contract's, field for field (ported from services/api
/// routes/health.py).
/// </summary>
public static class HealthEndpoint
{
    /// <summary>The name the service reports; the Python API's <c>SERVICE_NAME</c>.</summary>
    public const string ServiceName = "insolvia-api";

    /// <summary>The version the service reports; the Python API's <c>__version__</c>.</summary>
    public const string Version = "0.1.0";

    /// <summary>Maps <c>GET /health</c>.</summary>
    /// <param name="app">The route builder to map onto.</param>
    /// <returns>The same route builder.</returns>
    public static IEndpointRouteBuilder MapHealth(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Flask's jsonify answers a bare `application/json` with the keys
        // sorted; both are matched, so the two backends are byte-comparable
        // apart from Flask's trailing newline.
        app.MapGet("/health", (InsolviaEnvironment environment) => Results.Json(
            new HealthBody(environment.Name, ServiceName, "ok", Version),
            contentType: "application/json"));

        return app;
    }

    /// <summary>The <c>/health</c> body, in Flask's sorted key order.</summary>
    /// <param name="Environment">The configured environment.</param>
    /// <param name="Service">The service name.</param>
    /// <param name="Status">Always <c>ok</c>.</param>
    /// <param name="Version">The service version.</param>
    public sealed record HealthBody(string Environment, string Service, string Status, string Version);
}
