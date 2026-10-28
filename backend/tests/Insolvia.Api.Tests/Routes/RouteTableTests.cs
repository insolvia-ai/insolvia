using System.Text.Json.Nodes;
using Insolvia.Api.Auth;
using Insolvia.Api.Http;
using Insolvia.Api.Tests.Support;

namespace Insolvia.Api.Tests.Routes;

/// <summary>
/// The firm-side route table as a whole: every route 18.14 maps is either
/// one of the two deliberately public ones or answers 401 without a token —
/// which also proves no handler bound as a raw <c>RequestDelegate</c> (that
/// would skip its filters silently).
/// </summary>
public sealed class RouteTableTests : IDisposable
{
    private static readonly string[] Public = ["POST /v1/waitlist", "POST /v1/unsubscribe"];

    private readonly FirmWorld _world = new();

    public static TheoryData<string, string> FirmSideRoutes => new()
    {
        { "GET", "/v1/me" },
        { "PATCH", "/v1/me" },
        { "GET", "/v1/firm" },
        { "PATCH", "/v1/firm" },
        { "GET", "/v1/firm/directory" },
        { "GET", "/v1/firm/users" },
        { "POST", "/v1/firm/users" },
        { "PATCH", "/v1/firm/users/{subject}" },
        { "DELETE", "/v1/firm/users/{subject}" },
        { "GET", "/v1/firm/clients" },
        { "POST", "/v1/firm/clients" },
        { "GET", "/v1/firm/clients/{clientId}" },
        { "PUT", "/v1/firm/clients/{clientId}" },
        { "PUT", "/v1/firm/clients/{clientId}/status" },
        { "PUT", "/v1/firm/clients/{clientId}/prospect-stage" },
        { "POST", "/v1/firm/clients/{clientId}/merge" },
        { "GET", "/v1/firm/creditors" },
        { "POST", "/v1/firm/creditors" },
        { "GET", "/v1/firm/creditors/{creditorId}" },
        { "PUT", "/v1/firm/creditors/{creditorId}" },
        { "DELETE", "/v1/firm/creditors/{creditorId}" },
        { "GET", "/v1/firm/questionnaire" },
        { "PUT", "/v1/firm/questionnaire" },
        { "DELETE", "/v1/firm/questionnaire" },
        { "GET", "/v1/firm/document-checklist" },
        { "PUT", "/v1/firm/document-checklist" },
        { "DELETE", "/v1/firm/document-checklist" },
    };

    public void Dispose() => _world.Dispose();

    [Theory]
    [MemberData(nameof(FirmSideRoutes))]
    public async Task Every_firm_side_route_is_a_staff_route_and_401s_without_a_token(string method, string template)
    {
        var path = template.Replace("{subject}", Ids.Bob, StringComparison.Ordinal).Replace("{clientId}", "c-1", StringComparison.Ordinal).Replace("{creditorId}", "c-1", StringComparison.Ordinal);

        var answer = await _world.SendAsync(new HttpMethod(method), path, json: "{}");

        Assert.Equal(401, answer.Status);
        Assert.Equal(ErrorBodies.Unauthorized, answer.Body);
        var endpoint = Endpoints().Single(e => e.Name == $"{method} {template}");
        Assert.Equal(PrincipalClass.Staff, endpoint.Principal);
    }

    [Fact]
    public void The_route_table_is_exactly_the_firm_side_slice_plus_the_two_public_routes()
    {
        var mapped = Endpoints().Where(e => e.Name.Contains("/v1/", StringComparison.Ordinal)).Select(e => e.Name).Order(StringComparer.Ordinal);
        var expected = FirmSideRoutes.Select(row => $"{row.Data.Item1} {row.Data.Item2}").Concat(Public).Order(StringComparer.Ordinal);

        Assert.Equal(expected, mapped);
        Assert.All(Endpoints().Where(e => Public.Contains(e.Name)), e => Assert.Null(e.Principal));
    }

    private List<(string Name, PrincipalClass? Principal)> Endpoints() =>
        [.. _world.Host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/v1/", StringComparison.Ordinal) == true)
            .SelectMany(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? []).Select(m => ($"{m} {e.RoutePattern.RawText}", e.Metadata.GetMetadata<PrincipalClass>())))];
}

/// <summary>
/// <see cref="FlaskJson"/>: the bytes Flask's jsonify writes — sorted keys at
/// every depth, compact, ensure_ascii — and <c>fields.prune</c>.
/// </summary>
public sealed class FlaskJsonTests
{
    [Fact]
    public void Keys_are_sorted_at_every_depth_and_separators_are_compact()
    {
        var node = JsonNode.Parse("""{"b": 1, "a": {"z": true, "y": [ {"d": null, "c": "x"} ]}}""");

        Assert.Equal("""{"a":{"y":[{"c":"x","d":null}],"z":true},"b":1}""", FlaskJson.Serialize(node));
    }

    [Theory]
    [InlineData("Example & Partners", "\"Example & Partners\"")]
    [InlineData("doesn't <b>", "\"doesn't <b>\"")]
    [InlineData("café — ok", "\"caf\\u00e9 \\u2014 ok\"")]
    [InlineData("\U0001F600", "\"\\ud83d\\ude00\"")]
    [InlineData("q\"b\\s/\n\r\t\b\f\u0001\u007f", "\"q\\\"b\\\\s/\\n\\r\\t\\b\\f\\u0001\\u007f\"")]
    public void Strings_are_escaped_as_python_ensure_ascii_does(string value, string expected)
    {
        Assert.Equal(expected, FlaskJson.Serialize(JsonValue.Create(value)));
    }

    [Fact]
    public void Prune_drops_nulls_and_emptied_containers_but_keeps_answers()
    {
        var pruned = FlaskJson.Prune(JsonNode.Parse("""{"a": null, "b": {"c": null}, "d": [], "e": false, "f": 0, "g": "", "h": [null, {}], "i": {"j": {"k": null}}}"""));

        Assert.Equal("""{"e":false,"f":0,"g":"","h":[null,{}]}""", FlaskJson.Serialize(pruned));
    }

    [Fact]
    public void Timestamps_are_pythons_isoformat_with_a_z()
    {
        var at = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero).AddTicks(1234567);

        Assert.Equal("2026-01-02T03:04:05.123Z", FlaskJson.Millis(at));
        Assert.Equal("2026-01-02T03:04:05.123456Z", FlaskJson.Micros(at));
        Assert.Equal("2026-01-02T03:04:05.000000Z", FlaskJson.Micros(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)));
    }
}
