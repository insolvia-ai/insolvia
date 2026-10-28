using Insolvia.Admin.Configuration;
using Insolvia.Admin.Http;
using Insolvia.Admin.Tests.Support;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Insolvia.Admin.Tests;

// The host around the routes: CORS (services/admin core/cors.py), the
// Werkzeug-shaped 404/405/OPTIONS, configuration and the Lambda boot.
public sealed class HostTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task A_loopback_origin_is_echoed_with_this_services_methods()
    {
        using var host = new AdminHost(factory);
        using var client = host.CreateClient();

        var answer = await client.SendAsync(HttpMethod.Get, "/health", headers: new Dictionary<string, string> { ["Origin"] = "http://localhost:3100" });

        Assert.Equal("http://localhost:3100", answer.Response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("GET, POST, PATCH, OPTIONS", answer.Response.Headers.GetValues("Access-Control-Allow-Methods").Single());
    }

    [Fact]
    public async Task A_preflight_runs_no_auth_and_a_foreign_origin_gets_no_headers()
    {
        using var host = new AdminHost(factory);
        using var client = host.CreateClient();

        var preflight = await client.SendAsync(HttpMethod.Options, "/v1/firms", headers: new Dictionary<string, string> { ["Origin"] = "http://127.0.0.1:3100" });
        var foreign = await client.SendAsync(HttpMethod.Get, "/health", headers: new Dictionary<string, string> { ["Origin"] = "https://evil.example.test" });

        Assert.Equal(200, preflight.Status);
        Assert.True(preflight.Response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(foreign.Response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public void Production_echoes_the_admin_portal_and_never_loopback()
    {
        var config = AdminConfig.Load(name => name == "INSOLVIA_ENV" ? "production" : null);

        Assert.True(Cors.OriginAllowed(config, "https://admin.insolvia.ai"));
        Assert.False(Cors.OriginAllowed(config, "http://localhost:3100"));
        Assert.False(Cors.OriginAllowed(config, "https://app.insolvia.ai"));
    }

    [Fact]
    public async Task An_unknown_path_and_a_wrong_method_answer_as_werkzeug()
    {
        using var host = new AdminHost(factory);
        using var client = host.CreateClient();

        var missing = await client.Get("/nope");
        var wrongMethod = await client.SendAsync(HttpMethod.Delete, "/v1/firms");

        Assert.Equal(404, missing.Status);
        Assert.Equal(ErrorResponses.WerkzeugHtml(404), missing.Body);
        Assert.Equal(405, wrongMethod.Status);
        Assert.Equal("HEAD, OPTIONS, GET, POST", string.Join(", ", wrongMethod.Response.Content.Headers.Allow));
    }

    [Fact]
    public void Configuration_reads_the_python_names()
    {
        var config = AdminConfig.Load(name => name switch
        {
            "INSOLVIA_ENV" => "staging",
            "FIRM_USER_POOL_ID" => "us-east-1_EXAMPLE00",
            "GOOGLE_CLIENT_ID" => "000000000000-fake.apps.googleusercontent.com",
            "GOOGLE_JWKS_URL" => "http://127.0.0.1:9999/jwks.json",
            _ => null,
        });

        Assert.Equal("staging", config.Environment.Name);
        Assert.Equal("us-east-1_EXAMPLE00", config.FirmUserPoolId);
        Assert.Equal(AdminConfig.WorkspaceDomainName, config.WorkspaceDomain);
        Assert.Equal(["https://staging-admin.insolvia.ai"], config.CorsAllowedOrigins);

        // The local key override is a LOCAL affordance: staging ignores it.
        Assert.Null(config.LocalJwksUrl);
        Assert.Equal(
            new Uri("http://127.0.0.1:9999/jwks.json"),
            AdminConfig.Load(name => name == "GOOGLE_JWKS_URL" ? "http://127.0.0.1:9999/jwks.json" : null).LocalJwksUrl);
    }

    [Fact]
    public void A_misspelt_environment_is_refused()
    {
        Assert.Throws<ArgumentException>(() => AdminConfig.Load(name => name == "INSOLVIA_ENV" ? "prod" : null));
    }

    [Theory]
    [InlineData(null, "000000000000-fake.apps.googleusercontent.com", "FIRM_USER_POOL_ID")]
    [InlineData("us-east-1_EXAMPLE00", null, "GOOGLE_CLIENT_ID")]
    public void The_lambda_refuses_to_boot_without_its_requirements(string? pool, string? clientId, string named)
    {
        var config = AdminConfig.Load(_ => null) with { FirmUserPoolId = pool, GoogleClientId = clientId };

        var refusal = Assert.Throws<InvalidOperationException>(() => LambdaBootRequirements.Enforce(config));
        Assert.StartsWith(named, refusal.Message, StringComparison.Ordinal);
    }
}
