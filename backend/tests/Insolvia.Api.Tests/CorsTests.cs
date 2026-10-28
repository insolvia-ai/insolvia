using Insolvia.Api.Configuration;
using Insolvia.Api.Http;
using Insolvia.Api.Tests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using static Insolvia.Api.Tests.Support.HttpAssert;

namespace Insolvia.Api.Tests;

// Translated from services/api tests/unit/test_cors.py, case for case, plus
// the headers Flask's after_request put on preflights and refusals.
public sealed class CorsTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Acao = "Access-Control-Allow-Origin";

    private HttpClient ClientFor(string environment) =>
        factory.WithWebHostBuilder(host => host.UseSetting("INSOLVIA_ENV", environment)).CreateClient();

    private static Task<(int Status, string Body, HttpResponseMessage Response)> Get(HttpClient client, string path, string? origin, HttpMethod? method = null)
    {
        var headers = new Dictionary<string, string>();
        if (origin is not null)
        {
            headers["Origin"] = origin;
        }

        if (method == HttpMethod.Options)
        {
            headers["Access-Control-Request-Method"] = "POST";
        }

        return client.SendAsync(method ?? HttpMethod.Get, path, headers: headers);
    }

    [Theory]
    [InlineData("production", "https://app.insolvia.ai")]
    [InlineData("staging", "https://staging-app.insolvia.ai")]
    [InlineData("staging", "http://localhost:5173")]
    [InlineData("staging", "http://127.0.0.1:8081")]
    [InlineData("local", "http://localhost:8081")]
    [InlineData("local", "http://[::1]:3000")]
    public async Task An_allowed_origin_is_echoed_exactly(string environment, string origin)
    {
        using var client = ClientFor(environment);

        var answer = await Get(client, "/health", origin);

        Assert.Equal([origin], answer.Response.Headers.GetValues(Acao));
        Assert.Contains("Origin", answer.Response.Headers.Vary);
        Assert.Equal([Cors.AllowMethods], answer.Response.Headers.GetValues("Access-Control-Allow-Methods"));
        Assert.Equal([Cors.AllowHeaders], answer.Response.Headers.GetValues("Access-Control-Allow-Headers"));
        Assert.Equal(["600"], answer.Response.Headers.GetValues("Access-Control-Max-Age"));
    }

    [Theory]
    [InlineData("production", "https://staging-app.insolvia.ai")]
    [InlineData("staging", "https://app.insolvia.ai")]
    [InlineData("production", "http://localhost:5173")]
    [InlineData("production", "https://www.insolvia.ai")]
    [InlineData("staging", "https://www.insolvia.ai")]
    [InlineData("local", "https://evil.example")]
    [InlineData("production", "https://app.insolvia.ai.evil.example")]
    [InlineData("staging", "http://localhost.evil.example")]
    public async Task A_disallowed_origin_gets_no_cors_headers(string environment, string origin)
    {
        using var client = ClientFor(environment);

        var answer = await Get(client, "/health", origin);

        Assert.False(answer.Response.Headers.Contains(Acao));
        Assert.False(answer.Response.Headers.Contains("Access-Control-Allow-Methods"));
        Assert.Contains("Origin", answer.Response.Headers.Vary);
    }

    [Theory]
    [InlineData("local")]
    [InlineData("staging")]
    [InlineData("production")]
    public async Task No_origin_header_means_no_cors_headers(string environment)
    {
        using var client = ClientFor(environment);

        Assert.False((await Get(client, "/health", null)).Response.Headers.Contains(Acao));
    }

    [Fact]
    public async Task Never_a_wildcard()
    {
        foreach (var environment in new[] { "local", "staging", "production" })
        {
            using var client = ClientFor(environment);
            var answer = await Get(client, "/health", "https://app.insolvia.ai");
            Assert.False(answer.Response.Headers.TryGetValues(Acao, out var values) && values.Contains("*"));
        }
    }

    [Fact]
    public async Task A_preflight_gets_cors_headers_and_an_empty_200()
    {
        using var client = ClientFor("production");

        var answer = await Get(client, "/health", "https://app.insolvia.ai", HttpMethod.Options);

        Assert.Equal(200, answer.Status);
        Assert.Equal(string.Empty, answer.Body);
        Assert.Equal(["https://app.insolvia.ai"], answer.Response.Headers.GetValues(Acao));
        Assert.Contains("POST", answer.Response.Headers.GetValues("Access-Control-Allow-Methods").Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_preflight_from_a_disallowed_origin_is_still_200_but_bare()
    {
        using var client = ClientFor("production");

        var answer = await Get(client, "/health", "https://evil.example", HttpMethod.Options);

        Assert.Equal(200, answer.Status);
        Assert.False(answer.Response.Headers.Contains(Acao));
    }

    [Fact]
    public async Task Refusals_carry_cors_headers_too()
    {
        // A browser must be able to READ a 401 or a 404 to act on it.
        using var host = new AuthHost(factory);
        using var client = host.CreateClient();
        var origin = new Dictionary<string, string> { ["Origin"] = "http://localhost:3000" };

        foreach (var path in new[] { "/test/staff", "/nope" })
        {
            var answer = await client.SendAsync(HttpMethod.Get, path, headers: origin);
            Assert.Equal(["http://localhost:3000"], answer.Response.Headers.GetValues(Acao));
        }
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("ftp://localhost")]
    [InlineData("http://localhost/path")]
    [InlineData("http://localhost?x=1")]
    [InlineData("http://localhost#frag")]
    [InlineData("null")]
    [InlineData("")]
    public void Origin_allowed_rejects_garbage_origins(string origin) =>
        Assert.False(Cors.OriginAllowed(ApiConfig.Load(_ => null), origin));
}
