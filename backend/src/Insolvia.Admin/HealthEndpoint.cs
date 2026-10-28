namespace Insolvia.Admin;

/// <summary>
/// <c>GET /health</c>, deliberately public: the deploy workflow's smoke check
/// curls it and asserts the environment before any staff token exists in CI
/// (services/admin routes/health.py). The body is that route's — <c>status</c>
/// and <c>environment</c> only, in Flask's sorted key order.
/// </summary>
public static class HealthEndpoint
{
    /// <summary>Maps <c>GET /health</c>.</summary>
    /// <param name="app">The route builder to map onto.</param>
    /// <returns>The same route builder.</returns>
    public static IEndpointRouteBuilder MapHealth(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/health", (InsolviaEnvironment environment) => Results.Json(
            new HealthBody(environment.Name, "ok"),
            contentType: "application/json"));
        return app;
    }

    /// <summary>The <c>/health</c> body, in Flask's sorted key order.</summary>
    /// <param name="Environment">The configured environment.</param>
    /// <param name="Status">Always <c>ok</c>.</param>
    public sealed record HealthBody(string Environment, string Status);
}
