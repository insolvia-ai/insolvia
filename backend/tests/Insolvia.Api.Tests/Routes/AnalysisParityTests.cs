using System.Text.Json.Nodes;
using Insolvia.Api.Http;
using Insolvia.Api.Tests.Support;

namespace Insolvia.Api.Tests.Routes;

/// <summary>
/// The analysis routes end to end against the Python oracle: each of the
/// four Engines parity case files is stored into the API's stores, read back
/// through the whole HTTP pipeline (auth, the permission filters, the store
/// reads, the engine, FlaskJson), and the response body must be exactly the
/// bytes Flask's <c>jsonify</c> writes for the output the Python oracle
/// recorded (Parity/*.expected.json) — or, where the Python route wraps the engine's result, for that
/// wrapping of it.
/// </summary>
public sealed class AnalysisParityTests : IAsyncLifetime, IDisposable
{
    private readonly AnalysisWorld _world = new(pinned: true);

    // Every collection a case file carries, for a seed that leaves one out.
    private static readonly string[] AllCollections =
        ["petitions", "prior_cases", "related_cases", "sole_proprietorships", "filing_professionals", "employments", "income_summaries", "pay_period_records", "other_income_records", "means_test_inputs", "assets", "exemptions", "creditors", "claims", "contract_leases", "codebtors", "community_household_members", "households", "expenses", "dependents", "sofa_entries", "plans"];

    public static TheoryData<string> CaseFiles => [.. AnalysisWorld.ParityCaseFiles];

    /// <summary>The two case files that carry a stored plan.</summary>
    public static TheoryData<string> PlanCaseFiles => ["reference-ch13", "chapter-13-sample"];

    public async ValueTask InitializeAsync() => await _world.InitializeAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _world.Dispose();

    [Theory]
    [MemberData(nameof(CaseFiles))]
    public Task The_means_test_is_the_oracles_trace(string caseFile) => AssertRouteAsync(caseFile, "means-test", Value(caseFile, "means_test"));

    [Theory]
    [MemberData(nameof(CaseFiles))]
    public Task The_liens_are_the_oracles(string caseFile) => AssertRouteAsync(caseFile, "liens", Value(caseFile, "liens"));

    // The oracle ran the analysis over `exemption_case_of` (ids
    // `exemption-<n>`) on 2026-10-01; the fixture stores the exemption
    // records under those ids and pins the server's today to that date.
    [Theory]
    [MemberData(nameof(CaseFiles))]
    public Task The_exemption_analysis_is_the_oracles(string caseFile) => AssertRouteAsync(caseFile, "exemption-analysis", Value(caseFile, "exemptions"));

    [Theory]
    [MemberData(nameof(CaseFiles))]
    public Task The_creditor_matrix_is_the_oracles(string caseFile) => AssertRouteAsync(caseFile, "creditor-matrix", Value(caseFile, "creditor_matrix"));

    // Every parity case file has at most one plan, so the route adds no
    // cardinality warning to the oracle's calculation.
    [Theory]
    [MemberData(nameof(CaseFiles))]
    public Task The_stored_plans_calculation_is_the_oracles(string caseFile) => AssertRouteAsync(caseFile, "plan-calculation", Value(caseFile, "plan"));

    // The oracle recorded the dataclass; the route's wire shape is
    // standards.py's `_standards_json` of it. The route resolves on the
    // case's creation date and the oracle on TODAY, so the case is created then.
    [Theory]
    [MemberData(nameof(CaseFiles))]
    public async Task The_standards_are_the_oracles_in_the_routes_wrapping(string caseFile)
    {
        var caseId = await _world.SeedAsync(caseFile, createdAt: AnalysisWorld.ParityToday);
        var oracle = Value(caseFile, "standards").AsObject();
        var wrapped = new JsonObject
        {
            ["asOf"] = oracle["as_of"]?.DeepClone(),
            ["state"] = oracle["state"]?.DeepClone(),
            ["county"] = oracle["county"]?.DeepClone(),
            ["householdSize"] = oracle["household_size"]?.DeepClone(),
            ["jurisdictionSource"] = oracle["jurisdiction_source"]?.DeepClone(),
            ["nationalStandards"] = new JsonObject
            {
                ["releaseId"] = oracle["national_release_id"]?.DeepClone(),
                ["allowance"] = oracle["national_allowance"]?.DeepClone(),
                ["oopHealthcareUnder65"] = oracle["oop_healthcare_under_65"]?.DeepClone(),
                ["oopHealthcare65AndOlder"] = oracle["oop_healthcare_65_and_older"]?.DeepClone(),
            },
            ["localStandards"] = new JsonObject
            {
                ["releaseId"] = oracle["local_release_id"]?.DeepClone(),
                ["housingNonMortgage"] = oracle["housing_non_mortgage"]?.DeepClone(),
                ["housingMortgageRent"] = oracle["housing_mortgage_rent"]?.DeepClone(),
                ["transportationPublicNational"] = oracle["transportation_public_national"]?.DeepClone(),
                ["transportationOwnershipOneCar"] = oracle["transportation_ownership_one_car"]?.DeepClone(),
                ["transportationOwnershipTwoCars"] = oracle["transportation_ownership_two_cars"]?.DeepClone(),
                ["transportationOperatingOneCar"] = oracle["transportation_operating_one_car"]?.DeepClone(),
                ["transportationOperatingTwoCars"] = oracle["transportation_operating_two_cars"]?.DeepClone(),
            },
            ["problems"] = oracle["problems"]?.DeepClone(),
        };
        // Every member of the dataclass is carried — none dropped by the mapping.
        Assert.Equal(18, oracle.Count);

        var answer = await _world.Get($"/v1/cases/{caseId}/standards");

        AssertFlaskBody(wrapped, answer);
    }

    // A scenario is the stored plan's body sent unsaved: the same calculation,
    // under its label, and nothing written.
    [Theory]
    [MemberData(nameof(PlanCaseFiles))]
    public async Task A_scenario_of_the_stored_plan_is_the_oracles_calculation(string caseFile)
    {
        var plans = AnalysisWorld.CaseFileJson(caseFile)["plans"]!.AsArray();
        Assert.Single(plans);
        var caseId = await _world.SeedAsync(caseFile, collections: [.. AllCollections.Where(name => name != "plans")]);
        var body = new JsonObject { ["scenarios"] = new JsonArray(new JsonObject { ["label"] = "stored", ["plan"] = plans[0]!.DeepClone() }) };

        var answer = await _world.Post($"/v1/cases/{caseId}/plan-calculation", body.ToJsonString());

        var expected = new JsonObject { ["scenarios"] = new JsonArray(new JsonObject { ["label"] = "stored", ["calculation"] = Value(caseFile, "plan").DeepClone() }) };
        AssertFlaskBody(expected, answer);
    }

    private static JsonNode Value(string caseFile, string output)
    {
        var outcome = AnalysisWorld.Expected(caseFile)[output]!.AsObject();
        Assert.True(outcome.ContainsKey("value"), $"{caseFile}/{output}: the oracle recorded {outcome.ToJsonString()}");
        return outcome["value"]!;
    }

    private static void AssertFlaskBody(JsonNode expected, Answer answer)
    {
        Assert.Equal(200, answer.Status);
        Assert.Equal("application/json", answer.ContentType);
        Assert.Equal(FlaskJson.Serialize(expected), answer.Body);
    }

    private async Task AssertRouteAsync(string caseFile, string route, JsonNode expected)
    {
        var caseId = await _world.SeedAsync(caseFile);

        var answer = await _world.Get($"/v1/cases/{caseId}/{route}");

        AssertFlaskBody(expected, answer);
    }
}
