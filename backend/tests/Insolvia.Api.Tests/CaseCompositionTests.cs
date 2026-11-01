using Insolvia.Api.Configuration;
using Insolvia.Core.Local;
using Insolvia.Core.Memory;
using Insolvia.Core.Ports;
using Insolvia.Data.Cases;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Insolvia.Api.Tests;

/// <summary>
/// Which case-side stores each shape of the host composes (18.11): the
/// Postgres stores over the request's context inside Lambda, and the memory
/// twins — wired to each other as the tables are — everywhere else.
/// </summary>
public sealed class CaseCompositionTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly (string Name, string Value)[] Lambda =
    [
        (LambdaBootRequirements.LambdaFunctionVariable, "insolvia-staging-api"),
        ("AUTH_ISSUER_URL", "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE00"),
        ("AUTH_CLIENT_ID", "exampleappclientid000000"),
        ("AUTH_USER_POOL_ID", "us-east-1_EXAMPLE00"),
        ("CASE_DOCUMENT_BUCKET", "insolvia-staging-case-documents-us-east-1"),
        ("INSOLVIA_KMS_CASE_KEY_ALIAS", "alias/insolvia-staging-cases"),
        ("INSOLVIA_KMS_FILING_KEY_ALIAS", "alias/insolvia-staging-filing-credentials"),
        ("AWS_REGION", "us-east-1"),

        // Never connected to: composing a store opens nothing.
        ("INSOLVIA_DB_CONNECTION_STRING", "Host=127.0.0.1;Port=1;Database=insolvia;Username=api;Password=unused"),
    ];

    [Fact]
    public void Inside_lambda_every_case_port_is_a_postgres_store_scoped_to_the_request()
    {
        using var lambda = factory.WithWebHostBuilder(host =>
        {
            foreach (var (name, value) in Lambda)
            {
                host.UseSetting(name, value);
            }
        });
        using var scope = lambda.Services.CreateScope();
        var services = scope.ServiceProvider;

        Assert.IsType<PostgresCaseStore>(services.GetRequiredService<ICaseStore>());
        Assert.IsType<PostgresDebtorStore>(services.GetRequiredService<IDebtorStore>());
        Assert.Same(services.GetRequiredService<IDebtorStore>(), services.GetRequiredService<IDebtorClientLinks>());
        Assert.IsType<PostgresCaseEntityStore>(services.GetRequiredService<ICaseEntityStore>());
        Assert.IsType<PostgresDocumentStore>(services.GetRequiredService<IDocumentStore>());
        Assert.IsType<PostgresCandidateStore>(services.GetRequiredService<ICandidateStore>());
        Assert.IsType<PostgresCaseTaskStore>(services.GetRequiredService<ICaseTaskStore>());
        Assert.IsType<PostgresDocumentRequestStore>(services.GetRequiredService<IDocumentRequestStore>());
        Assert.IsType<PostgresFilingStore>(services.GetRequiredService<IFilingStore>());
        Assert.IsType<PostgresTaxIdStore>(services.GetRequiredService<ITaxIdStore>());

        using var another = lambda.Services.CreateScope();
        Assert.NotSame(services.GetRequiredService<ICaseStore>(), another.ServiceProvider.GetRequiredService<ICaseStore>());
    }

    [Fact]
    public void Outside_lambda_a_named_database_composes_the_postgres_stores_with_the_local_twins_beside_them()
    {
        using var local = factory.WithWebHostBuilder(host =>
        {
            host.UseSetting("INSOLVIA_DB_CONNECTION_STRING", "Host=127.0.0.1;Port=1;Database=insolvia;Username=api;Password=unused");
            host.UseSetting("INSOLVIA_LOCAL_BLOB_DIR", Path.Combine(Path.GetTempPath(), "insolvia-composition-test"));
            host.UseSetting("INSOLVIA_LOCAL_BLOB_URL", "http://127.0.0.1:18060");
        });
        using var scope = local.Services.CreateScope();
        var services = scope.ServiceProvider;

        Assert.IsType<PostgresCaseStore>(services.GetRequiredService<ICaseStore>());
        Assert.IsType<PostgresFilingStore>(services.GetRequiredService<IFilingStore>());
        Assert.IsType<PostgresTaxIdStore>(services.GetRequiredService<ITaxIdStore>());
        Assert.IsType<Insolvia.Data.Firms.PostgresFirmStore>(services.GetRequiredService<IFirmStore>());

        // No pool, no KMS, no bucket on a laptop: the local twins.
        Assert.IsType<MemoryUserDirectory>(services.GetRequiredService<IUserDirectory>());
        Assert.IsType<Insolvia.Aws.Sealing.LocalTaxIdCipher>(services.GetRequiredService<ITaxIdCipher>());
        Assert.IsType<DirectoryDocumentBlobStore>(services.GetRequiredService<IDocumentBlobStore>());
    }

    [Fact]
    public void Off_lambda_with_no_filing_queue_the_memory_sender_records_and_inside_lambda_there_is_none()
    {
        Assert.IsType<MemoryFilingJobSender>(factory.Services.GetRequiredService<IFilingJobSender>());

        using var lambda = factory.WithWebHostBuilder(host =>
        {
            foreach (var (name, value) in Lambda)
            {
                host.UseSetting(name, value);
            }
        });
        Assert.Null(lambda.Services.GetService<IFilingJobSender>());
    }

    [Fact]
    public void Inside_lambda_the_local_blob_pair_is_ignored()
    {
        using var lambda = factory.WithWebHostBuilder(host =>
        {
            foreach (var (name, value) in Lambda)
            {
                host.UseSetting(name, value);
            }

            host.UseSetting("INSOLVIA_ENV", "local");
            host.UseSetting("INSOLVIA_LOCAL_BLOB_DIR", Path.Combine(Path.GetTempPath(), "insolvia-composition-test"));
            host.UseSetting("INSOLVIA_LOCAL_BLOB_URL", "http://127.0.0.1:18060");
        });

        Assert.IsType<Insolvia.Aws.Documents.S3DocumentBlobStore>(lambda.Services.GetRequiredService<IDocumentBlobStore>());
    }

    [Fact]
    public void Elsewhere_the_case_twins_share_one_state_as_the_tables_do()
    {
        var services = factory.Services;
        var cases = Assert.IsType<MemoryCaseStore>(services.GetRequiredService<ICaseStore>());
        var filings = Assert.IsType<MemoryFilingStore>(services.GetRequiredService<IFilingStore>());

        Assert.Same(cases, filings.Cases);
        Assert.Same(cases.Debtors, services.GetRequiredService<IDebtorStore>());
        Assert.Same(cases.Debtors, services.GetRequiredService<IDebtorClientLinks>());
        Assert.IsType<MemoryCaseEntityStore>(services.GetRequiredService<ICaseEntityStore>());
        Assert.IsType<MemoryDocumentStore>(services.GetRequiredService<IDocumentStore>());
        Assert.IsType<MemoryCandidateStore>(services.GetRequiredService<ICandidateStore>());
        Assert.IsType<MemoryCaseTaskStore>(services.GetRequiredService<ICaseTaskStore>());
        Assert.IsType<MemoryDocumentRequestStore>(services.GetRequiredService<IDocumentRequestStore>());
        Assert.IsType<MemoryTaxIdStore>(services.GetRequiredService<ITaxIdStore>());
    }
}
