using System.Text.Json.Nodes;
using Insolvia.Aws.Jwks;
using Insolvia.Core.Memory;
using Insolvia.Core.Ports;
using Insolvia.Data;
using Insolvia.Data.AccessLogging;
using Insolvia.Data.Cases;
using Insolvia.Data.Connections;
using Insolvia.Data.Firms;
using Insolvia.Data.Pagination;
using Insolvia.Mcp.Auth;
using Insolvia.Mcp.Configuration;
using Insolvia.Mcp.Http;
using Insolvia.Mcp.Logging;
using Insolvia.Mcp.Tools;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging.Console;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;

namespace Insolvia.Mcp.Hosting;

/// <summary>
/// What Program.cs composes. The Python service's two entrypoints collapse
/// into one binary with two shapes, chosen by
/// <see cref="LambdaBootRequirements.LambdaFunctionVariable"/>:
/// <list type="bullet">
/// <item><b>Lambda</b> (mcp_lambda.py): refuses to boot without the Cognito
/// pair and the database's <c>INSOLVIA_DB_*</c>; composes the HTTP JWKS
/// provider and the Postgres stores as the <c>mcp</c> role (request-scoped,
/// one <see cref="InsolviaDbContext"/> per request). Never a memory twin.</item>
/// <item><b>Everything else</b> (development_server.py): with
/// <c>INSOLVIA_DB_CONNECTION_STRING</c> set, the same Postgres stores against
/// this machine's database (no emulator — the dev database IS the database);
/// without it, the memory twins, wholesale — a real case store beside a
/// memory candidate store is a half-state nobody can debug. The memory firm
/// store starts empty: a signed-in developer with no firm is exactly the
/// case whoami exists to report. Auth is optional and fails CLOSED.</item>
/// </list>
/// </summary>
public static class McpComposition
{
    /// <summary>The endpoint's path.</summary>
    public const string EndpointPath = "/mcp";

    /// <summary>Registers configuration, logging, the ports, auth and the MCP server.</summary>
    /// <param name="builder">The application builder.</param>
    /// <returns>The same builder.</returns>
    public static WebApplicationBuilder AddInsolviaMcp(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var configuration = builder.Configuration;
        var isLambda = !string.IsNullOrEmpty(configuration[LambdaBootRequirements.LambdaFunctionVariable]);

        // One JSON line per record; nothing from the framework below a
        // warning (its request lines carry the query string).
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.FormatterName = JsonLogFormatter.FormatterName);
        builder.Logging.AddConsoleFormatter<JsonLogFormatter, ConsoleFormatterOptions>();
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddFilter("ModelContextProtocol", LogLevel.Warning);

        // Kestrel's own ceiling matches TransportSecurity's count (local only;
        // under Lambda there is no Kestrel, and the middleware is the limit).
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = TransportSecurity.MaxRequestBytes + 1);

        // Read through IConfiguration so a test host can set any variable with UseSetting.
        var config = McpConfig.Load(name => configuration[name]);
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(TimeProvider.System);

        var database = configuration[DatabaseSettings.ConnectionStringVariable];
        if (isLambda)
        {
            LambdaBootRequirements.Enforce(config);
        }

        if (isLambda || !string.IsNullOrEmpty(database))
        {
            // Throws at composition when the INSOLVIA_DB_* contract is
            // incomplete, so a misconfigured Lambda never takes a request.
            // The mcp role's grants (Migrator/Grants.cs) are SELECT on the
            // case and firm tables, read/insert/update on candidates, and
            // append-only on the access log: a case-record write would be
            // refused by the database even if this host had a path to one.
            builder.Services.AddInsolviaData(DatabaseSettings.FromEnvironment(name => configuration[name], applicationName: "insolvia-mcp"));
            builder.Services.AddSingleton(_ => CursorCodec.FromEnvironment(name => configuration[name]));
            builder.Services.AddScoped<IFirmStore>(services => new PostgresFirmStore(services.GetRequiredService<InsolviaDbContext>(), services.GetRequiredService<TimeProvider>()));
            builder.Services.AddScoped<IAccessLog>(services => new PostgresAccessLog(services.GetRequiredService<InsolviaDbContext>()));
            builder.Services.AddScoped<ICaseStore>(services => new PostgresCaseStore(services.GetRequiredService<InsolviaDbContext>(), services.GetRequiredService<CursorCodec>()));
            builder.Services.AddScoped<IDebtorStore>(services => new PostgresDebtorStore(services.GetRequiredService<InsolviaDbContext>()));
            builder.Services.AddScoped<ICaseEntityStore>(services => new PostgresCaseEntityStore(services.GetRequiredService<InsolviaDbContext>()));
            builder.Services.AddScoped<IDocumentStore>(services => new PostgresDocumentStore(services.GetRequiredService<InsolviaDbContext>()));
            builder.Services.AddScoped<ICandidateStore>(services => new PostgresCandidateStore(services.GetRequiredService<InsolviaDbContext>()));
        }
        else
        {
            builder.Services.AddSingleton<IFirmStore>(services => new MemoryFirmStore(services.GetRequiredService<TimeProvider>()));
            builder.Services.AddSingleton<IAccessLog, MemoryAccessLog>();

            // Wired as the real stores share one database: the case store
            // writes debtors into the debtor store, which reads its client
            // conditions from the firm store.
            builder.Services.AddSingleton(services => new MemoryDebtorStore(services.GetRequiredService<IFirmStore>()));
            builder.Services.AddSingleton<IDebtorStore>(services => services.GetRequiredService<MemoryDebtorStore>());
            builder.Services.AddSingleton(services => new MemoryCaseStore(services.GetRequiredService<MemoryDebtorStore>()));
            builder.Services.AddSingleton<ICaseStore>(services => services.GetRequiredService<MemoryCaseStore>());
            builder.Services.AddSingleton<ICaseEntityStore, MemoryCaseEntityStore>();
            builder.Services.AddSingleton<IDocumentStore, MemoryDocumentStore>();
            builder.Services.AddSingleton<ICandidateStore, MemoryCandidateStore>();
        }

        builder.Services.AddScoped(services => new CaseTools(
            services.GetRequiredService<ICaseStore>(),
            services.GetRequiredService<ICaseEntityStore>(),
            services.GetRequiredService<IDebtorStore>(),
            services.GetRequiredService<IDocumentStore>(),
            services.GetRequiredService<ICandidateStore>(),
            services.GetRequiredService<IAccessLog>(),
            services.GetRequiredService<IFirmStore>()));

        // Constructed once, at start, so its key cache lives as long as the
        // process; nothing is fetched until the first token. Without the pair,
        // no provider at all: every call answers 401.
        if (config.AuthIssuerUrl is not null && config.AuthClientIds.Count > 0)
        {
            var issuer = config.AuthIssuerUrl;
            builder.Services.AddSingleton<IJwksProvider>(services => new HttpJwksProvider(
                new HttpClient(),
                HttpJwksProvider.ForIssuer(issuer),
                clock: services.GetRequiredService<TimeProvider>()));
        }

        builder.Services
            .AddAuthentication(BearerAuthentication.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, BearerAuthentication>(BearerAuthentication.SchemeName, null);
        builder.Services.AddAuthorization();

        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = McpTools.ServerName, Title = McpTools.ServerTitle, Version = string.Empty };
                options.ServerInstructions = McpTools.Instructions;
            })

            // Revision 2026-07-28 removed protocol sessions and the standalone
            // GET stream, so a fully stateless server is conformant — the
            // shape Lambda wants: any invocation may land on any container.
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithTools(McpTools.All());
        return builder;
    }

    /// <summary>
    /// The pipeline, outermost first: the request log; the RFC 9728 metadata
    /// (public); then, on <c>/mcp</c> only, authentication (the 401
    /// challenge), the transport checks, the JSON shim and the SDK.
    /// </summary>
    /// <param name="app">The application.</param>
    /// <returns>The same application.</returns>
    public static WebApplication UseInsolviaMcp(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var config = app.Services.GetRequiredService<McpConfig>();

        app.UseRequestLog();

        // The metadata endpoints need an issuer to advertise even on a bare
        // local server with no pool configured; verification still fails
        // closed there, so this default can only ever 401 harder, never admit.
        var issuer = config.AuthIssuerUrl ?? "http://127.0.0.1/auth-not-configured";
        app.MapMethods(config.ResourceMetadataPath, [HttpMethods.Get, HttpMethods.Head], () => Results.Text(
            new JsonObject
            {
                ["resource"] = config.ResourceUrl,
                ["authorization_servers"] = new JsonArray(issuer),
                ["bearer_methods_supported"] = new JsonArray("header"),
            }.ToJsonString(),
            "application/json"));

        app.UseWhen(
            context => context.Request.Path.StartsWithSegments(EndpointPath, StringComparison.Ordinal),
            mcp =>
            {
                mcp.UseAuthentication();
                mcp.Use(async (context, next) =>
                {
                    // Every method on /mcp is protected: Python's auth
                    // middleware wrapped the whole transport.
                    if (context.User.Identity?.IsAuthenticated != true)
                    {
                        await context.ChallengeAsync(BearerAuthentication.SchemeName).ConfigureAwait(false);
                        return;
                    }

                    await next(context).ConfigureAwait(false);
                });
                mcp.UseMiddleware<TransportSecurity>();
                mcp.UseMiddleware<JsonResponseShim>();
            });

        app.MapMcp(EndpointPath);
        return app;
    }
}
