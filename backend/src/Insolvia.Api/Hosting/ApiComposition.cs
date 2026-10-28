using Insolvia.Api.Configuration;
using Insolvia.Api.Http;
using Insolvia.Api.Logging;
using Insolvia.Api.Mail;
using Insolvia.Api.Routes;
using Insolvia.Aws.Jwks;
using Insolvia.Aws.Mailer;
using Insolvia.Core.Memory;
using Insolvia.Core.Ports;
using Insolvia.Data;
using Insolvia.Data.AccessLogging;
using Insolvia.Data.Connections;
using Insolvia.Data.Firms;
using Insolvia.Data.Portal;
using Insolvia.Data.Waitlist;
using Microsoft.Extensions.Logging.Console;

namespace Insolvia.Api.Hosting;

/// <summary>
/// What Program.cs composes, kept out of it so the composition reads as a
/// list. The two entrypoints of the Python service collapse into one binary
/// with two shapes, chosen by <see cref="LambdaBootRequirements.LambdaFunctionVariable"/>:
/// <list type="bullet">
/// <item><b>Lambda</b> (api_lambda.py): refuses to boot without its
/// requirements — the database's <c>INSOLVIA_DB_*</c> among them; composes
/// the HTTP JWKS provider and the Postgres stores (request-scoped, over one
/// <see cref="InsolviaDbContext"/> per request). Never an in-memory store: an
/// empty one would make every user look unprovisioned.</item>
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

            // Throws at composition when the INSOLVIA_DB_* contract is
            // incomplete, so a misconfigured Lambda never takes a request.
            builder.Services.AddInsolviaData(DatabaseSettings.FromEnvironment(name => configuration[name], applicationName: "insolvia-api"));
            builder.Services.AddScoped<IFirmStore>(services => new PostgresFirmStore(
                services.GetRequiredService<InsolviaDbContext>(),
                services.GetRequiredService<TimeProvider>()));
            builder.Services.AddScoped<IClientBindingStore>(services => new PostgresClientBindingStore(services.GetRequiredService<InsolviaDbContext>()));
            builder.Services.AddScoped<IAccessLog>(services => new PostgresAccessLog(services.GetRequiredService<InsolviaDbContext>()));
            builder.Services.AddScoped<IWaitlistStore>(services => new PostgresWaitlistStore(services.GetRequiredService<InsolviaDbContext>()));

            // Not composed until their adapters are ported, so the routes
            // that need them answer the 500 that names the gap — never a
            // memory twin that would accept a write and lose it:
            //   IUserDirectory (Cognito AdminCreateUser) — POST /v1/firm/users;
            //   ICourtRegistry (the case half, 18.7) — PATCH /v1/firm naming a court;
            //   IDebtorClientLinks (the debtor store, 18.11) — the client merge.
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

        // The mailer's own fallback shape (api_lambda.py and the development
        // server alike): with MAILER_API_URL, the SigV4 client — pointed at the
        // mailer's local stack on a laptop; without it, the in-memory one that
        // records and sends nothing.
        if (config.MailerApiUrl is not null)
        {
            var mailerUrl = config.MailerApiUrl;
            builder.Services.AddSingleton<IMailer>(_ => new SigV4Mailer(SigV4MailerClient.FromEnvironment(new HttpClient(), mailerUrl, name => configuration[name])));
        }
        else
        {
            builder.Services.AddSingleton<IMailer, MemoryMailer>();
        }

        if (!isLambda)
        {
            builder.Services.AddSingleton<IFirmStore>(services => new MemoryFirmStore(services.GetRequiredService<TimeProvider>()));
            builder.Services.AddSingleton<IClientBindingStore, MemoryClientBindingStore>();
            builder.Services.AddSingleton<IAccessLog, MemoryAccessLog>();
            builder.Services.AddSingleton<IWaitlistStore, MemoryWaitlistStore>();

            // Mints a subject where Cognito would; sends no invitation.
            builder.Services.AddSingleton<IUserDirectory, MemoryUserDirectory>();
        }

        // The firm-side routes (18.14), mapped after /health in this order.
        builder.Services.AddSingleton<IEndpointModule, MeRoutes>();
        builder.Services.AddSingleton<IEndpointModule, FirmRoutes>();
        builder.Services.AddSingleton<IEndpointModule, FirmClientRoutes>();
        builder.Services.AddSingleton<IEndpointModule, LibraryCreditorRoutes>();
        builder.Services.AddSingleton<IEndpointModule, FirmQuestionnaireRoutes>();
        builder.Services.AddSingleton<IEndpointModule, DocumentChecklistRoutes>();
        builder.Services.AddSingleton<IEndpointModule, WaitlistRoutes>();
        builder.Services.AddSingleton<IEndpointModule, UnsubscribeRoutes>();

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
