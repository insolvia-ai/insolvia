using Amazon;
using Amazon.KeyManagementService;
using Amazon.S3;
using Insolvia.Aws.Documents;
using Insolvia.Aws.Filing;
using Insolvia.Aws.Sealing;
using Insolvia.Core.Ports;
using Insolvia.Data;
using Insolvia.Data.AccessLogging;
using Insolvia.Data.Cases;
using Insolvia.Data.Connections;
using Insolvia.Data.Pagination;
using Insolvia.Data.Vault;
using Insolvia.Filing.Drivers;
using Insolvia.Filing.Fence;
using Insolvia.Filing.Http;
using Insolvia.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace Insolvia.Filing;

/// <summary>
/// The ONE composition of the worker against its real ports — shared by the
/// Lambda entrypoint and the local poller, so a laptop runs the exact worker
/// the cloud runs (from <c>entrypoints/compose.py</c>). What differs by
/// environment is decided HERE and nowhere else:
/// <list type="bullet">
/// <item>the stores: Postgres as the <c>filing</c> role (<c>INSOLVIA_DB_*</c>) — SELECT
/// on the vault, the approvals' consume, the filing record, the case's filed move;</item>
/// <item>THE OPENER — <see cref="KmsFilingCredentialOpener"/> under the vault alias
/// (<see cref="SealingKeys"/>), composed in this host and no other; locally,
/// without an alias, the local twin the API's local sealer pairs with;</item>
/// <item>the kill switch: the SSM parameter deployed, <c>FILING_SUBMISSIONS_ENABLED</c> locally;</item>
/// <item>the fake driver: only with <c>INSOLVIA_ENV=local</c> AND <c>FAKE_CMECF_URL</c>;</item>
/// <item>the fence: <see cref="HostFence.For"/> — the code's allowlist, no input.</item>
/// </list>
/// </summary>
public static class FilingComposition
{
    /// <summary>
    /// The packet store is #528's (the job worker writes packets); until 18.22
    /// wires it as an <see cref="IPacketReader"/> this host refuses to start
    /// rather than claim approvals it could only hand back.
    /// </summary>
    public const string PacketReaderMissing =
        "the packet store is not composed in the filing host yet (#528); plan 18.22 wires it as Insolvia.Forms.IPacketReader";

    /// <summary>The services, once per process. <paramref name="packets"/> is the packet store's reader (18.22).</summary>
    public static ServiceProvider Services(FilingConfig config, Func<string, string?> read, Func<IServiceProvider, IPacketReader>? packets = null, IFilingLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(read);
        if (packets is null)
        {
            throw new InvalidOperationException(PacketReaderMissing);
        }

        if (config.CaseDocumentBucket is null)
        {
            throw new InvalidOperationException("CASE_DOCUMENT_BUCKET must be set for the filing worker");
        }

        var services = new ServiceCollection();
        services.AddSingleton(config);
        services.AddSingleton(log ?? new ConsoleFilingLog());
        services.AddInsolviaData(DatabaseSettings.FromEnvironment(read, applicationName: FilingConfig.ServiceName));
        services.AddScoped<IFilingStore>(provider => new PostgresFilingStore(provider.GetRequiredService<InsolviaDbContext>()));
        services.AddScoped<IFilingApprovalStore>(provider => new PostgresFilingApprovalStore(provider.GetRequiredService<InsolviaDbContext>()));
        services.AddScoped<ICaseStore>(provider => new PostgresCaseStore(provider.GetRequiredService<InsolviaDbContext>(), CursorCodec.FromEnvironment(read)));
        services.AddScoped<IDebtorStore>(provider => new PostgresDebtorStore(provider.GetRequiredService<InsolviaDbContext>()));
        services.AddScoped<ICaseEntityStore>(provider => new PostgresCaseEntityStore(provider.GetRequiredService<InsolviaDbContext>()));
        services.AddScoped<IDocumentStore>(provider => new PostgresDocumentStore(provider.GetRequiredService<InsolviaDbContext>()));
        services.AddScoped<IFilingCredentialStore>(provider => new PostgresFilingCredentialStore(provider.GetRequiredService<InsolviaDbContext>()));
        services.AddScoped<IFilingAuthorizationStore>(provider => new PostgresFilingAuthorizationStore(provider.GetRequiredService<InsolviaDbContext>()));
        services.AddScoped<IAccessLog>(provider => new PostgresAccessLog(provider.GetRequiredService<InsolviaDbContext>()));
        services.AddScoped(packets);

        var bucket = config.CaseDocumentBucket;
        services.AddSingleton<IDocumentBlobStore>(_ => new S3DocumentBlobStore(new AmazonS3Client(S3DocumentBlobStore.ClientConfig()), bucket));

        var region = read("AWS_REGION") is { Length: > 0 } named ? RegionEndpoint.GetBySystemName(named) : null;
        if (config.Environment == "local" && read(SealingKeys.FilingKeyVariable) is not { Length: > 0 } && read("CASE_TABLE_NAME") is not { Length: > 0 })
        {
            services.AddSingleton<IFilingCredentialOpener>(new LocalFilingCredentialOpener());
        }
        else
        {
            var alias = SealingKeys.Load(read).FilingKeyAlias;
            services.AddSingleton<IFilingCredentialOpener>(_ => new KmsFilingCredentialOpener(
                region is null ? new AmazonKeyManagementServiceClient() : new AmazonKeyManagementServiceClient(region),
                alias));
        }

        if (config.Environment == "local")
        {
            services.AddSingleton<IKillSwitch>(new StaticKillSwitch(config.LocalSubmissionsEnabled));
        }
        else
        {
            // The AWS control plane, not a court: this HttpClient reaches SSM
            // alone (FenceTests names this file as the one other HTTP user).
            var parameter = config.KillSwitchParameter!;
            services.AddSingleton<IKillSwitch>(_ => SsmKillSwitch.FromEnvironment(new HttpClient(), parameter, read));
        }

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    /// <summary>One run's dependencies, over a scope (one database context per message).</summary>
    public static FilingDeps Deps(IServiceProvider scope, FilingConfig config)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(config);
        var fence = HostFence.For(config.Environment);
        var timeout = TimeSpan.FromSeconds(config.RequestTimeoutSeconds);
        Func<ICourtDriver>? fake = null;
        if (config.Environment == "local" && config.FakeCmEcfUrl is { } url)
        {
            fake = () => new FakeCmEcfDriver(url);
        }

        return new FilingDeps
        {
            Environment = config.Environment,
            Filings = scope.GetRequiredService<IFilingStore>(),
            Approvals = scope.GetRequiredService<IFilingApprovalStore>(),
            Cases = scope.GetRequiredService<ICaseStore>(),
            Debtors = scope.GetRequiredService<IDebtorStore>(),
            Entities = scope.GetRequiredService<ICaseEntityStore>(),
            Packets = scope.GetRequiredService<IPacketReader>(),
            Blobs = scope.GetRequiredService<IDocumentBlobStore>(),
            Documents = scope.GetRequiredService<IDocumentStore>(),
            Credentials = scope.GetRequiredService<IFilingCredentialStore>(),
            Authorizations = scope.GetRequiredService<IFilingAuthorizationStore>(),
            Opener = scope.GetRequiredService<IFilingCredentialOpener>(),
            AccessLog = scope.GetRequiredService<IAccessLog>(),
            KillSwitch = scope.GetRequiredService<IKillSwitch>(),
            Fence = fence,
            Http = () => new FencedHttpClient(fence, timeout),
            Log = scope.GetRequiredService<IFilingLog>(),
            FakeDriver = fake,
            LeaseSeconds = config.LeaseSeconds,
        };
    }
}
