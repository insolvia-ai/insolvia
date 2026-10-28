using System.Text.Json.Nodes;
using Insolvia.Api.Tests.Support;
using Insolvia.Core.AccessLog;
using static Insolvia.Api.Tests.Support.CaseWorld;

namespace Insolvia.Api.Tests.Routes;

/// <summary>
/// test_case_summary_route.py, the route half (the arithmetic is Forms'
/// CaseSummaryAndHubTests): tenancy, the static segment, money as strings,
/// the column each claim lands in, liens, and readiness from the same gate
/// assembly runs.
/// </summary>
public sealed class CaseSummaryRouteTests : IAsyncLifetime, IDisposable
{
    private const string Typed = """{"source": "staff_typed"}""";

    private readonly CaseWorld _world = new();

    public async ValueTask InitializeAsync() => await _world.SeedAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _world.Dispose();

    private Task<Answer> Summary(string caseId, string subject = Alice) => _world.Get($"/v1/cases/{caseId}/summary", subject);

    private async Task<string> AddAsync(string caseId, string collection, JsonObject body)
    {
        var provenance = new JsonObject();
        foreach (var (key, _) in body)
        {
            provenance[key] = JsonNode.Parse(Typed);
        }

        body["provenance"] = provenance;
        var answer = await _world.Post($"/v1/cases/{caseId}/{collection}", body.ToJsonString());
        Assert.True(answer.Status == 201, answer.Body);
        return answer.Str("id")!;
    }

    private Task<string> AddCreditorAsync(string caseId) => AddAsync(caseId, "creditors", new JsonObject { ["name"] = "Example Bank" });

    private Task<string> AddClaimAsync(string caseId, string creditorId, string claimClass, params (string Key, JsonNode Value)[] fields)
    {
        var body = new JsonObject { ["creditor_id"] = creditorId, ["claim_class"] = claimClass };
        foreach (var (key, value) in fields)
        {
            body[key] = value;
        }

        return AddAsync(caseId, "claims", body);
    }

    [Fact]
    public async Task An_unknown_case_and_another_firms_are_the_same_404()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var missing = await Summary("no-such-case");
        var foreign = await Summary(caseId, Carol);
        Assert.Equal(404, foreign.Status);
        Assert.Equal(missing.Body, foreign.Body);
        Assert.Contains(_world.Events, e => e.Action == AccessAction.CaseRead && e.Outcome == AccessOutcome.Denied);
    }

    [Fact]
    public async Task The_static_segment_is_not_shadowed_by_the_collection_routes()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var answer = await Summary(caseId);
        Assert.Equal(200, answer.Status);
        Assert.Equal(["liens", "problems", "readyToFile", "totals"], Answer.Keys(answer.Object));
    }

    [Fact]
    public async Task An_empty_case_totals_zero_and_reports_no_liens()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var body = (await Summary(caseId)).Object;
        Assert.Equal(
            """{"assets":"0","liabilities":"0","monthlyExcess":"0","monthlyExpenses":"0","monthlyIncome":"0","nonpriorityUnsecured":"0","personalProperty":"0","priorityUnsecured":"0","realEstate":"0","secured":"0","totalExempt":"0","totalNonExempt":"0"}""",
            Sorted(body["totals"]!));
        Assert.Equal("""{"assets":[],"claims":[]}""", body["liens"]!.ToJsonString());
    }

    [Fact]
    public async Task Money_is_a_string_and_lands_in_the_column_its_kind_names()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var creditorId = await AddCreditorAsync(caseId);
        await AddClaimAsync(caseId, creditorId, "nonpriority_unsecured", ("amount", "8412.66"));
        await AddClaimAsync(caseId, creditorId, "secured", ("amount", "14500.00"));
        await AddClaimAsync(caseId, creditorId, "priority_unsecured", ("priority_amount", "50.00"));

        var totals = (await Summary(caseId)).Object["totals"]!.AsObject();

        Assert.All(totals, pair => Assert.Equal(System.Text.Json.JsonValueKind.String, pair.Value!.GetValueKind()));
        Assert.Equal("8412.66", totals["nonpriorityUnsecured"]!.GetValue<string>());
        Assert.Equal("14500.00", totals["secured"]!.GetValue<string>());
        Assert.Equal("50.00", totals["priorityUnsecured"]!.GetValue<string>());
        Assert.Equal("22962.66", totals["liabilities"]!.GetValue<string>());
    }

    [Fact]
    public async Task Linking_a_claim_to_an_asset_derives_both_figures_without_typing_either()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var creditorId = await AddCreditorAsync(caseId);
        var assetId = await AddAsync(caseId, "assets", new JsonObject { ["category"] = "vehicle", ["value_entire"] = "9000.00" });
        var claimId = await AddClaimAsync(caseId, creditorId, "secured", ("amount", "12000.00"), ("asset_id", assetId), ("lien_position", 1));

        var body = (await Summary(caseId)).Object;

        Assert.Equal($$"""[{"assetId":"{{assetId}}","claimIds":["{{claimId}}"],"securedTotal":"12000.00"}]""", body["liens"]!["assets"]!.ToJsonString());
        var row = Assert.Single(body["liens"]!["claims"]!.AsArray())!;
        Assert.Equal(claimId, row["claimId"]!.GetValue<string>());
        Assert.Equal("9000.00", row["securedAmount"]!.GetValue<string>());
        Assert.Equal("3000.00", row["unsecuredAmount"]!.GetValue<string>());
        Assert.Equal("derived", row["unsecuredSource"]!.GetValue<string>());
        Assert.Equal("12000.00", body["totals"]!["secured"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_bare_case_is_not_ready_and_every_problem_names_where_the_fix_belongs()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var body = (await Summary(caseId)).Object;
        Assert.False(body["readyToFile"]!.GetValue<bool>());
        var problems = body["problems"]!.AsArray();
        Assert.NotEmpty(problems);
        Assert.All(problems, problem =>
        {
            Assert.False(string.IsNullOrEmpty(problem!["source"]!.GetValue<string>()));
            Assert.False(string.IsNullOrEmpty(problem["message"]!.GetValue<string>()));
            Assert.True(problem["itemId"] is null || problem["itemId"]!.GetValueKind() == System.Text.Json.JsonValueKind.String);
        });
    }

    [Fact]
    public async Task The_read_is_access_logged_as_a_case_read()
    {
        var caseId = await _world.OpenCaseIdAsync();
        await Summary(caseId);
        Assert.Contains(_world.Events, e => e.Action == AccessAction.CaseRead && e.CaseId == caseId && e.Outcome == AccessOutcome.Allowed);
    }

    private static string Sorted(JsonNode node) =>
        new JsonObject([.. node.AsObject().OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, pair.Value?.DeepClone()))]).ToJsonString();
}
