namespace Insolvia.Api;

/// <summary>
/// <c>GET /health</c>: the service's identity, unauthenticated.
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

        app.MapGet("/health", Health);

        return app;
    }

    /// <summary>Liveness, and which build and environment answered.</summary>
    /// <param name="environment">The configured environment.</param>
    /// <returns>The body.</returns>
    internal static HealthBody Health(InsolviaEnvironment environment) => new(environment.Name, ServiceName, "ok", Version);

    /// <summary>The <c>/health</c> body.</summary>
    /// <param name="Environment">The configured environment.</param>
    /// <param name="Service">The service name.</param>
    /// <param name="Status">Always <c>ok</c>.</param>
    /// <param name="Version">The service version.</param>
    public sealed record HealthBody(string Environment, string Service, string Status, string Version);
}
