using System.Text.Json.Nodes;
using Insolvia.Api.Auth;
using Insolvia.Api.Tests.Support;
using Insolvia.Domain;

namespace Insolvia.Api.Tests.OpenApi;

/// <summary>
/// <c>docs/reference/openapi/api.json</c> against the routes the API maps.
/// The document is generated from a hand-kept table (tool/openapi/api-routes.ts),
/// so this is what notices a route added, removed or re-permissioned without
/// it: every route is in the document and nothing else is, each with the
/// principal its filter admits and exactly the permissions it requires. The
/// contract suite checks the schemas against real exchanges.
/// </summary>
public sealed class OpenApiDocumentTests : IDisposable
{
    private const string Regenerate = "Edit tool/openapi/api-routes.ts, then run `npm run openapi`.";

    private readonly FirmWorld _world = new();

    public void Dispose() => _world.Dispose();

    [Fact]
    public void Every_route_the_api_maps_is_documented_and_nothing_else_is()
    {
        var mapped = Mapped().Keys.Order(StringComparer.Ordinal).ToList();
        var documented = Documented().Keys.Order(StringComparer.Ordinal).ToList();

        Assert.True(
            mapped.SequenceEqual(documented),
            $"Undocumented: {string.Join(", ", mapped.Except(documented))}. Not mapped: {string.Join(", ", documented.Except(mapped))}. {Regenerate}");
    }

    [Fact]
    public void Each_route_documents_the_principal_its_filter_admits()
    {
        var documented = Documented();
        var wrong = Mapped()
            .Where(route => documented.ContainsKey(route.Key))
            .Where(route => !documented[route.Key].Schemes.Contains(route.Value.Principal?.Name ?? "none"))
            .Select(route => $"{route.Key} admits {route.Value.Principal?.Name ?? "anyone"}")
            .ToList();

        Assert.True(wrong.Count == 0, $"{string.Join("; ", wrong)}. {Regenerate}");
    }

    [Fact]
    public void Each_route_documents_exactly_the_permissions_it_requires()
    {
        var documented = Documented();
        var wrong = Mapped()
            .Where(route => documented.ContainsKey(route.Key))
            .Where(route => !route.Value.Permissions.SetEquals(documented[route.Key].Permissions))
            .Select(route => $"{route.Key} requires [{string.Join(", ", route.Value.Permissions.Order(StringComparer.Ordinal))}]")
            .ToList();

        Assert.True(wrong.Count == 0, $"{string.Join("; ", wrong)}. {Regenerate}");
    }

    private Dictionary<string, (PrincipalClass? Principal, HashSet<string> Permissions)> Mapped() =>
        _world.Host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            // FirmWorld's own probes (AuthHost), not the API's.
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/test/", StringComparison.Ordinal) != true)
            .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [])
                .Select(method => (
                    Key: $"{method} {endpoint.RoutePattern.RawText}",
                    Principal: endpoint.Metadata.GetMetadata<PrincipalClass>(),
                    Permissions: endpoint.Metadata.GetOrderedMetadata<PermissionRequirement>()
                        .Select(required => $"{required.Feature.ToWire()}:{required.Level.ToWire()}")
                        .ToHashSet(StringComparer.Ordinal))))
            .ToDictionary(route => route.Key, route => (route.Principal, route.Permissions), StringComparer.Ordinal);

    private static Dictionary<string, (HashSet<string> Schemes, HashSet<string> Permissions)> Documented()
    {
        var document = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "OpenApi", "api.json")))!;
        var routes = new Dictionary<string, (HashSet<string>, HashSet<string>)>(StringComparer.Ordinal);
        foreach (var (path, operations) in document["paths"]!.AsObject())
        {
            foreach (var (method, operation) in operations!.AsObject())
            {
                var schemes = operation!["security"]!.AsArray()
                    .SelectMany(requirement => requirement!.AsObject().Select(scheme => scheme.Key))
                    .ToHashSet(StringComparer.Ordinal);
                if (schemes.Count == 0)
                {
                    schemes.Add("none");
                }

                var permissions = (operation["x-insolvia-permissions"]?.AsArray() ?? [])
                    .Select(permission => permission!.GetValue<string>())
                    .ToHashSet(StringComparer.Ordinal);
                routes[$"{method.ToUpperInvariant()} {path}"] = (schemes, permissions);
            }
        }

        return routes;
    }
}
