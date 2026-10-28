using System.Net;
using System.Text.Json.Nodes;
using Insolvia.Core;
using Insolvia.Mcp.Configuration;
using Insolvia.Mcp.Http;
using Insolvia.Mcp.Tests.Support;
using static Insolvia.Mcp.Tests.Support.World;

namespace Insolvia.Mcp.Tests;

/// <summary>
/// services/mcp/tests/unit/test_config.py, plus the boot rules of
/// mcp_lambda.py and what test_lambda_handler.py pinned on a deployed
/// environment's shape (metadata served on every invocation, the challenge
/// naming the environment's own hostname).
/// </summary>
public sealed class ConfigTests
{
    private static McpConfig Load(params (string Name, string Value)[] variables)
    {
        var map = variables.ToDictionary(pair => pair.Name, pair => pair.Value, StringComparer.Ordinal);
        return McpConfig.Load(name => map.GetValueOrDefault(name));
    }

    [Fact]
    public void Defaults_are_local_and_unconfigured()
    {
        var config = Load();
        Assert.Equal("local", config.Environment);
        Assert.Null(config.AuthIssuerUrl);
        Assert.Empty(config.AuthClientIds);
        Assert.Equal("http://127.0.0.1:8788/mcp", config.ResourceUrl);
    }

    [Fact]
    public void Unknown_environment_is_rejected() =>
        Assert.Throws<ValidationException>(() => Load(("INSOLVIA_ENV", "sandbox")));

    [Theory]
    [InlineData("staging", "https://staging-mcp.insolvia.ai/mcp")]
    [InlineData("production", "https://mcp.insolvia.ai/mcp")]
    public void Resource_url_follows_the_environment(string environment, string expected) =>
        Assert.Equal(expected, Load(("INSOLVIA_ENV", environment)).ResourceUrl);

    [Fact]
    public void Resource_url_override_is_local_devs_seam() =>
        Assert.Equal("http://127.0.0.1:9000/mcp", Load(("MCP_RESOURCE_URL", "http://127.0.0.1:9000/mcp")).ResourceUrl);

    [Fact]
    public void Empty_values_read_as_unset()
    {
        var config = Load(("AUTH_ISSUER_URL", string.Empty), ("INSOLVIA_ENV", string.Empty));
        Assert.Null(config.AuthIssuerUrl);
        Assert.Equal("local", config.Environment);
    }

    [Fact]
    public void The_client_allowlist_drops_empties_and_whitespace() =>
        Assert.Equal(["a", "b"], Load(("AUTH_CLIENT_IDS", " a, ,b ,")).AuthClientIds);

    [Fact]
    public void The_metadata_url_is_the_resource_with_the_well_known_prefix()
    {
        Assert.Equal("https://staging-mcp.insolvia.ai/.well-known/oauth-protected-resource/mcp", Load(("INSOLVIA_ENV", "staging")).ResourceMetadataUrl.ToString());
        Assert.Equal("/.well-known/oauth-protected-resource/mcp", Load().ResourceMetadataPath);
    }

    [Fact]
    public void A_deployed_environment_answers_only_its_own_host()
    {
        Assert.Equal(["staging-mcp.insolvia.ai"], TransportSecurity.AllowedHosts(Load(("INSOLVIA_ENV", "staging"))));
        Assert.Equal(["127.0.0.1:8788", "127.0.0.1:*", "localhost:*", "127.0.0.1", "localhost"], TransportSecurity.AllowedHosts(Load()));
    }

    // ── mcp_lambda.py: refuse to boot ───────────────────────────────

    [Theory]
    [InlineData(null, "c")]
    [InlineData(Issuer, null)]
    public void The_lambda_refuses_to_boot_without_auth(string? issuer, string? clients)
    {
        var settings = new Dictionary<string, string?>
        {
            ["AWS_LAMBDA_FUNCTION_NAME"] = "insolvia-staging-mcp",
            ["AUTH_ISSUER_URL"] = issuer,
            ["AUTH_CLIENT_IDS"] = clients,
        };
        var error = Assert.ThrowsAny<Exception>(() => new McpHost(settings).Services);
        Assert.Contains("AUTH_ISSUER_URL and AUTH_CLIENT_IDS must be set for the Lambda entrypoint", Flatten(error), StringComparison.Ordinal);
    }

    [Fact]
    public void The_lambda_refuses_to_boot_without_its_database()
    {
        // The Python pair rule (case data never without its access log) is
        // one database here: no INSOLVIA_DB_* contract, no boot.
        var settings = new Dictionary<string, string?> { ["AWS_LAMBDA_FUNCTION_NAME"] = "insolvia-staging-mcp" };
        var error = Assert.ThrowsAny<Exception>(() => new McpHost(settings).Services);
        Assert.Contains("INSOLVIA_DB_", Flatten(error), StringComparison.Ordinal);
    }

    // ── test_lambda_handler.py: a deployed environment's shape ───────

    [Fact]
    public async Task A_staging_host_serves_its_metadata_and_challenges_with_its_own_hostname()
    {
        using var host = new McpHost(new Dictionary<string, string?> { ["INSOLVIA_ENV"] = "staging" }, baseAddress: "https://staging-mcp.insolvia.ai");
        for (var invocation = 0; invocation < 2; invocation++)
        {
            using var response = await host.Http.GetAsync(new Uri("/.well-known/oauth-protected-resource/mcp", UriKind.Relative), Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var metadata = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
            Assert.Equal("https://staging-mcp.insolvia.ai/mcp", (string?)metadata["resource"]);
            Assert.Equal([Issuer], metadata["authorization_servers"]!.AsArray().Select(node => (string?)node));
        }

        var challenged = await host.RpcAsync("tools/list");
        Assert.Equal(HttpStatusCode.Unauthorized, challenged.Status);
        Assert.Contains("resource_metadata=\"https://staging-mcp.insolvia.ai/.well-known/oauth-protected-resource/mcp\"", challenged.WwwAuthenticate, StringComparison.Ordinal);

        // Loopback is a local convenience only: staging answers its own name.
        await host.SeededAsync();
        Assert.Equal(HttpStatusCode.OK, (await host.RpcAsync("tools/list", headers: McpHost.Auth())).Status);
        Assert.Equal(HttpStatusCode.MisdirectedRequest, (await host.RpcAsync("tools/list", headers: new(McpHost.Auth()) { ["Host"] = "127.0.0.1:8788" })).Status);
    }

    private static string Flatten(Exception error) =>
        error.InnerException is null ? error.Message : error.Message + " | " + Flatten(error.InnerException);
}
