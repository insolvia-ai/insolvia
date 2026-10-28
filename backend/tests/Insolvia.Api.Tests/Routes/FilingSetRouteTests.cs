using Insolvia.Api.Routes;
using Insolvia.Api.Tests.Support;
using Insolvia.Core.AccessLog;
using Insolvia.Core.Courts;
using Insolvia.Core.Ports;
using Insolvia.Forms;
using static Insolvia.Api.Tests.Support.CaseWorld;

namespace Insolvia.Api.Tests.Routes;

/// <summary>
/// test_filing_set_routes.py, translated: what the route REFUSES, and that a
/// new case gets its court's filing set and what is missing. The set's own
/// content is Forms' FilingSetTests.
/// </summary>
public sealed class FilingSetRouteTests : IAsyncLifetime, IDisposable
{
    private static readonly string[] Statuses = ["ready", "missing", "action", "confirm"];

    private readonly CaseWorld _world = new();

    public async ValueTask InitializeAsync() => await _world.SeedAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _world.Dispose();

    private async Task<string> OpenTxwbAsync() => (await _world.OpenCaseAsync(court: "txwb", division: "austin")).Str("id")!;

    [Fact]
    public async Task An_unauthenticated_caller_is_refused() =>
        Assert.Equal(401, (await _world.Get("/v1/cases/any-id/filing-set", subject: null)).Status);

    [Fact]
    public async Task An_unknown_case_is_not_found() =>
        Assert.Equal(404, (await _world.Get("/v1/cases/no-such-case/filing-set")).Status);

    [Fact]
    public async Task Another_firms_case_is_the_same_404()
    {
        var caseId = await OpenTxwbAsync();
        var missing = await _world.Get("/v1/cases/no-such-case/filing-set", Carol);
        var foreign = await _world.Get($"/v1/cases/{caseId}/filing-set", Carol);
        Assert.Equal(404, foreign.Status);
        Assert.Equal(missing.Body, foreign.Body);
        Assert.Contains(_world.Events, e => e.Action == AccessAction.CaseRead && e.Outcome == AccessOutcome.Denied && e.Principal == Carol);
    }

    [Fact]
    public async Task A_new_case_gets_its_courts_filing_set_and_what_is_missing()
    {
        var caseId = await OpenTxwbAsync();

        var answer = await _world.Get($"/v1/cases/{caseId}/filing-set");

        Assert.Equal(200, answer.Status);
        var body = answer.Object;
        Assert.Equal("""{"code":"txwb","divisionName":"Austin Division","name":"Western District of Texas"}""", Http.FlaskJson.Serialize(body["court"]));
        Assert.Equal("hand_off", body.Str("filingMethod"));
        Assert.StartsWith("courts/us-bankruptcy@", body.Str("registryRelease"), StringComparison.Ordinal);
        Assert.False(body.ContainsKey("packet"));
        var documents = body["documents"]!.AsArray().Select(d => d!.AsObject()).ToList();
        Assert.Equal("form/b101", documents[0].Str("key"));
        Assert.Contains(documents, d => d.Str("key") == "creditor_matrix");
        // TXWB's registry record says B121 is not filed there (unverified).
        Assert.Equal("not_filed", documents.Single(d => d.Str("key") == "form/b121").Str("handling"));
        var checklist = body["checklist"]!.AsArray().Select(i => i!.AsObject()).ToDictionary(i => i.Str("id")!, StringComparer.Ordinal);
        Assert.Equal("missing", checklist["packet"].Str("status"));
        Assert.Equal("packet", checklist["packet"].Str("link"));
        Assert.Equal("confirm", checklist["ssn_statement"].Str("status"));
        Assert.All(checklist.Values, item =>
        {
            Assert.Subset(new HashSet<string>(["id", "status", "title", "detail", "link"]), item.Select(pair => pair.Key).ToHashSet());
            Assert.Contains(item.Str("status"), Statuses);
        });
    }

    [Fact]
    public async Task The_body_is_the_forms_engines_filing_set_and_the_read_is_logged()
    {
        var caseId = await OpenTxwbAsync();
        var answer = await _world.Get($"/v1/cases/{caseId}/filing-set");

        var data = await CaseDataReads.ReadAsync((await _world.StoredAsync(caseId))!, _world.Service<IDebtorStore>(), _world.Service<ICaseEntityStore>(), HttpAssert.Ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var expected = FilingSetRoutes.FilingSetJson(FilingSets.Build(data, [], CourtRegistry.Shipped.Resolve(today), today));

        Assert.Equal(Http.FlaskJson.Serialize(expected), answer.Body.TrimEnd('\n'));
        Assert.Contains(_world.Events, e => e.Action == AccessAction.CaseRead && e.CaseId == caseId && e.Outcome == AccessOutcome.Allowed);
    }
}
