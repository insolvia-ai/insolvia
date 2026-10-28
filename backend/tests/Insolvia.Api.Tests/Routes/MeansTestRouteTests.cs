using System.Text.Json;
using System.Text.Json.Nodes;
using Insolvia.Api.Tests.Support;
using Insolvia.Core.Income;
using Insolvia.Core.MeansTest;
using Insolvia.Core.Petitions;
using Insolvia.Engines.Projections;
using static Insolvia.Api.Tests.Support.CaseWorld;

namespace Insolvia.Api.Tests.Routes;

/// <summary>
/// test_means_test_route.py: <c>GET …/means-test</c> — the § 707(b) trace on
/// demand. The one claim the issue makes: the endpoint's result IS what the
/// packet's B122A-1 / B122A-2 (and, under Chapter 13, B122C-1 / B122C-2)
/// print. So the reference case is stored into the API's stores, read back
/// over HTTP, and its figures asserted equal to the projections' over the
/// same records — the engine tests own the arithmetic.
/// </summary>
public sealed class MeansTestRouteTests : IAsyncLifetime, IDisposable
{
    private readonly AnalysisWorld _world = new();

    public async ValueTask InitializeAsync() => await _world.InitializeAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task The_route_refuses_an_unauthenticated_caller()
    {
        Assert.Equal(401, (await _world.Get("/v1/cases/any-id/means-test", subject: null)).Status);
    }

    [Fact]
    public async Task An_unknown_case_is_not_found()
    {
        Assert.Equal(404, (await MeansTest("no-such-case")).Status);
    }

    [Fact]
    public async Task Another_firms_means_test_is_the_same_404_as_no_case()
    {
        var caseId = await SeedReferenceAsync();

        Assert.Equal(404, (await MeansTest(caseId, Carol)).Status);
    }

    [Fact]
    public async Task The_static_segment_is_not_shadowed_by_the_collection_routes()
    {
        var caseId = await _world.OpenCaseAsync();

        var answer = await MeansTest(caseId);

        Assert.Equal(200, answer.Status);
        Assert.True(answer.Object.ContainsKey("cmi"));
    }

    [Fact]
    public async Task A_bare_case_reports_why_rather_than_failing()
    {
        var caseId = await _world.OpenCaseAsync();

        var body = (await MeansTest(caseId)).Object;

        Assert.Equal("undetermined", body.Str("outcome"));
        Assert.Null(body["determinedBy"]);
        Assert.Null(body["comparison"]);
        Assert.Empty(body["lines"]!.AsArray());
        Assert.Contains(Problems(body), problem => problem.Contains("household composition", StringComparison.Ordinal));
        // The window and the (empty) derivation still land: the screen shows
        // the six months the records will be read over before any exist.
        Assert.Empty(body["cmi"]!["columns"]!.AsArray());
        Assert.Equal(6, body["cmi"]!["window"]!["months"]!.AsArray().Count);
        Assert.Equal("case.created_at", body.Str("asOfSource"));
        Assert.Equal("""{"source":null,"value":null}""", Http.FlaskJson.Serialize(body["household"]!["medianHouseholdSize"]));
    }

    [Fact]
    public async Task The_trace_equals_what_b122a1_prints()
    {
        var caseId = await SeedReferenceAsync();
        var values = FormProjections.Project(FormTemplates.Latest("form/b122a1"), await _world.CaseFileAsync(caseId));

        var body = (await MeansTest(caseId)).Object;

        Assert.Equal("2026-08-01", body.Str("asOf"));
        var cmi = body["cmi"]!.AsObject();
        Assert.Equal(Money(values["total_cmi"]), cmi.Str("combinedMonthlyTotal"));
        Assert.Equal(Money(values["annualized_cmi"]), cmi.Str("annualized"));
        var columns = cmi["columns"]!.AsArray().Select(column => column!.AsObject()).ToDictionary(column => column.Str("column")!, StringComparer.Ordinal);
        var wages = Line(columns["A"], "lines", "wages");
        Assert.Equal(Money(values.Rows("wages")["Debto1.Quest2.0"]), wages.Str("monthlyAverage"));
        Assert.Equal(6, wages["entries"]!.AsArray().Count);
        var unemployment = Line(columns["B"], "lines", "unemployment");
        Assert.Equal(Money(values.Rows("unemployment")["Debto2.Quest8.0"]), unemployment.Str("monthlyAverage"));
        // The § 101(10A)(B)(ii) exclusion is shown with its citation, and its
        // figure is the one line 8's contention box prints.
        var ssa = Assert.Single(columns["B"]["excluded"]!.AsArray())!.AsObject();
        Assert.Equal("social_security_act_benefit", ssa.Str("category"));
        Assert.Equal(Money(values.Rows("ssa_contention")["Debto2.Quest8A"]), ssa.Str("monthlyAverage"));
        Assert.Contains("101(10A)(B)(ii)", ssa.Str("citation"), StringComparison.Ordinal);

        var comparison = body["comparison"]!.AsObject();
        Assert.Equal(((TextFill)values["median_state"]).Value, comparison.Str("state"));
        Assert.Equal(((TextFill)values["median_household_size"]).Value, comparison["householdSize"]!.ToJsonString());
        Assert.Equal(Money(values["median_income"]), comparison.Str("annualMedian"));
        Assert.True(comparison["aboveMedian"]!.GetValue<bool>());
        Assert.Equal(new OptionFill("12b more than 13"), values["median_comparison"]);
        Assert.Contains("ust/census-median-family-income@", comparison.Str("source"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_trace_equals_what_b122a2_prints()
    {
        var caseId = await SeedReferenceAsync();
        var values = FormProjections.Project(FormTemplates.Latest("form/b122a2"), await _world.CaseFileAsync(caseId));

        var body = (await MeansTest(caseId)).Object;

        Assert.Equal("no_presumption", body.Str("outcome"));
        Assert.Equal("threshold_floor", body.Str("determinedBy"));
        Assert.Equal(new OptionFill("1"), values["presumption_thresholds"]);
        var lines = body["lines"]!.AsArray().Select(line => line!.AsObject()).ToDictionary(line => line.Str("line")!, StringComparer.Ordinal);
        foreach (var (number, fieldId) in new[]
        {
            ("1", "total_cmi"), ("4", "adjusted_cmi"), ("6", "food_clothing_allowance"), ("8", "housing_operating"),
            ("9b", "home_debt_total"), ("13b", "vehicle_1_loan_total"), ("24", "irs_allowances_total"),
            ("32", "additional_deductions_total"), ("33e", "secured_debt_total"), ("34", "cure_monthly_total"),
            ("36", "ch13_admin_expense"), ("38", "total_deductions"), ("39c", "monthly_disposable_income"),
            ("39d", "disposable_income_60_months"),
        })
        {
            Assert.True(Money(values[fieldId]) == lines[number].Str("amount"), number);
        }

        Assert.Equal("-23359.80", lines["39d"].Str("amount"));
        // Every line names where it came from.
        Assert.All(lines.Values, line => Assert.False(string.IsNullOrEmpty(line.Str("source"))));
        Assert.Contains("ust/irs-national-standards@", lines["6"].Str("source"), StringComparison.Ordinal);
        Assert.Contains("means_test_input.taxes", lines["16"].Str("source"), StringComparison.Ordinal);
        Assert.StartsWith("ust/irs-local-standards@", body["releaseIds"]!["ust/irs-local-standards"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Empty(Problems(body));
    }

    [Fact]
    public async Task A_chapter_7_trace_carries_no_chapter_13_verdict()
    {
        var caseId = await SeedReferenceAsync();

        var body = (await MeansTest(caseId)).Object;

        Assert.Equal(7, body["chapter"]!.GetValue<int>());
        Assert.Null(body["chapter13"]);
        Assert.Equal(["122A-2"], Forms(body));
    }

    // Issue #365: the same family under Chapter 13 — the commitment period,
    // whether Form 122C-2 is required, and the disposable income are the
    // figures the two projections land, read through one endpoint.
    [Fact]
    public async Task The_chapter_13_trace_equals_what_b122c1_and_b122c2_print()
    {
        var caseId = await SeedReferenceAsync(chapter: 13);
        var caseFile = await _world.CaseFileAsync(caseId);
        var c1 = FormProjections.Project(FormTemplates.Latest("form/b122c1"), caseFile);
        var c2 = FormProjections.Project(FormTemplates.Latest("form/b122c2"), caseFile);

        var body = (await MeansTest(caseId)).Object;

        Assert.Equal(13, body["chapter"]!.GetValue<int>());
        Assert.Equal("above_median", body.Str("outcome"));
        Assert.Equal("median", body.Str("determinedBy"));
        Assert.Null(body["exemptions"]!["applied"]);
        var comparison = body["comparison"]!.AsObject();
        Assert.Equal(Money(c1["current_monthly_income"]), comparison.Str("monthlyCmi"));
        Assert.Equal(Money(c1["annualized_cmi"]), comparison.Str("annualizedCmi"));
        Assert.True(comparison["aboveMedian"]!.GetValue<bool>());
        Assert.Equal(new OptionFill("17b"), c1["median_comparison"]);
        var verdict = body["chapter13"]!.AsObject();
        Assert.Equal(60, verdict["commitmentPeriodMonths"]!.GetValue<int>());
        Assert.Equal(new OptionFill("4"), c1["caption.commitment_period_box"]);
        Assert.Equal(Money(c1["commitment_annualized"]), verdict.Str("commitmentAnnualizedIncome"));
        Assert.Equal(Money(c1["median_income"]), verdict.Str("commitmentAnnualMedian"));
        Assert.Contains("§ 1325(b)(4)", verdict.Str("commitmentSource"), StringComparison.Ordinal);
        Assert.True(verdict["disposableIncomeRequired"]!.GetValue<bool>());
        Assert.Equal(Money(c2["monthly_disposable_income"]), verdict.Str("monthlyDisposableIncome"));
        var lines = body["lines"]!.AsArray().Select(line => line!.AsObject()).ToDictionary(line => (line.Str("form"), line.Str("line")));
        Assert.Equal(Money(c1["current_monthly_income"]), lines[("122C-1", "14")].Str("amount"));
        Assert.Equal(Money(c2["total_deductions"]), lines[("122C-2", "38")].Str("amount"));
        Assert.Equal(Money(c2["monthly_disposable_income"]), lines[("122C-2", "45")].Str("amount"));
        Assert.All(lines.Values, line => Assert.False(string.IsNullOrEmpty(line.Str("source"))));
        Assert.Empty(Problems(body));
    }

    [Fact]
    public async Task A_below_median_chapter_13_case_reports_three_years_and_no_122c2()
    {
        var caseId = await SeedReferenceAsync(chapter: 13);
        foreach (var entity in await _world.Entities.ListForCaseAsync(caseId, IncomeRules.PayPeriodRecord, HttpAssert.Ct))
        {
            var answer = await _world.Put($"/v1/cases/{caseId}/pay_period_records/{entity.Id}", AnalysisWorld.Typed("""{"gross": "4500.00"}"""));
            Assert.True(answer.Status == 200, answer.Body);
        }

        var body = (await MeansTest(caseId)).Object;

        Assert.Equal("below_median", body.Str("outcome"));
        Assert.Equal(36, body["chapter13"]!["commitmentPeriodMonths"]!.GetValue<int>());
        Assert.False(body["chapter13"]!["disposableIncomeRequired"]!.GetValue<bool>());
        Assert.Null(body["chapter13"]!["monthlyDisposableIncome"]);
        Assert.Equal(["122C-1"], Forms(body));
    }

    [Fact]
    public async Task The_inputs_are_reported_as_the_engine_read_them()
    {
        var caseId = await SeedReferenceAsync();

        var body = (await MeansTest(caseId)).Object;

        Assert.Equal("""{"source":"the case's debtor_2 record","value":"married_filing_jointly"}""", Http.FlaskJson.Serialize(body["maritalFilingStatus"]));
        var household = body["household"]!.AsObject();
        Assert.Equal(3, household["peopleUnder65"]!.GetValue<int>());
        Assert.Equal(3, household["medianHouseholdSize"]!["value"]!.GetValue<int>());
        Assert.Equal(3, household["irsFamilySize"]!["value"]!.GetValue<int>());
        Assert.Equal(3, household["irsHousingFamilySize"]!["value"]!.GetValue<int>());
        Assert.Equal(1, household["childrenUnder18"]!.GetValue<int>());
        Assert.Null(body["exemptions"]!["applied"]);
        Assert.Equal("""["non_consumer_debts","disabled_veteran","reservist_national_guard"]""", Http.FlaskJson.Serialize(body["exemptions"]!["available"]));
        Assert.NotEqual("0.00", body["debt"]!["priorityTotal"]!.GetValue<string>());
    }

    [Fact]
    public async Task Saving_an_exemption_changes_the_verdict()
    {
        var caseId = await SeedReferenceAsync();
        var existing = Assert.Single(await _world.Entities.ListForCaseAsync(caseId, MeansTestInputRules.Kind, HttpAssert.Ct));
        var saved = await _world.Put($"/v1/cases/{caseId}/means_test_inputs/{existing.Id}", AnalysisWorld.Typed("""{"reservist_national_guard": true, "people_under_65": 3}"""));
        Assert.True(saved.Status == 200, saved.Body);

        var body = (await MeansTest(caseId)).Object;

        Assert.Equal("exempt", body.Str("outcome"));
        Assert.Equal("reservist_national_guard", body.Str("determinedBy"));
        Assert.Equal("reservist_national_guard", body["exemptions"]!["applied"]!.GetValue<string>());
        Assert.Contains("707(b)(2)(D)(ii)", body["exemptions"]!["rule"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Empty(body["lines"]!.AsArray());
    }

    [Fact]
    public async Task The_window_follows_the_petitions_expected_filing_date()
    {
        var caseId = await SeedReferenceAsync();
        await PutExpectedFilingDateAsync(caseId, "2026-10-15");

        var body = (await MeansTest(caseId)).Object;

        Assert.Equal("2026-10-15", body.Str("asOf"));
        Assert.Equal("petition.expected_filing_date", body.Str("asOfSource"));
        Assert.Equal("""["2026-04","2026-05","2026-06","2026-07","2026-08","2026-09"]""", Http.FlaskJson.Serialize(body["cmi"]!["window"]!["months"]));
        // Ada's February and March checks fall out of the window; the figure
        // moves with the planned date, as the form's would.
        Assert.NotEqual("8700.00", body["cmi"]!["combinedMonthlyTotal"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_planned_date_before_the_datasets_begin_is_a_problem_not_a_500()
    {
        var caseId = await SeedReferenceAsync();
        await PutExpectedFilingDateAsync(caseId, "2026-04-15");

        var answer = await MeansTest(caseId);

        Assert.Equal(200, answer.Status);
        var body = answer.Object;
        Assert.Equal("undetermined", body.Str("outcome"));
        Assert.Equal("{}", Http.FlaskJson.Serialize(body["releaseIds"]));
        Assert.Contains(Problems(body), problem => problem.Contains("no release of", StringComparison.Ordinal));
        // The window is a calendar fact and still lands.
        Assert.Equal("2025-10", body["cmi"]!["window"]!["months"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task Money_is_a_string_never_a_json_number()
    {
        var caseId = await SeedReferenceAsync();

        var body = (await MeansTest(caseId)).Object;

        Assert.Equal(JsonValueKind.String, body["cmi"]!["combinedMonthlyTotal"]!.GetValueKind());
        Assert.Equal(JsonValueKind.String, body["comparison"]!["annualMedian"]!.GetValueKind());
        Assert.All(body["lines"]!.AsArray(), line => Assert.Equal(JsonValueKind.String, line!["amount"]!.GetValueKind()));
    }

    // The screen writes the whole record through the generic collection
    // route; the new members must survive that trip unchanged.
    [Fact]
    public async Task The_sample_input_body_round_trips_through_the_collection_route()
    {
        var caseId = await _world.OpenCaseAsync();
        const string IncomeOverrides = """[{"category":"wages","column":"A","id":"ov1","monthly_amount":"5000.00"}]""";
        const string SecuredPayments = """[{"bucket":"vehicle_1","claim_id":"claim-1","cure_total":"1200.00","id":"sp1","monthly_payment":"400.00"}]""";
        var typed = """{"source": "staff_typed"}""";
        var payload = $$$"""
            {"median_household_size": 4, "marital_filing_status": "married_not_filing_separated",
             "income_overrides": {{{IncomeOverrides}}}, "other_secured_payments": {{{SecuredPayments}}},
             "provenance": {"median_household_size": {{{typed}}}, "marital_filing_status": {{{typed}}},
               "income_overrides[ov1].id": {{{typed}}}, "income_overrides[ov1].column": {{{typed}}}, "income_overrides[ov1].category": {{{typed}}}, "income_overrides[ov1].monthly_amount": {{{typed}}},
               "other_secured_payments[sp1].id": {{{typed}}}, "other_secured_payments[sp1].claim_id": {{{typed}}}, "other_secured_payments[sp1].bucket": {{{typed}}},
               "other_secured_payments[sp1].monthly_payment": {{{typed}}}, "other_secured_payments[sp1].cure_total": {{{typed}}}}}
            """;

        var answer = await _world.Post($"/v1/cases/{caseId}/means_test_inputs", payload);

        Assert.True(answer.Status == 201, answer.Body);
        var stored = answer.Object;
        Assert.Equal(IncomeOverrides, Http.FlaskJson.Serialize(stored["income_overrides"]));
        Assert.Equal(SecuredPayments, Http.FlaskJson.Serialize(stored["other_secured_payments"]));
        var bare = JsonNode.Parse(payload)!.AsObject();
        bare.Remove("provenance");
        Assert.Equal(4, MeansTestInputRules.Kind.ParseBody(bare).MedianHouseholdSize);
    }

    /// <summary>A projected money box back to the wire's plain two-place string.</summary>
    private static string Money(FieldEntry printed) => ((TextFill)printed).Value.Replace(",", string.Empty, StringComparison.Ordinal);

    private static JsonObject Line(JsonObject column, string member, string category) =>
        column[member]!.AsArray().Select(line => line!.AsObject()).First(line => line.Str("category") == category);

    private static List<string> Problems(JsonObject body) => [.. body["problems"]!.AsArray().Select(problem => problem!.GetValue<string>())];

    private static List<string> Forms(JsonObject body) => [.. body["lines"]!.AsArray().Select(line => line!["form"]!.GetValue<string>()).Distinct()];

    private async Task PutExpectedFilingDateAsync(string caseId, string expected)
    {
        var petition = Assert.Single(await _world.Entities.ListForCaseAsync(caseId, PetitionRules.Petition, HttpAssert.Ct));
        var answer = await _world.Put($"/v1/cases/{caseId}/petitions/{petition.Id}", AnalysisWorld.Typed($$"""{"expected_filing_date": "{{expected}}"}"""));
        Assert.True(answer.Status == 200, answer.Body);
    }

    /// <summary>test_means_test_route.py's <c>seed_reference_case</c>.</summary>
    private Task<string> SeedReferenceAsync(int chapter = 7) =>
        _world.SeedAsync("reference-ch7", chapter: chapter, collections: AnalysisWorld.MeansTestCollections);

    private Task<Answer> MeansTest(string caseId, string subject = Alice) => _world.Get($"/v1/cases/{caseId}/means-test", subject);
}
