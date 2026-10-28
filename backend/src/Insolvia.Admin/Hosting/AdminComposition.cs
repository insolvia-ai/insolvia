using Amazon.CognitoIdentityProvider;
using Insolvia.Admin.Configuration;
using Insolvia.Admin.Http;
using Insolvia.Admin.Logging;
using Insolvia.Admin.Routes;
using Insolvia.Aws.Cognito;
using Insolvia.Aws.Jwks;
using Insolvia.Core.Auth;
using Insolvia.Core.Memory;
using Insolvia.Core.Ports;
using Insolvia.Data;
using Insolvia.Data.AdminAuditing;
using Insolvia.Data.Connections;
using Insolvia.Data.Firms;
using Microsoft.Extensions.Logging.Console;

namespace Insolvia.Admin.Hosting;

/// <summary>
/// What Program.cs composes (services/admin entrypoints/api_lambda.py and
/// development_server.py in one binary, chosen by
/// <see cref="LambdaBootRequirements.LambdaFunctionVariable"/>).
/// </summary>
/// <remarks>
/// <para><b>Lambda</b> refuses to boot without its requirements; composes the
/// Postgres firm store and audit log (request-scoped, the <c>admin</c> role),
/// the Cognito directory over the firm pool, and Google's JWKS — THE TRUST
/// BOUNDARY in one constructor call: the only tokens that can verify here are
/// ones Google signed.</para>
/// <para><b>Everywhere else</b> (<c>dotnet run</c>, compose, the test host):
/// with <c>INSOLVIA_DB_CONNECTION_STRING</c> the firm store and the audit log
/// are Postgres — as a GROUP, so a real firm is never suspended with its audit
/// row landing in a dictionary that dies with the process; without it, both
/// are in memory. <c>FIRM_USER_POOL_ID</c> separately picks the Cognito
/// directory (invites really mailed) over the in-memory one (subjects minted,
/// nothing sent). Without <c>GOOGLE_CLIENT_ID</c> every staff route 401s.</para>
/// <para>The HTTP plumbing (<see cref="FlaskJson"/>, <see cref="Cors"/>,
/// <see cref="MethodFallback"/>, the request log and error mapping) is copied
/// from Insolvia.Api, not referenced: a host never references another host.</para>
/// </remarks>
public static class AdminComposition
{
    /// <summary>Registers configuration, logging and the ports.</summary>
    /// <param name="builder">The application builder.</param>
    /// <returns>The same builder.</returns>
    public static WebApplicationBuilder AddInsolviaAdmin(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var configuration = builder.Configuration;
        var isLambda = !string.IsNullOrEmpty(configuration[LambdaBootRequirements.LambdaFunctionVariable]);

        // One JSON line per record, metadata only — never bodies, never
        // claims, never a firm name next to an error.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.FormatterName = JsonLogFormatter.FormatterName);
        builder.Logging.AddConsoleFormatter<JsonLogFormatter, ConsoleFormatterOptions>();
        builder.Logging.AddFilter("Microsoft.AspNetCore", Microsoft.Extensions.Logging.LogLevel.Warning);

        var config = AdminConfig.Load(name => configuration[name]);
        if (isLambda)
        {
            // A deployed service reads Google's keys whatever the environment
            // says: the local JWKS override never reaches a Lambda.
            config = config with { LocalJwksUrl = null };
            LambdaBootRequirements.Enforce(config);
        }

        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(services => services.GetRequiredService<AdminConfig>().Environment);
        builder.Services.AddSingleton(TimeProvider.System);

        var databaseConfigured = isLambda || !string.IsNullOrWhiteSpace(configuration[DatabaseSettings.ConnectionStringVariable]);
        if (databaseConfigured)
        {
            // Throws at composition when the INSOLVIA_DB_* contract is
            // incomplete, so a misconfigured Lambda never takes a request.
            builder.Services.AddInsolviaData(DatabaseSettings.FromEnvironment(name => configuration[name], applicationName: "insolvia-admin"));
            builder.Services.AddScoped<IFirmStore>(services => new PostgresFirmStore(
                services.GetRequiredService<InsolviaDbContext>(),
                services.GetRequiredService<TimeProvider>()));
            builder.Services.AddScoped<IAdminAuditLog>(services => new PostgresAdminAuditLog(services.GetRequiredService<InsolviaDbContext>()));
        }
        else
        {
            builder.Services.AddSingleton<IFirmStore>(services => new MemoryFirmStore(services.GetRequiredService<TimeProvider>()));
            builder.Services.AddSingleton<IAdminAuditLog, MemoryAdminAuditLog>();
        }

        if (config.FirmUserPoolId is { } poolId)
        {
            builder.Services.AddSingleton<IAmazonCognitoIdentityProvider>(_ => new AmazonCognitoIdentityProviderClient());
            builder.Services.AddSingleton<IUserDirectory>(services => new CognitoUserDirectory(services.GetRequiredService<IAmazonCognitoIdentityProvider>(), poolId));
        }
        else
        {
            builder.Services.AddSingleton<IUserDirectory, MemoryUserDirectory>();
        }

        // Constructed once so its key cache lives as long as the process;
        // nothing is fetched until the first token arrives.
        var jwksUrl = config.LocalJwksUrl ?? TokenVerification.GoogleJwksUrl;
        builder.Services.AddSingleton<IJwksProvider>(services => new HttpJwksProvider(
            new HttpClient(),
            jwksUrl,
            clock: services.GetRequiredService<TimeProvider>()));

        return builder;
    }

    /// <summary>
    /// The pipeline, outermost first — the API's: the request log, CORS, the
    /// error handlers, HEAD-as-GET, routing, the 404/405/OPTIONS fallback,
    /// then the endpoints.
    /// </summary>
    /// <param name="app">The application.</param>
    /// <returns>The same application.</returns>
    public static WebApplication UseInsolviaAdmin(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        _ = app.Services.GetRequiredService<AdminConfig>();

        app.UseRequestLog();
        app.UseInsolviaCors();
        app.UseMiddleware<ErrorMappingMiddleware>();
        app.UseHeadAsGet();
        app.UseRouting();
        app.UseMethodFallback();

        app.MapFirmRoutes();
        app.MapHealth();
        return app;
    }
}
