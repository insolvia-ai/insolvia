using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Insolvia.Admin.Tests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;

namespace Insolvia.Admin.Tests;

/// <summary>
/// <c>docs/reference/openapi/admin.json</c> is a committed copy of the
/// document the admin service generates and serves at <c>/openapi/v1.json</c>
/// (the API's <c>OpenApiDocumentTests</c> says why). These keep it current,
/// and keep every route in it with a typed answer and the right token.
/// </summary>
public sealed class OpenApiDocumentTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task The_reference_page_is_served_at_docs()
    {
        using var host = new AdminHost(factory);
        using var client = host.CreateClient();
        using var response = await client.GetAsync(new Uri("/docs", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    private const string Regenerate = "Run backend/scripts/dev-openapi.sh and commit docs/reference/openapi/admin.json.";

    [Fact]
    public async Task The_committed_document_is_the_one_the_admin_service_serves()
    {
        var served = await ServedAsync();
        var path = CommittedPath();
        if (Environment.GetEnvironmentVariable("INSOLVIA_WRITE_OPENAPI") == "1")
        {
            await File.WriteAllTextAsync(path, served.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", TestContext.Current.CancellationToken);
        }

        var committed = JsonNode.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.True(JsonNode.DeepEquals(served, committed), $"docs/reference/openapi/admin.json is out of date. {Regenerate}");
    }

    [Fact]
    public async Task Every_route_is_documented_with_its_token_and_what_it_answers()
    {
        using var host = new AdminHost(factory);
        var mapped = host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText is { } raw && !raw.StartsWith("/openapi/", StringComparison.Ordinal) && !raw.StartsWith("/docs", StringComparison.Ordinal))
            .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [])
                .Select(method => $"{method} {endpoint.RoutePattern.RawText}"))
            .ToList();

        var operations = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var (path, item) in (await ServedAsync())["paths"]!.AsObject())
        {
            foreach (var (method, operation) in item!.AsObject())
            {
                operations[$"{method.ToUpperInvariant()} {path}"] = operation!;
            }
        }

        foreach (var route in mapped)
        {
            Assert.True(operations.TryGetValue(route, out var operation), $"{route} is not in the document.");
            var schemes = operation["security"]?.AsArray().SelectMany(requirement => requirement!.AsObject().Select(scheme => scheme.Key)).ToList() ?? [];
            Assert.Equal(route.EndsWith(" /health", StringComparison.Ordinal) ? [] : ["google"], schemes);
            Assert.True(
                operation["responses"]!.AsObject().Any(response => response.Key == "204" || (response.Key.StartsWith('2') && response.Value?["content"] is JsonObject { Count: > 0 })),
                $"{route} documents no success body: return a typed result.");
        }
    }

    private async Task<JsonNode> ServedAsync()
    {
        using var host = new AdminHost(factory);
        using var client = host.CreateClient();
        var text = await client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken);
        return JsonNode.Parse(text)!;
    }

    // From this source file to the repository's docs/, so the write lands in the checkout.
    private static string CommittedPath([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "..", "docs", "reference", "openapi", "admin.json"));
}
