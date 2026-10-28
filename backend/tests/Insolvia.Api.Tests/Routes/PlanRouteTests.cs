using System.Globalization;
using System.Text.Json.Nodes;
using Insolvia.Api.Tests.Support;
using Insolvia.Core.CaseEntities;
using Insolvia.Core.Plans;
using static Insolvia.Api.Tests.Support.CaseWorld;

namespace Insolvia.Api.Tests.Routes;

/// <summary>
/// test_plan_route.py: <c>/v1/cases/{id}/plan-calculation</c> over HTTP — auth
/// and reachability, the literal segment not shadowed, the stored
/// <c>plans</c> record is what GET calculates, the commitment period is the
/// means-test route's own figure, scenarios are parsed by the record's parser
/// and write nothing — and the issue's "done when" on the reference case
/// filed under Chapter 13. The arithmetic is the Engines' Chapter13PlanTests'.
/// </summary>
public sealed class PlanRouteTests : IAsyncLifetime, IDisposable
{
    // $500 a month over the commitment period, the mortgage kept current
    // outside the plan, the car paid in full at 7%.
    private const string ReferencePlan = """
        {"payment_source": "fixed", "monthly_payment": "500.00", "trustee_percentage": "10", "attorney_fees": "3500.00",
         "secured_treatments": [
           {"id": "t-home", "claim_id": "claim-mortgage", "treatment": "cure_and_maintain"},
           {"id": "t-car", "claim_id": "claim-auto", "treatment": "cramdown", "interest_rate": "7"}],
         "unsecured_treatment": "pot"}
        """;

    private const string Provenance = """
        {"payment_source": {"source": "staff_typed"}, "monthly_payment": {"source": "staff_typed"}, "trustee_percentage": {"source": "staff_typed"},
         "attorney_fees": {"source": "staff_typed"},
         "secured_treatments[t-home].claim_id": {"source": "staff_typed"}, "secured_treatments[t-home].treatment": {"source": "staff_typed"},
         "secured_treatments[t-car].claim_id": {"source": "staff_typed"}, "secured_treatments[t-car].treatment": {"source": "staff_typed"},
         "secured_treatments[t-car].interest_rate": {"source": "staff_typed"}, "unsecured_treatment": {"source": "staff_typed"}}
        """;

    private readonly AnalysisWorld _world = new();

    public static TheoryData<string> MalformedLists =>
    [
        "{}",
        """{"scenarios": []}""",
        """{"scenarios": "one"}""",
        """{"scenarios": [{}, {}, {}, {}, {}, {}]}""",
    ];

    public async ValueTask InitializeAsync() => await _world.InitializeAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _world.Dispose();

    // ── auth and reachability ────────────────────────────────────────

    [Fact]
    public async Task The_route_refuses_an_unauthenticated_caller()
    {
        Assert.Equal(401, (await _world.Get("/v1/cases/any-id/plan-calculation", subject: null)).Status);
        Assert.Equal(401, (await _world.Post("/v1/cases/any-id/plan-calculation", "{}", subject: null)).Status);
    }

    [Fact]
    public async Task Another_firms_case_is_the_same_404_as_no_case()
    {
        var caseId = await SeedChapter13Async();

        Assert.Equal(404, (await Calculation(caseId, Carol)).Status);
        Assert.Equal(404, (await Calculation("no-such-case")).Status);
    }

    [Fact]
    public async Task A_scenario_post_on_an_unreachable_case_is_404_not_400()
    {
        var caseId = await SeedChapter13Async();

        Assert.Equal(404, (await Scenarios(caseId, """{"nonsense": true}""", Carol)).Status);
    }

    [Fact]
    public async Task The_static_segment_is_not_shadowed_by_the_collection_routes()
    {
        var caseId = await _world.OpenCaseAsync();

        var answer = await Calculation(caseId);

        Assert.Equal(200, answer.Status);
        Assert.True(answer.Object.ContainsKey("liquidation"));
    }

    // ── the stored plan ──────────────────────────────────────────────

    [Fact]
    public async Task The_plan_record_is_written_through_the_generic_collection()
    {
        var caseId = await SeedChapter13Async();

        var answer = await _world.Post($"/v1/cases/{caseId}/plans", WithProvenance(ReferencePlan));

        Assert.Equal(201, answer.Status);
        Assert.True((await Calculation(caseId)).Object["planPresent"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_plan_value_without_provenance_is_refused()
    {
        var caseId = await SeedChapter13Async();

        Assert.Equal(400, (await _world.Post($"/v1/cases/{caseId}/plans", ReferencePlan)).Status);
    }

    [Fact]
    public async Task Without_a_plan_the_liquidation_floor_is_still_reported()
    {
        var caseId = await SeedChapter13Async();

        var body = (await Calculation(caseId)).Object;

        Assert.False(body["planPresent"]!.GetValue<bool>());
        Assert.Equal("39.33", body["liquidation"]!["percentage"]!.GetValue<string>());
        Assert.Null(body["feasibility"]!["feasible"]);
        Assert.Contains("There is no plan yet: choose a monthly payment.", Problems(body));
    }

    [Fact]
    public async Task The_commitment_period_is_the_means_tests_own()
    {
        var caseId = await SeedChapter13Async();
        var meansTest = (await _world.Get($"/v1/cases/{caseId}/means-test")).Object;

        var body = (await Calculation(caseId)).Object;

        var expected = new JsonObject
        {
            ["months"] = meansTest["chapter13"]!["commitmentPeriodMonths"]!.DeepClone(),
            ["source"] = meansTest["chapter13"]!["commitmentSource"]!.DeepClone(),
        };
        Assert.Equal(Http.FlaskJson.Serialize(expected), Http.FlaskJson.Serialize(body["commitmentPeriod"]));
    }

    /// <summary>The issue's "done when", against the reference case.</summary>
    [Fact]
    public async Task The_reference_case_has_a_feasible_plan_that_clears_the_floor()
    {
        var caseId = await SeedChapter13Async();
        await _world.StoreAsync(PlanRules.Kind, caseId, WithProvenance(ReferencePlan));

        var body = (await Calculation(caseId)).Object;

        Assert.Empty(Problems(body));
        Assert.Equal(60, body["funding"]!["termMonths"]!.GetValue<int>());
        Assert.True(body["feasibility"]!["feasible"]!.GetValue<bool>());
        Assert.True(body["bestInterests"]!["passes"]!.GetValue<bool>());
        Assert.True(Percent(body["unsecured"]!["percentage"]) > Percent(body["liquidation"]!["percentage"]));
        // Every payee row names where its figure came from.
        Assert.All(body["classes"]!.AsArray(), summary => Assert.All(summary!["rows"]!.AsArray(), row => Assert.False(string.IsNullOrEmpty(row!["source"]?.GetValue<string>()))));
    }

    /// <summary>Issue #370's flag is accepted on every collection; on a plan nothing reads it, so the figures are the same.</summary>
    [Fact]
    public async Task The_generic_amended_flag_changes_nothing_on_a_plan()
    {
        var caseId = await SeedChapter13Async();
        await _world.StoreAsync(PlanRules.Kind, caseId, WithProvenance(ReferencePlan));
        var plain = (await Calculation(caseId)).Body;
        var stored = (CaseEntity<PlanBody>)Assert.Single(await _world.Entities.ListForCaseAsync(caseId, PlanRules.Kind, HttpAssert.Ct));
        Assert.True(await _world.Entities.PutAsync(stored with { Amended = true }, HttpAssert.Ct));

        var flagged = (await Calculation(caseId)).Body;

        Assert.True(Assert.Single(await _world.Entities.ListForCaseAsync(caseId, PlanRules.Kind, HttpAssert.Ct)).Amended);
        Assert.Equal(plain, flagged);
    }

    [Fact]
    public async Task Two_plan_records_are_read_first_one_first_and_said_so()
    {
        var caseId = await SeedChapter13Async();
        foreach (var payment in new[] { "500.00", "900.00" })
        {
            var plan = JsonNode.Parse(ReferencePlan)!.AsObject();
            plan["monthly_payment"] = payment;
            await _world.StoreAsync(PlanRules.Kind, caseId, WithProvenance(plan.ToJsonString()));
        }

        var body = (await Calculation(caseId)).Object;

        Assert.StartsWith("This case has 2 plan records", body["warnings"]![0]!.GetValue<string>(), StringComparison.Ordinal);
    }

    // ── scenarios ────────────────────────────────────────────────────

    [Fact]
    public async Task Scenarios_are_calculated_side_by_side_and_write_nothing()
    {
        var caseId = await SeedChapter13Async();
        var cheaper = JsonNode.Parse(ReferencePlan)!.AsObject();
        cheaper["monthly_payment"] = "300";

        var answer = await Scenarios(caseId, $$"""{"scenarios": [{"label": "$500 a month", "plan": {{ReferencePlan}}}, {"label": "$300", "plan": {{cheaper.ToJsonString()}}}]}""");

        Assert.Equal(200, answer.Status);
        var scenarios = answer.Object["scenarios"]!.AsArray();
        Assert.Equal(2, scenarios.Count);
        Assert.Equal("$500 a month", scenarios[0]!["label"]!.GetValue<string>());
        Assert.True(scenarios[0]!["calculation"]!["feasibility"]!["feasible"]!.GetValue<bool>());
        Assert.False(scenarios[1]!["calculation"]!["bestInterests"]!["passes"]!.GetValue<bool>());
        Assert.Equal("""{"plans":[]}""", (await _world.Get($"/v1/cases/{caseId}/plans")).Body);
    }

    [Fact]
    public async Task An_unlabelled_scenario_answers_a_null_label()
    {
        var caseId = await SeedChapter13Async();

        var answer = await Scenarios(caseId, $$"""{"scenarios": [{"plan": {{ReferencePlan}}}]}""");

        Assert.Equal(200, answer.Status);
        var scenario = Assert.Single(answer.Object["scenarios"]!.AsArray())!.AsObject();
        Assert.True(scenario.ContainsKey("label"));
        Assert.Null(scenario["label"]);
    }

    [Fact]
    public async Task A_malformed_scenario_names_its_path()
    {
        var caseId = await SeedChapter13Async();

        var answer = await Scenarios(caseId, """{"scenarios": [{"plan": {"term_months": 84}}]}""");

        Assert.Equal(400, answer.Status);
        Assert.Contains("scenarios[0].plan.term_months", answer.FieldErrors);
    }

    [Fact]
    public async Task A_scenario_that_is_not_an_object_or_has_no_plan_object_is_named()
    {
        var caseId = await SeedChapter13Async();

        var answer = await Scenarios(caseId, """{"scenarios": ["one", {"label": "x"}]}""");

        Assert.Equal(400, answer.Status);
        Assert.Equal(["scenarios[0]", "scenarios[1].plan"], answer.FieldErrors.Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(MalformedLists))]
    public async Task The_scenario_list_is_required_and_bounded(string body)
    {
        var caseId = await SeedChapter13Async();

        var answer = await Scenarios(caseId, body);

        Assert.Equal(400, answer.Status);
        Assert.Equal(["scenarios"], answer.FieldErrors);
    }

    [Fact]
    public async Task A_body_that_is_not_a_json_object_is_refused()
    {
        var caseId = await SeedChapter13Async();

        var answer = await Scenarios(caseId, "[1]");

        Assert.Equal(400, answer.Status);
        Assert.Equal("""{"error":"ValidationError","message":"request body must be a JSON object"}""", answer.Body);
    }

    private static string WithProvenance(string plan)
    {
        var body = JsonNode.Parse(plan)!.AsObject();
        body["provenance"] = JsonNode.Parse(Provenance);
        return body.ToJsonString();
    }

    private static decimal Percent(JsonNode? node) => decimal.Parse(node!.GetValue<string>(), CultureInfo.InvariantCulture);

    private static List<string> Problems(JsonObject body) => [.. body["problems"]!.AsArray().Select(problem => problem!.GetValue<string>())];

    /// <summary>test_plan_route.py's <c>seed_chapter_13</c>: the reference case under Chapter 13, with Schedules C, I and J and the SOFA.</summary>
    private Task<string> SeedChapter13Async() =>
        _world.SeedAsync("reference-ch7", chapter: 13, collections: AnalysisWorld.PlanCollections);

    private Task<Answer> Calculation(string caseId, string subject = Alice) => _world.Get($"/v1/cases/{caseId}/plan-calculation", subject);

    private Task<Answer> Scenarios(string caseId, string json, string subject = Alice) => _world.Post($"/v1/cases/{caseId}/plan-calculation", json, subject);
}
