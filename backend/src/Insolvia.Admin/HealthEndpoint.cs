namespace Insolvia.Admin;

/// <summary>
/// <c>GET /health</c>, deliberately public: the deploy workflow's smoke check
/// curls it and asserts the environment before any staff token exists in CI.
/// </summary>
public static class HealthEndpoint
{
    /// <summary>Maps <c>GET /health</c>.</summary>
    /// <param name="app">The route builder to map onto.</param>
    /// <returns>The same route builder.</returns>
    public static IEndpointRouteBuilder MapHealth(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/health", Health);
        return app;
    }

    /// <summary>Liveness, and which environment answered.</summary>
    /// <param name="environment">The configured environment.</param>
    /// <returns>The body.</returns>
    internal static HealthBody Health(InsolviaEnvironment environment) => new(environment.Name, "ok");

    /// <summary>The <c>/health</c> body.</summary>
    /// <param name="Environment">The configured environment.</param>
    /// <param name="Status">Always <c>ok</c>.</param>
    public sealed record HealthBody(string Environment, string Status);
}
