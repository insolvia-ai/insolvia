using Amazon;
using Amazon.KeyManagementService;
using Amazon.S3;
using Amazon.SQS;
using Insolvia.Api.Configuration;
using Insolvia.Api.Http;
using Insolvia.Api.Logging;
using Insolvia.Api.Mail;
using Insolvia.Api.Routes;
using Insolvia.Aws.Documents;
using Insolvia.Aws.Jobs;
using Insolvia.Aws.Jwks;
using Insolvia.Aws.Mailer;
using Insolvia.Aws.Sealing;
using Insolvia.Core.Courts;
using Insolvia.Core.Memory;
using Insolvia.Core.Ports;
using Insolvia.Data;
using Insolvia.Data.AccessLogging;
using Insolvia.Data.Calendar;
using Insolvia.Data.Cases;
using Insolvia.Data.Connections;
using Insolvia.Data.Firms;
using Insolvia.Data.Pagination;
using Insolvia.Data.Portal;
using Insolvia.Data.Vault;
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

        // The court registry ships inside Insolvia.Core (ADR 0014) and is the
        // same everywhere: a firm's default court is validated against it.
        builder.Services.AddSingleton<ICourtRegistry>(CourtRegistry.Shipped);

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

            // Cognito AdminCreateUser for POST /v1/firm/users and the portal
            // invitation's POST …/portal/invitation and …/resend, over the pool
            // LambdaBootRequirements already demanded.
            builder.Services.AddSingleton<IUserDirectory>(_ => new Insolvia.Aws.Cognito.CognitoUserDirectory(new Amazon.CognitoIdentityProvider.AmazonCognitoIdentityProviderClient(), config.AuthUserPoolId!));

            // The case side (18.11): every store over the request's one
            // context, so a route's writes share its connection. Cursors are
            // signed with INSOLVIA_CURSOR_KEY when set (CursorCodec). The
            // debtor store is also the client merge's IDebtorClientLinks.
            builder.Services.AddSingleton(_ => CursorCodec.FromEnvironment(name => configuration[name]));
            builder.Services.AddScoped<ICaseStore>(services => new PostgresCaseStore(
                services.GetRequiredService<InsolviaDbContext>(),
                services.GetRequiredService<CursorCodec>()));
            builder.Services.AddScoped<IDebtorStore>(services => new PostgresDebtorStore(services.GetRequiredService<InsolviaDbContext>()));
            builder.Services.AddScoped<IDebtorClientLinks>(services => services.GetRequiredService<IDebtorStore>());
            builder.Services.AddScoped<ICaseEntityStore>(services => new PostgresCaseEntityStore(services.GetRequiredService<InsolviaDbContext>()));
            builder.Services.AddScoped<IDocumentStore>(services => new PostgresDocumentStore(services.GetRequiredService<InsolviaDbContext>()));
            builder.Services.AddScoped<ICandidateStore>(services => new PostgresCandidateStore(services.GetRequiredService<InsolviaDbContext>()));
            builder.Services.AddScoped<ICaseTaskStore>(services => new PostgresCaseTaskStore(services.GetRequiredService<InsolviaDbContext>()));
            builder.Services.AddScoped<IDocumentRequestStore>(services => new PostgresDocumentRequestStore(services.GetRequiredService<InsolviaDbContext>()));
            builder.Services.AddScoped<IFilingStore>(services => new PostgresFilingStore(services.GetRequiredService<InsolviaDbContext>()));
            builder.Services.AddScoped<ITaxIdStore>(services => new PostgresTaxIdStore(services.GetRequiredService<InsolviaDbContext>()));

            // The pipeline (18.17): the API creates and reads job rows, reads
            // packets; the worker advances the one and writes the other.
            builder.Services.AddScoped<IJobStore>(services => new PostgresJobStore(services.GetRequiredService<InsolviaDbContext>()));
            builder.Services.AddScoped<IPacketStore>(services => new PostgresPacketStore(services.GetRequiredService<InsolviaDbContext>()));

            // Sealed data (18.12). The vault's stores, over the same context:
            // the API's role may INSERT and DELETE a credential and SELECT
            // its metadata columns only (GrantTable), which is all these
            // stores ask of it. KMS: the tax-id cipher seals AND opens under
            // the case key; the vault gets a SEALER and nothing else — no
            // IFilingCredentialOpener exists in this container (a test pins
            // it), and the vault key's policy refuses this role Decrypt
            // regardless. Aliases resolve at composition, so a Lambda that
            // cannot name its keys never takes a request (SealingKeys).
            var keys = SealingKeys.Load(name => configuration[name]);
            builder.Services.AddScoped<IFilingCredentialStore>(services => new PostgresFilingCredentialStore(services.GetRequiredService<InsolviaDbContext>()));
            builder.Services.AddScoped<IFilingAuthorizationStore>(services => new PostgresFilingAuthorizationStore(services.GetRequiredService<InsolviaDbContext>()));
            builder.Services.AddSingleton<IAmazonKeyManagementService>(_ => configuration["AWS_REGION"] is { Length: > 0 } region
                ? new AmazonKeyManagementServiceClient(RegionEndpoint.GetBySystemName(region))
                : new AmazonKeyManagementServiceClient());
            builder.Services.AddSingleton<ITaxIdCipher>(services => new KmsTaxIdCipher(services.GetRequiredService<IAmazonKeyManagementService>(), keys.CaseKeyAlias));
            builder.Services.AddSingleton<IFilingCredentialSealer>(services => new KmsFilingCredentialSealer(services.GetRequiredService<IAmazonKeyManagementService>(), keys.FilingKeyAlias));

            // The calendar (14.6 / #358).
            builder.Services.AddScoped<ICalendarEventStore>(services => new PostgresCalendarEventStore(services.GetRequiredService<InsolviaDbContext>()));
            builder.Services.AddScoped<ICalendarTokenStore>(services => new PostgresCalendarTokenStore(services.GetRequiredService<InsolviaDbContext>()));

            // The bytes: the case-document bucket, which LambdaBootRequirements
            // already refuses to boot without. One client per process.
            var bucket = config.CaseDocumentBucket!;
            builder.Services.AddSingleton<IDocumentBlobStore>(_ => new S3DocumentBlobStore(new AmazonS3Client(S3DocumentBlobStore.ClientConfig()), bucket));
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

        // The pipeline's queue (ADR 0018): with JOB_QUEUE_URL, SQS — a laptop's
        // own dev queue, which the worker's poller consumes. Without it, a
        // deployed API answers 503 on accept (the image can roll out ahead of
        // the queue); everywhere else the memory sender records the message
        // and runs nothing.
        if (config.JobQueueUrl is { } queueUrl)
        {
            builder.Services.AddSingleton<IAmazonSQS>(_ => configuration["AWS_REGION"] is { Length: > 0 } region
                ? new AmazonSQSClient(RegionEndpoint.GetBySystemName(region))
                : new AmazonSQSClient());
            builder.Services.AddSingleton<IJobSender>(services => new SqsJobSender(services.GetRequiredService<IAmazonSQS>(), queueUrl));
        }
        else if (!isLambda)
        {
            builder.Services.AddSingleton<IJobSender, MemoryJobSender>();
        }

        if (!isLambda)
        {
            builder.Services.AddSingleton<IFirmStore>(services => new MemoryFirmStore(services.GetRequiredService<TimeProvider>()));
            builder.Services.AddSingleton<IClientBindingStore, MemoryClientBindingStore>();
            builder.Services.AddSingleton<IAccessLog, MemoryAccessLog>();
            builder.Services.AddSingleton<IWaitlistStore, MemoryWaitlistStore>();

            // Mints a subject where Cognito would; sends no invitation.
            builder.Services.AddSingleton<IUserDirectory, MemoryUserDirectory>();

            // The case twins, wired as the real stores share one database:
            // the debtor store reads its client conditions from the firm
            // store, the case store writes debtors into it, and the filing
            // store files into the case store.
            builder.Services.AddSingleton(services => new MemoryDebtorStore(services.GetRequiredService<IFirmStore>()));
            builder.Services.AddSingleton<IDebtorStore>(services => services.GetRequiredService<MemoryDebtorStore>());
            builder.Services.AddSingleton<IDebtorClientLinks>(services => services.GetRequiredService<MemoryDebtorStore>());
            builder.Services.AddSingleton(services => new MemoryCaseStore(services.GetRequiredService<MemoryDebtorStore>()));
            builder.Services.AddSingleton<ICaseStore>(services => services.GetRequiredService<MemoryCaseStore>());
            builder.Services.AddSingleton<IFilingStore>(services => new MemoryFilingStore(services.GetRequiredService<MemoryCaseStore>()));
            builder.Services.AddSingleton<ICaseEntityStore, MemoryCaseEntityStore>();
            builder.Services.AddSingleton<IDocumentStore, MemoryDocumentStore>();
            builder.Services.AddSingleton<ICandidateStore, MemoryCandidateStore>();
            builder.Services.AddSingleton<ICaseTaskStore, MemoryCaseTaskStore>();
            builder.Services.AddSingleton<IDocumentRequestStore, MemoryDocumentRequestStore>();
            builder.Services.AddSingleton<ITaxIdStore, MemoryTaxIdStore>();
            builder.Services.AddSingleton<IJobStore, MemoryJobStore>();
            builder.Services.AddSingleton<IPacketStore>(services => new MemoryPacketStore(services.GetRequiredService<MemoryCaseStore>()));

            // The vault's twins and the deterministic local key — never
            // composed beside a real table. Still a SEALER only: the
            // development host is no more allowed to open a credential than
            // the deployed one.
            builder.Services.AddSingleton<IFilingCredentialStore, MemoryFilingCredentialStore>();
            builder.Services.AddSingleton<IFilingAuthorizationStore, MemoryFilingAuthorizationStore>();
            builder.Services.AddSingleton<ITaxIdCipher>(_ => new LocalTaxIdCipher());
            builder.Services.AddSingleton<IFilingCredentialSealer, LocalFilingCredentialSealer>();
            builder.Services.AddSingleton<ICalendarEventStore, MemoryCalendarEventStore>();
            builder.Services.AddSingleton<ICalendarTokenStore, MemoryCalendarTokenStore>();

            // No local object store: the memory twin mints memory+s3:// URLs no
            // client can fetch.
            builder.Services.AddSingleton<IDocumentBlobStore, MemoryDocumentBlobStore>();
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

        // The case routes (18.15, part 1). Order does not decide routing —
        // a literal segment beats {collection} by template precedence.
        builder.Services.AddSingleton<IEndpointModule, CaseRoutes>();
        builder.Services.AddSingleton<IEndpointModule, CaseLifecycleRoutes>();
        builder.Services.AddSingleton<IEndpointModule, DebtorRoutes>();
        builder.Services.AddSingleton<IEndpointModule, NoteRoutes>();
        builder.Services.AddSingleton<IEndpointModule, DocumentRoutes>();
        builder.Services.AddSingleton<IEndpointModule, DocumentRequestRoutes>();
        builder.Services.AddSingleton<IEndpointModule, TaskRoutes>();
        builder.Services.AddSingleton<IEndpointModule, EventRoutes>();
        builder.Services.AddSingleton<IEndpointModule, CalendarRoutes>();
        builder.Services.AddSingleton<IEndpointModule, CaseAnalysisRoutes>(); // 18.15 part 2: the computed analyses
        builder.Services.AddSingleton<IEndpointModule, CaseEntityRoutes>();

        // The portal (18.18): the firm's invitation routes, then the client's
        // own — the only routes RequireClient admits.
        builder.Services.AddSingleton<IEndpointModule, PortalInvitationRoutes>();
        builder.Services.AddSingleton<IEndpointModule, PortalRoutes>();
        builder.Services.AddSingleton<IEndpointModule, PortalDocumentRoutes>();

        // The pipeline's routes (18.17).
        builder.Services.AddSingleton<IEndpointModule, JobRoutes>();
        builder.Services.AddSingleton<IEndpointModule, PacketRoutes>();
        builder.Services.AddSingleton<IEndpointModule, CaseSummaryRoutes>();
        builder.Services.AddSingleton<IEndpointModule, ExtractionReviewRoutes>();

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
