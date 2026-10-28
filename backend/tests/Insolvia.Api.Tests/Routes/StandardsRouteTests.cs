using System.Text.Json;
using System.Text.Json.Nodes;
using Insolvia.Api.Tests.Support;
using static Insolvia.Api.Tests.Support.CaseWorld;

namespace Insolvia.Api.Tests.Routes;

/// <summary>
/// test_standards_route.py: <c>GET …/standards</c> — the IRS National and
/// Local Standards for the case's jurisdiction and household. Always 200 on
/// a reachable case, every figure that could not resolve a null with a
/// problem saying why. The lookups are the Engines' StandardsTests'.
/// </summary>
public sealed class StandardsRouteTests : IAsyncLifetime, IDisposable
{
    private static readonly string[] LocalFigures =
        ["releaseId", "housingNonMortgage", "housingMortgageRent", "transportationPublicNational", "transportationOwnershipOneCar", "transportationOwnershipTwoCars", "transportationOperatingOneCar", "transportationOperatingTwoCars"];

    private static readonly string[] NationalFigures = ["releaseId", "allowance", "oopHealthcareUnder65", "oopHealthcare65AndOlder"];

    private readonly AnalysisWorld _world = new();

    public async ValueTask InitializeAsync() => await _world.InitializeAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task The_route_refuses_an_unauthenticated_caller()
    {
        Assert.Equal(401, (await _world.Get("/v1/cases/any-id/standards", subject: null)).Status);
    }

    [Fact]
    public async Task An_unknown_case_is_not_found()
    {
        Assert.Equal(404, (await Standards("no-such-case")).Status);
    }

    [Fact]
    public async Task Another_firms_standards_are_the_same_404_as_no_case()
    {
        var caseId = await _world.OpenCaseAsync();

        Assert.Equal(404, (await Standards(caseId, Carol)).Status);
    }

    [Fact]
    public async Task The_static_segment_is_not_shadowed_by_the_collection_routes()
    {
        var caseId = await _world.OpenCaseAsync();

        var answer = await Standards(caseId);

        Assert.Equal(200, answer.Status);
        Assert.True(answer.Object.ContainsKey("nationalStandards"));
    }

    [Fact]
    public async Task A_case_with_no_debtor_1_reports_the_problem_not_an_error()
    {
        var caseId = await _world.OpenCaseAsync();

        var body = (await Standards(caseId)).Object;

        // The keys stay, null: the client labels the box that is missing.
        Assert.Null(body["state"]);
        Assert.True(body.ContainsKey("state"));
        Assert.Null(body["householdSize"]);
        Assert.Null(body["nationalStandards"]!["allowance"]);
        Assert.Contains(Problems(body), problem => problem.Contains("Debtor 1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_debtor_with_no_county_reports_the_problem_not_an_error()
    {
        var caseId = await _world.OpenCaseAsync();
        await PutDebtor1Async(caseId, state: "FL");

        var body = (await Standards(caseId)).Object;

        Assert.Null(body["state"]);
        Assert.Contains(Problems(body), problem => problem.Contains("state and county", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unsupported_jurisdiction_is_reported_but_the_national_figures_still_land()
    {
        var caseId = await _world.OpenCaseAsync();
        await PutDebtor1Async(caseId, state: "ZZ", county: "Nowhere County");

        var body = (await Standards(caseId)).Object;

        Assert.Equal("ZZ", body.Str("state"));
        // No jurisdiction-specific figures for a state the launch set does
        // not carry — but the National Standards apply everywhere.
        Assert.Null(body["localStandards"]!["housingNonMortgage"]);
        Assert.NotNull(body["nationalStandards"]!["allowance"]);
        Assert.NotEmpty(Problems(body));
    }

    [Fact]
    public async Task A_supported_county_returns_every_figure_and_no_problems()
    {
        var caseId = await _world.OpenCaseAsync();
        await PutDebtor1Async(caseId, state: "FL", county: "Alachua County");

        var body = (await Standards(caseId)).Object;

        Assert.Equal("FL", body.Str("state"));
        Assert.Equal("Alachua County", body.Str("county"));
        Assert.Equal(1, body["householdSize"]!.GetValue<int>());
        Assert.Empty(Problems(body));
        Assert.All(NationalFigures, key => Assert.False(string.IsNullOrEmpty(body["nationalStandards"]![key]?.GetValue<string>()), key));
        Assert.All(LocalFigures, key => Assert.False(string.IsNullOrEmpty(body["localStandards"]![key]?.GetValue<string>()), key));
        // The float-then-pin rule: the case's creation date stands in for the filing date.
        Assert.Equal((await _world.Get($"/v1/cases/{caseId}")).Str("createdAt")![..10], body.Str("asOf"));
    }

    [Fact]
    public async Task Every_money_figure_is_a_string_never_a_json_number()
    {
        var caseId = await _world.OpenCaseAsync();
        await PutDebtor1Async(caseId, state: "FL", county: "Alachua County");

        var body = (await Standards(caseId)).Object;

        Assert.Equal(JsonValueKind.String, body["nationalStandards"]!["allowance"]!.GetValueKind());
        Assert.Equal(JsonValueKind.String, body["localStandards"]!["housingNonMortgage"]!.GetValueKind());
    }

    [Fact]
    public async Task A_dependent_living_with_the_debtor_grows_the_household()
    {
        var caseId = await _world.OpenCaseAsync();
        await PutDebtor1Async(caseId, state: "FL", county: "Alachua County");
        await _world.AddAsync(caseId, "dependents", """{"household_id": "no-household-yet", "relationship": "child", "age": 9, "lives_with_debtor": true}""");

        var body = (await Standards(caseId)).Object;

        Assert.Equal(2, body["householdSize"]!.GetValue<int>());
    }

    private static List<string> Problems(JsonObject body) => [.. body["problems"]!.AsArray().Select(problem => problem!.GetValue<string>())];

    private async Task PutDebtor1Async(string caseId, string? state = null, string? county = null)
    {
        var body = new JsonObject { ["name"] = new JsonObject { ["given"] = "Ada" } };
        var provenance = new JsonObject { ["name.given"] = new JsonObject { ["source"] = "staff_typed" } };
        if (state is not null || county is not null)
        {
            var address = new JsonObject();
            if (state is not null)
            {
                address["state"] = state;
                provenance["residence_address.state"] = new JsonObject { ["source"] = "staff_typed" };
            }

            if (county is not null)
            {
                address["county"] = county;
                provenance["residence_address.county"] = new JsonObject { ["source"] = "staff_typed" };
            }

            body["residence_address"] = address;
        }

        body["provenance"] = provenance;
        var answer = await _world.Put($"/v1/cases/{caseId}/debtors/debtor_1", body.ToJsonString());
        Assert.True(answer.Status == 200, answer.Body);
    }

    private Task<Answer> Standards(string caseId, string subject = Alice) => _world.Get($"/v1/cases/{caseId}/standards", subject);
}
