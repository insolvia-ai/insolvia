using System.Text.Json.Nodes;
using Insolvia.Admin.Tests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;

namespace Insolvia.Admin.Tests;

/// <summary>
/// <c>docs/reference/openapi/admin.json</c> against the routes the admin
/// service maps (the API's <c>OpenApiDocumentTests</c> says why). Every route
/// but <c>/health</c> takes a staff Google token, which FirmRouteTests proves
/// route by route; here, the document must say so.
/// </summary>
public sealed class OpenApiDocumentTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Regenerate = "Edit tool/openapi/admin-routes.ts, then run `npm run openapi`.";

    [Fact]
    public void Every_route_the_service_maps_is_documented_with_its_principal_and_nothing_else_is()
    {
        using var host = new AdminHost(factory);
        var mapped = host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [])
                .Select(method => $"{method} {endpoint.RoutePattern.RawText}"))
            .ToDictionary(route => route, route => route.EndsWith(" /health", StringComparison.Ordinal) ? "none" : "google", StringComparer.Ordinal);

        var document = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "OpenApi", "admin.json")))!;
        var documented = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, operations) in document["paths"]!.AsObject())
        {
            foreach (var (method, operation) in operations!.AsObject())
            {
                var schemes = operation!["security"]!.AsArray().SelectMany(requirement => requirement!.AsObject().Select(scheme => scheme.Key)).ToList();
                documented[$"{method.ToUpperInvariant()} {path}"] = schemes.Count == 0 ? "none" : string.Join(",", schemes);
            }
        }

        Assert.True(
            mapped.OrderBy(route => route.Key, StringComparer.Ordinal).SequenceEqual(documented.OrderBy(route => route.Key, StringComparer.Ordinal)),
            $"Mapped: {string.Join("; ", mapped.Select(route => $"{route.Key} ({route.Value})"))}. Documented: {string.Join("; ", documented.Select(route => $"{route.Key} ({route.Value})"))}. {Regenerate}");
    }
}
