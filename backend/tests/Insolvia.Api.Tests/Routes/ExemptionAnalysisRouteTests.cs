using System.Text.Json;
using System.Text.Json.Nodes;
using Insolvia.Api.Tests.Support;
using static Insolvia.Api.Tests.Support.CaseWorld;

namespace Insolvia.Api.Tests.Routes;

/// <summary>
/// test_exemption_analysis_route.py: <c>GET …/exemption-analysis</c> — the
/// debtor's state, the petition's date, the assets, the liens and the claims
/// all reach the analysis; authorised like the intake reads; and the literal
/// segment beside (not instead of) the <c>exemptions</c> collection. The
/// arithmetic is the Engines' ExemptionAnalysisTests'.
/// </summary>
public sealed class ExemptionAnalysisRouteTests : IAsyncLifetime, IDisposable
{
    private readonly AnalysisWorld _world = new();

    public async ValueTask InitializeAsync() => await _world.InitializeAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task The_route_refuses_an_unauthenticated_caller()
    {
        Assert.Equal(401, (await _world.Get("/v1/cases/any-id/exemption-analysis", subject: null)).Status);
    }

    [Fact]
    public async Task An_unknown_case_is_not_found()
    {
        Assert.Equal(404, (await Analysis("no-such-case")).Status);
    }

    [Fact]
    public async Task Another_firms_analysis_is_the_same_404_as_no_case()
    {
        var caseId = await _world.OpenCaseAsync();

        Assert.Equal(404, (await Analysis(caseId, Carol)).Status);
    }

    [Fact]
    public async Task The_static_segment_is_not_shadowed_by_the_collection_routes()
    {
        var caseId = await _world.OpenCaseAsync();

        var answer = await Analysis(caseId);

        Assert.Equal(200, answer.Status);
        var body = answer.Object;
        Assert.Null(body["election"]!["state"]);
        Assert.Empty(body["entries"]!.AsArray());
        Assert.Contains(Problems(body), problem => problem.Contains("no residence state", StringComparison.Ordinal));
    }

    // `/exemptions` stays the collection route; the analysis is a sibling.
    [Fact]
    public async Task The_exemption_records_own_listing_is_untouched()
    {
        var caseId = await _world.OpenCaseAsync();

        var answer = await _world.Get($"/v1/cases/{caseId}/exemptions");

        Assert.Equal(200, answer.Status);
        Assert.Equal("""{"exemptions":[]}""", answer.Body);
    }

    [Fact]
    public async Task The_debtors_state_the_liens_and_the_claims_reach_the_arithmetic()
    {
        var caseId = await _world.OpenCaseAsync();
        await PutDebtor1Async(caseId, "FL");
        var house = await _world.AddAsync(caseId, "assets", """{"category": "real_property", "description": "12 Byron Court", "value_entire": "300000.00"}""");
        await _world.AddAsync(caseId, "claims", $$"""{"claim_class": "secured", "amount": "250000.00", "asset_id": "{{house}}", "lien_position": 1}""");
        var exemption = await _world.AddAsync(caseId, "exemptions", $$"""{"asset_id": "{{house}}", "statute_citation": "Fla. Const. art. X, § 4(a)(2)", "amount": "1000.00", "claims_full_fmv": false}""");

        var body = (await Analysis(caseId)).Object;

        Assert.True(body["election"]!["optedOut"]!.GetValue<bool>());
        Assert.Equal("state_and_federal_nonbankruptcy", body["election"]!["effective"]!.GetValue<string>());
        var row = Assert.Single(body["assets"]!.AsArray())!.AsObject();
        Assert.Equal(house, row.Str("assetId"));
        Assert.Equal("250000.00", row.Str("liens"));
        Assert.Equal("50000.00", row.Str("netEquity"));
        Assert.Equal("1000.00", row.Str("claimed"));
        Assert.Equal("49000.00", row.Str("unexempt"));
        Assert.Equal(exemption, row["claims"]![0]!["exemptionId"]!.GetValue<string>());
        var personal = body["entries"]!.AsArray().Select(entry => entry!.AsObject()).Single(entry => entry.Str("entryId") == "fl-personal-property");
        Assert.Equal("0.00", personal.Str("available"));
    }

    [Fact]
    public async Task The_petitions_expected_filing_date_is_the_resolution_date()
    {
        var caseId = await _world.OpenCaseAsync();
        await PutDebtor1Async(caseId, "GA");
        await _world.AddAsync(caseId, "petitions", """{"expected_filing_date": "2026-06-01"}""");

        var body = (await Analysis(caseId)).Object;

        Assert.Equal("2026-06-01", body.Str("asOf"));
        Assert.Equal("expected_filing_date", body.Str("asOfSource"));
        Assert.Equal("2023-02-02", body["lookbacks"]!["section522p"]!.GetValue<string>());
        // Georgia's series begins 2026-07-01: the registry refuses, and says so.
        Assert.Contains(Problems(body), problem => problem.Contains("no release of exemptions/ga", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_stored_election_changes_the_table()
    {
        var caseId = await _world.OpenCaseAsync();
        await PutDebtor1Async(caseId, "TX");

        var before = (await Analysis(caseId)).Object;
        Assert.Null(before["election"]!["effective"]);
        Assert.Empty(before["entries"]!.AsArray());

        var patched = await _world.Patch($"/v1/cases/{caseId}", """{"exemption_set": "federal"}""");
        Assert.True(patched.Status == 200, patched.Body);

        var after = (await Analysis(caseId)).Object;
        Assert.Equal("federal", after["election"]!["stored"]!.GetValue<string>());
        Assert.Equal("federal", after["election"]!["effective"]!.GetValue<string>());
        Assert.Contains(after["entries"]!.AsArray(), entry => entry!.AsObject().Str("entryId") == "us-homestead");
    }

    [Fact]
    public async Task Money_is_a_string_never_a_json_number()
    {
        var caseId = await _world.OpenCaseAsync();
        await PutDebtor1Async(caseId, "FL");
        await _world.AddAsync(caseId, "assets", """{"category": "vehicle", "value_entire": "8412.66"}""");

        var row = Assert.Single((await Analysis(caseId)).Object["assets"]!.AsArray())!.AsObject();

        Assert.Equal("8412.66", row.Str("currentValue"));
        Assert.Equal(JsonValueKind.String, row["liens"]!.GetValueKind());
    }

    private static List<string> Problems(JsonObject body) => [.. body["problems"]!.AsArray().Select(problem => problem!.GetValue<string>())];

    private async Task PutDebtor1Async(string caseId, string state)
    {
        var answer = await _world.Put(
            $"/v1/cases/{caseId}/debtors/debtor_1",
            $$"""{"name": {"given": "Ada"}, "residence_address": {"state": "{{state}}"}, "provenance": {"name.given": {"source": "staff_typed"}, "residence_address.state": {"source": "staff_typed"} } }""");
        Assert.True(answer.Status == 200, answer.Body);
    }

    private Task<Answer> Analysis(string caseId, string subject = Alice) => _world.Get($"/v1/cases/{caseId}/exemption-analysis", subject);
}
