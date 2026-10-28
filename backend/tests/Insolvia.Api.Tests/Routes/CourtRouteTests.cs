using System.Text.Json.Nodes;
using Insolvia.Api.Tests.Support;
using static Insolvia.Api.Tests.Support.CaseWorld;

namespace Insolvia.Api.Tests.Routes;

/// <summary>test_courts_route.py: <c>GET /v1/courts</c> — the shipped registry's release in force, to any member of a firm.</summary>
public sealed class CourtRouteTests : IAsyncLifetime, IDisposable
{
    private readonly CaseWorld _world = new();

    public async ValueTask InitializeAsync() => await _world.SeedAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task The_registry_needs_a_token_and_a_firm()
    {
        Assert.Equal(401, (await _world.Get("/v1/courts", null)).Status);
        Assert.Equal(403, (await _world.Get("/v1/courts", Frank)).Status);
    }

    [Fact]
    public async Task Every_member_reads_the_ten_launch_districts()
    {
        var body = (await _world.Get("/v1/courts", Greg)).Object;

        Assert.Equal(("courts/us-bankruptcy@2026-10-06", "2026-10-06"), (body.Str("releaseId"), body.Str("effectiveDate")));
        Assert.Equal(["flmb", "flnb", "flsb", "gamb", "ganb", "gasb", "txeb", "txnb", "txsb", "txwb"], Districts(body).Select(d => d.Str("code")));
    }

    [Fact]
    public async Task A_district_carries_what_a_picker_and_a_caption_need()
    {
        var flmb = Districts((await _world.Get("/v1/courts", Greg)).Object).Single(d => d.Str("code") == "flmb");

        Assert.Equal(("Middle District of Florida", "FLMBK", "FL", 11), (flmb.Str("name"), flmb.Str("courtId"), flmb.Str("state"), flmb["circuit"]!.GetValue<int>()));
        Assert.Equal("""{"status":"unverified","verifiedAt":null}""", Http.FlaskJson.Serialize(flmb["caseUpload"]));
        var tampa = flmb["divisions"]!.AsArray().Select(d => d!.AsObject()).Single(d => d.Str("code") == "tampa");
        Assert.Equal(("Tampa Division", "8", true, "Tampa"), (tampa.Str("name"), tampa.Str("officeCode"), tampa["officeCodeVerified"]!.GetValue<bool>(), tampa["courthouse"]!["city"]!.GetValue<string>()));
        Assert.Contains("""{"fips":"12057","name":"Hillsborough"}""", tampa["counties"]!.AsArray().Select(Http.FlaskJson.Serialize));
        Assert.Equal(["code", "counties", "courthouse", "name", "officeCode", "officeCodeVerified"], Answer.Keys(tampa));
        Assert.Equal(["caseUpload", "circuit", "code", "courtId", "divisions", "name", "state", "website"], Answer.Keys(flmb));
    }

    [Fact]
    public async Task An_unverified_office_code_is_null_and_says_so()
    {
        var txsb = Districts((await _world.Get("/v1/courts", Greg)).Object).Single(d => d.Str("code") == "txsb");
        var houston = txsb["divisions"]!.AsArray().Select(d => d!.AsObject()).Single(d => d.Str("code") == "houston");

        Assert.Null(houston["officeCode"]);
        Assert.False(houston["officeCodeVerified"]!.GetValue<bool>());
    }

    private static IEnumerable<JsonObject> Districts(JsonObject body) => body["districts"]!.AsArray().Select(d => d!.AsObject());
}
