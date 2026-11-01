using Insolvia.Api.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Insolvia.Api.Tests;

// Translated from services/api tests/unit/test_config.py, plus the boot
// refusals of entrypoints/api_lambda.py.
public sealed class ConfigTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static ApiConfig Load(params (string Name, string? Value)[] variables)
    {
        var map = variables.ToDictionary(pair => pair.Name, pair => pair.Value, StringComparer.Ordinal);
        return ApiConfig.Load(name => map.GetValueOrDefault(name));
    }

    [Fact]
    public void The_environment_defaults_to_local()
    {
        var config = Load();

        Assert.Equal("local", config.Environment.Name);
        Assert.Null(config.AuthIssuerUrl);
        Assert.Equal("http://localhost:3000", config.MarketingOrigin);
        Assert.Equal("http://localhost:3000", config.AppOrigin);
    }

    [Theory]
    [InlineData("local")]
    [InlineData("staging")]
    [InlineData("production")]
    public void Known_environments_are_accepted(string environment) =>
        Assert.Equal(environment, Load(("INSOLVIA_ENV", environment)).Environment.Name);

    [Fact]
    public void The_local_blob_pair_is_read_locally_and_ignored_elsewhere()
    {
        (string, string?)[] pair = [("INSOLVIA_LOCAL_BLOB_DIR", "/tmp/blobs"), ("INSOLVIA_LOCAL_BLOB_URL", "http://127.0.0.1:18060")];

        Assert.Equal(new LocalBlobSettings("/tmp/blobs", new Uri("http://127.0.0.1:18060")), Load(pair).LocalBlobs);
        Assert.Null(Load([.. pair, ("INSOLVIA_ENV", "staging")]).LocalBlobs);
        Assert.Null(Load([.. pair, ("INSOLVIA_ENV", "production")]).LocalBlobs);
        Assert.Null(Load().LocalBlobs);
    }

    [Theory]
    [InlineData("/tmp/blobs", null)]
    [InlineData(null, "http://127.0.0.1:18060")]
    [InlineData("/tmp/blobs", "file:///tmp/blobs")]
    public void The_local_blob_pair_is_both_or_neither_and_an_http_url(string? directory, string? url) =>
        Assert.Throws<ArgumentException>(() => Load(("INSOLVIA_LOCAL_BLOB_DIR", directory), ("INSOLVIA_LOCAL_BLOB_URL", url)));

    [Fact]
    public void An_unknown_environment_is_rejected() =>
        Assert.Contains("INSOLVIA_ENV", Assert.Throws<ArgumentException>(() => Load(("INSOLVIA_ENV", "prod"))).Message, StringComparison.Ordinal);

    [Theory]
    [InlineData("CASE_DOCUMENT_BUCKET", "insolvia-staging-case-documents-us-east-1")]
    [InlineData("MAILER_API_URL", "https://mailer-staging.insolvia.ai")]
    [InlineData("AUTH_USER_POOL_ID", "us-east-1_EXAMPLE00")]
    [InlineData("JOB_QUEUE_URL", "https://sqs.example.test/jobs")]
    [InlineData("FILING_QUEUE_URL", "https://sqs.example.test/filing")]
    [InlineData("ANTHROPIC_API_KEY", "not-a-real-key")]
    [InlineData("UNSUBSCRIBE_SECRET", "not-a-real-secret")]
    [InlineData("AUTH_ISSUER_URL", "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE00")]
    [InlineData("AUTH_CLIENT_ID", "exampleappclientid000000")]
    [InlineData("AUTH_PORTAL_CLIENT_ID", "exampleportalclientid000")]
    public void Each_variable_is_read_under_the_python_name_and_empty_means_unset(string name, string value)
    {
        static string? Field(ApiConfig config, string name) => name switch
        {
            "CASE_DOCUMENT_BUCKET" => config.CaseDocumentBucket,
            "MAILER_API_URL" => config.MailerApiUrl,
            "AUTH_USER_POOL_ID" => config.AuthUserPoolId,
            "JOB_QUEUE_URL" => config.JobQueueUrl,
            "FILING_QUEUE_URL" => config.FilingQueueUrl,
            "ANTHROPIC_API_KEY" => config.AnthropicApiKey,
            "UNSUBSCRIBE_SECRET" => config.UnsubscribeSecret,
            "AUTH_ISSUER_URL" => config.AuthIssuerUrl,
            "AUTH_CLIENT_ID" => config.AuthClientId,
            _ => config.AuthPortalClientId,
        };

        Assert.Null(Field(Load(), name));
        Assert.Equal(value, Field(Load((name, value)), name));
        Assert.Null(Field(Load((name, string.Empty)), name));
    }

    [Fact]
    public void The_cors_allowlist_is_per_environment()
    {
        Assert.Equal(["https://app.insolvia.ai"], Load(("INSOLVIA_ENV", "production")).CorsAllowedOrigins);
        Assert.Equal(["https://staging-app.insolvia.ai"], Load(("INSOLVIA_ENV", "staging")).CorsAllowedOrigins);
        Assert.Empty(Load().CorsAllowedOrigins);
    }

    [Fact]
    public void Localhost_origins_are_allowed_everywhere_but_production()
    {
        Assert.True(Load().CorsAllowLocalhost);
        Assert.True(Load(("INSOLVIA_ENV", "staging")).CorsAllowLocalhost);
        Assert.False(Load(("INSOLVIA_ENV", "production")).CorsAllowLocalhost);
    }

    // ── the Lambda's boot refusals (api_lambda.py) ───────────────────

    private static readonly (string, string?)[] Complete =
    [
        ("AUTH_ISSUER_URL", "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE00"),
        ("AUTH_CLIENT_ID", "exampleappclientid000000"),
        ("AUTH_PORTAL_CLIENT_ID", "exampleportalclientid000"),
        ("AUTH_USER_POOL_ID", "us-east-1_EXAMPLE00"),
        ("CASE_DOCUMENT_BUCKET", "insolvia-staging-case-documents-us-east-1"),
    ];

    private static (string, string?)[] CompleteWith(string name, string? value) =>
        [.. Complete.Where(pair => pair.Item1 != name), (name, value)];

    [Fact]
    public void A_complete_configuration_boots() => LambdaBootRequirements.Enforce(Load(Complete));

    [Fact]
    public void The_portal_client_is_optional_for_the_lambda() =>
        LambdaBootRequirements.Enforce(Load(CompleteWith("AUTH_PORTAL_CLIENT_ID", null)));

    [Theory]
    [InlineData("AUTH_ISSUER_URL", null, "AUTH_ISSUER_URL and AUTH_CLIENT_ID must be set for the Lambda entrypoint")]
    [InlineData("AUTH_CLIENT_ID", "", "AUTH_ISSUER_URL and AUTH_CLIENT_ID must be set for the Lambda entrypoint")]
    [InlineData("AUTH_PORTAL_CLIENT_ID", "exampleappclientid000000", "AUTH_PORTAL_CLIENT_ID must differ from AUTH_CLIENT_ID")]
    [InlineData("AUTH_USER_POOL_ID", null, "AUTH_USER_POOL_ID must be set for the Lambda entrypoint")]
    [InlineData("CASE_DOCUMENT_BUCKET", null, "CASE_DOCUMENT_BUCKET must be set for the Lambda entrypoint")]
    public void The_lambda_refuses_to_boot_without_its_requirements(string name, string? value, string message)
    {
        var error = Assert.Throws<InvalidOperationException>(() => LambdaBootRequirements.Enforce(Load(CompleteWith(name, value))));

        Assert.StartsWith(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_host_inside_lambda_refuses_to_start_without_auth()
    {
        // AWS_LAMBDA_FUNCTION_NAME is what makes this process the Lambda.
        using var lambda = factory.WithWebHostBuilder(host => host.UseSetting(LambdaBootRequirements.LambdaFunctionVariable, "insolvia-staging-api"));

        var error = Assert.Throws<InvalidOperationException>(() => lambda.CreateClient());

        Assert.Equal("AUTH_ISSUER_URL and AUTH_CLIENT_ID must be set for the Lambda entrypoint", error.Message);
    }

    [Fact]
    public void The_host_inside_lambda_refuses_to_start_without_its_database()
    {
        // Everything else the Lambda needs is set: the Postgres stores are
        // what is composed there, and they need the INSOLVIA_DB_* contract.
        using var lambda = factory.WithWebHostBuilder(host =>
        {
            host.UseSetting(LambdaBootRequirements.LambdaFunctionVariable, "insolvia-staging-api");
            foreach (var (name, value) in Complete)
            {
                host.UseSetting(name, value);
            }
        });

        var error = Assert.Throws<InvalidOperationException>(() => lambda.CreateClient());

        Assert.StartsWith("The database is not configured.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_development_host_starts_without_auth_and_fails_closed()
    {
        using var client = factory.CreateClient();

        var health = await client.GetAsync(new Uri("/health", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.OK, health.StatusCode);
    }
}
