using System.Text.Json.Nodes;
using Insolvia.Api.Tests.Support;
using Insolvia.Core.AccessLog;
using static Insolvia.Api.Tests.Support.CaseWorld;

namespace Insolvia.Api.Tests.Routes;

/// <summary>
/// test_liens_route.py: <c>GET …/liens</c> — the wiring (a stored claim and
/// its asset reach the arithmetic), the authorisation of the collection reads
/// it is a read over, and the static segment not swallowed by
/// <c>{collection}</c>. The arithmetic is the Engines' LienTests'.
/// </summary>
public sealed class LienRouteTests : IAsyncLifetime, IDisposable
{
    private readonly AnalysisWorld _world = new();

    public async ValueTask InitializeAsync() => await _world.InitializeAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task The_route_refuses_an_unauthenticated_caller()
    {
        Assert.Equal(401, (await _world.Get("/v1/cases/any-id/liens", subject: null)).Status);
    }

    [Fact]
    public async Task An_unknown_case_is_not_found()
    {
        Assert.Equal(404, (await Liens("no-such-case")).Status);
    }

    [Fact]
    public async Task Another_firms_liens_are_the_same_404_as_no_case_and_the_read_is_logged_denied()
    {
        var caseId = await OpenAsync();

        var foreign = await Liens(caseId, Carol);
        var logged = _world.World.Events[^1];

        Assert.Equal(404, foreign.Status);
        Assert.Equal((AccessAction.CaseRead, AccessOutcome.Denied, caseId), (logged.Action, logged.Outcome, logged.CaseId));
        Assert.Equal((await Liens("no-such-case")).Body, foreign.Body);
    }

    [Fact]
    public async Task The_static_segment_is_not_shadowed_by_the_collection_routes()
    {
        var caseId = await OpenAsync();

        var answer = await Liens(caseId);

        Assert.Equal(200, answer.Status);
        Assert.Equal("""{"assets":[],"claims":[]}""", answer.Body);
    }

    [Fact]
    public async Task A_stored_claim_and_its_asset_reach_the_arithmetic()
    {
        var caseId = await OpenAsync();
        var house = await _world.AddAsync(caseId, "assets", """{"category": "real_property", "value_entire": "300000.00"}""");
        var first = await _world.AddAsync(caseId, "claims", $$"""{"claim_class": "secured", "amount": "250000.00", "asset_id": "{{house}}", "lien_position": 1}""");
        var second = await _world.AddAsync(caseId, "claims", $$"""{"claim_class": "secured", "amount": "80000.00", "asset_id": "{{house}}", "lien_position": 2}""");

        var body = (await Liens(caseId)).Object;

        Assert.Equal($$"""[{"assetId":"{{house}}","claimIds":["{{first}}","{{second}}"],"securedTotal":"330000.00"}]""", Http.FlaskJson.Serialize(body["assets"]));
        var rows = Rows(body);
        Assert.Equal("0.00", rows[first].Str("unsecuredAmount"));
        Assert.Equal("250000.00", rows[second].Str("seniorLiens"));
        Assert.Equal("30000.00", rows[second].Str("unsecuredAmount"));
        Assert.Equal("50000.00", rows[second].Str("securedAmount"));
    }

    // The issue's "senior-lien ordering changes the deficiency correctly",
    // over the API: swap the positions with a PUT and the shortfall moves.
    [Fact]
    public async Task Moving_a_lien_senior_changes_the_deficiency()
    {
        var caseId = await OpenAsync();
        var house = await _world.AddAsync(caseId, "assets", """{"category": "real_property", "value_entire": "300000.00"}""");
        var big = await _world.AddAsync(caseId, "claims", $$"""{"claim_class": "secured", "amount": "250000.00", "asset_id": "{{house}}", "lien_position": 1}""");
        await _world.AddAsync(caseId, "claims", $$"""{"claim_class": "secured", "amount": "80000.00", "asset_id": "{{house}}", "lien_position": 2}""");
        var demoted = await _world.Put($"/v1/cases/{caseId}/claims/{big}", AnalysisWorld.Typed($$"""{"claim_class": "secured", "amount": "250000.00", "asset_id": "{{house}}", "lien_position": 3}"""));
        Assert.True(demoted.Status == 200, demoted.Body);

        var rows = Rows((await Liens(caseId)).Object);

        Assert.Equal("80000.00", rows[big].Str("seniorLiens"));
        Assert.Equal("30000.00", rows[big].Str("unsecuredAmount"));
    }

    [Fact]
    public async Task Money_is_a_string_never_a_json_number()
    {
        var caseId = await OpenAsync();
        await _world.AddAsync(caseId, "claims", """{"claim_class": "secured", "amount": "8412.66"}""");

        var row = Assert.Single((await Liens(caseId)).Object["claims"]!.AsArray())!.AsObject();

        Assert.Equal("8412.66", row.Str("amount"));
        Assert.Equal(System.Text.Json.JsonValueKind.String, row["seniorLiens"]!.GetValueKind());
    }

    private static Dictionary<string, JsonObject> Rows(JsonObject body) =>
        body["claims"]!.AsArray().Select(row => row!.AsObject()).ToDictionary(row => row.Str("claimId")!, StringComparer.Ordinal);

    private Task<string> OpenAsync() => _world.OpenCaseAsync(court: "flnb", division: "tallahassee");

    private Task<Answer> Liens(string caseId, string subject = Alice) => _world.Get($"/v1/cases/{caseId}/liens", subject);
}
