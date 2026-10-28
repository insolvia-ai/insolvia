using Insolvia.Api.Configuration;
using Insolvia.Api.Http;
using Insolvia.Api.Logging;
using Insolvia.Aws.Jwks;
using Insolvia.Core.Memory;
using Insolvia.Core.Ports;
using Microsoft.Extensions.Logging.Console;

namespace Insolvia.Api.Hosting;

/// <summary>
/// What Program.cs composes, kept out of it so the composition reads as a
/// list. The two entrypoints of the Python service collapse into one binary
/// with two shapes, chosen by <see cref="LambdaBootRequirements.LambdaFunctionVariable"/>:
/// <list type="bullet">
/// <item><b>Lambda</b> (api_lambda.py): refuses to boot without its
/// requirements; composes the HTTP JWKS provider; composes NO in-memory store
/// — until the Postgres stores land, an authenticated route there is a 500
/// ("not composed"), never an empty store that makes every user look
/// unprovisioned.</item>
/// <item><b>Everything else</b> — <c>dotnet run</c>, docker compose, the test
/// host — (development_server.py): auth is optional and fails CLOSED when
/// absent; the stores are empty in-memory ones, so a signed-in developer with
/// no firm is exactly the "authenticated, not provisioned" case.</item>
/// </list>
/// </summary>
public static class ApiComposition
{
    /// <summary>Registers configuration, logging and the ports.</summary>
    /// <param name="builder">The application builder.</param>
    /// <returns>The same builder.</returns>
    public static WebApplicationBuilder AddInsolviaApi(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var configuration = builder.Configuration;
        var isLambda = !string.IsNullOrEmpty(configuration[LambdaBootRequirements.LambdaFunctionVariable]);

        // One JSON line per record, and nothing from the framework below a
        // warning: ASP.NET Core's own request lines carry the query string,
        // which the GLBA rule keeps out of every log.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.FormatterName = JsonLogFormatter.FormatterName);
        builder.Logging.AddConsoleFormatter<JsonLogFormatter, ConsoleFormatterOptions>();
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

        // Read through IConfiguration (environment variables are one of its
        // sources), so a test host can set any of them with UseSetting.
        builder.Services.AddSingleton(_ => ApiConfig.Load(name => configuration[name]));
        builder.Services.AddSingleton(services => services.GetRequiredService<ApiConfig>().Environment);
        builder.Services.AddSingleton(TimeProvider.System);

        var config = ApiConfig.Load(name => configuration[name]);
        if (isLambda)
        {
            LambdaBootRequirements.Enforce(config);
        }

        // Constructed once, at start, so its key cache lives as long as the
        // process; nothing is fetched until the first token arrives, so an
        // unauthenticated path never touches the network. Without the pair,
        // no provider at all: every protected route answers 401.
        if (config.AuthIssuerUrl is not null && config.AuthClientId is not null)
        {
            var issuer = config.AuthIssuerUrl;
            builder.Services.AddSingleton<IJwksProvider>(services => new HttpJwksProvider(
                new HttpClient(),
                HttpJwksProvider.ForIssuer(issuer),
                clock: services.GetRequiredService<TimeProvider>()));
        }

        if (!isLambda)
        {
            builder.Services.AddSingleton<IFirmStore>(services => new MemoryFirmStore(services.GetRequiredService<TimeProvider>()));
            builder.Services.AddSingleton<IClientBindingStore, MemoryClientBindingStore>();
            builder.Services.AddSingleton<IAccessLog, MemoryAccessLog>();
        }

        return builder;
    }

    /// <summary>
    /// The pipeline, outermost first: the request log (so every response,
    /// a failed one included, is one line), CORS (on every response), the
    /// error handlers, HEAD-as-GET, routing, then the 404/405/OPTIONS
    /// fallback and the endpoints.
    /// </summary>
    /// <param name="app">The application.</param>
    /// <returns>The same application.</returns>
    public static WebApplication UseInsolviaApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Resolve once now: a misspelt INSOLVIA_ENV stops the process at
        // start, not on the first request.
        _ = app.Services.GetRequiredService<ApiConfig>();

        app.UseRequestLog();
        app.UseInsolviaCors();
        app.UseMiddleware<ErrorMappingMiddleware>();
        app.UseHeadAsGet();
        app.UseRouting();
        app.UseMethodFallback();

        app.MapHealth();
        foreach (var module in app.Services.GetServices<IEndpointModule>())
        {
            module.Map(app);
        }

        return app;
    }
}
