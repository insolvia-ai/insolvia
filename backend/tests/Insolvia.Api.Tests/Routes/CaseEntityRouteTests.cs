using Insolvia.Api.Tests.Support;
using Insolvia.Core.AccessLog;
using static Insolvia.Api.Tests.Support.CaseWorld;

namespace Insolvia.Api.Tests.Routes;

/// <summary>
/// test_case_entity_routes.py: the generic case collections — tenancy, the
/// unknown collection, the literal routes not shadowed, whole-record writes,
/// provenance, <c>amended</c> (#370), order, and the access log.
/// </summary>
public sealed class CaseEntityRouteTests : IAsyncLifetime, IDisposable
{
    private const string Typed = """{"source": "staff_typed"}""";
    private const string Creditor = """{"name": "Example Bank", "address": {"line1": "1 Example Way", "city": "Exampleville"}, "provenance": {"name": {"source": "staff_typed"}, "address.line1": {"source": "staff_typed"}, "address.city": {"source": "staff_typed"}}}""";

    private readonly CaseWorld _world = new();

    public async ValueTask InitializeAsync() => await _world.SeedAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _world.Dispose();

    [Theory]
    [InlineData("POST", "/v1/cases/any-id/creditors")]
    [InlineData("GET", "/v1/cases/any-id/creditors")]
    [InlineData("GET", "/v1/cases/any-id/creditors/any-entity")]
    [InlineData("PUT", "/v1/cases/any-id/creditors/any-entity")]
    [InlineData("DELETE", "/v1/cases/any-id/creditors/any-entity")]
    public async Task Every_route_refuses_an_unauthenticated_caller(string method, string path)
    {
        Assert.Equal(401, (await _world.World.SendAsync(new HttpMethod(method), path, json: "{}")).Status);
    }

    [Fact]
    public async Task Another_firms_case_is_not_found_and_its_records_are_indistinguishable_from_missing()
    {
        var caseId = await _world.OpenCaseIdAsync();
        Assert.Equal(404, (await Add(caseId, subject: Carol)).Status);
        var entityId = (await Add(caseId)).Str("id");

        var foreign = await _world.Get($"/v1/cases/{caseId}/creditors/{entityId}", Carol);
        var missing = await _world.Get("/v1/cases/no-such-case/creditors/no-such-id", Carol);

        Assert.Equal(404, foreign.Status);
        Assert.Equal(missing.Body, foreign.Body);
    }

    [Fact]
    public async Task An_unknown_collection_is_a_plain_404_and_an_id_does_not_cross_collections()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var entityId = (await Add(caseId)).Str("id");

        Assert.Equal("""{"error":"NotFoundError","message":"no such collection"}""", (await _world.Get($"/v1/cases/{caseId}/pay_stubs")).Body);
        Assert.Equal(404, (await _world.Get($"/v1/cases/{caseId}/claims/{entityId}")).Status);
    }

    [Fact]
    public async Task The_literal_debtor_and_document_routes_are_not_shadowed()
    {
        var caseId = await _world.OpenCaseIdAsync();

        Assert.Equal(200, (await _world.Get($"/v1/cases/{caseId}/debtors")).Status);
        Assert.Equal("""{"documents":[]}""", (await _world.Get($"/v1/cases/{caseId}/documents")).Body);
    }

    [Fact]
    public async Task Adding_a_record_answers_201_with_the_stored_record()
    {
        var caseId = await _world.OpenCaseIdAsync();

        var answer = await Add(caseId);

        Assert.Equal(201, answer.Status);
        var record = answer.Object;
        Assert.Equal("Example Bank", record.Str("name"));
        Assert.Equal("""{"city":"Exampleville","line1":"1 Example Way"}""", Http.FlaskJson.Serialize(record["address"]));
        Assert.Equal(caseId, record.Str("case_id"));
        Assert.Equal("""{"source":"staff_typed"}""", Http.FlaskJson.Serialize(record["provenance"]!["name"]));
        Assert.Equal(record.Str("created_at"), record.Str("updated_at"));
        Assert.Equal(["address", "amended", "case_id", "created_at", "id", "name", "provenance", "updated_at"], Answer.Keys(record));
    }

    [Theory]
    [InlineData("""{"name": "Example Bank", "provenance": {}}""")]
    [InlineData("""{"name": "Example Bank", "provenance": {"name": {"source": "ai_extracted"}}}""")]
    public async Task A_value_without_confirmed_provenance_is_a_400(string body)
    {
        var answer = await Add(await _world.OpenCaseIdAsync(), body);

        Assert.Equal(400, answer.Status);
        Assert.Contains("provenance.name", answer.FieldErrors);
    }

    [Fact]
    public async Task Editing_replaces_the_record_whole_and_an_unminted_id_is_404()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var entityId = (await Add(caseId)).Str("id");

        var answer = await _world.Put($"/v1/cases/{caseId}/creditors/{entityId}", """{"name": "Renamed Bank", "provenance": {"name": {"source": "staff_typed"}}}""");

        Assert.Equal(200, answer.Status);
        Assert.Equal(("Renamed Bank", entityId), (answer.Str("name"), answer.Str("id")));
        Assert.False(answer.Object.ContainsKey("address"));
        Assert.Equal(404, (await _world.Put($"/v1/cases/{caseId}/creditors/never-minted", """{"name": "Example", "provenance": {"name": {"source": "staff_typed"}}}""")).Status);
    }

    [Fact]
    public async Task Removing_a_record_answers_204_then_404()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var entityId = (await Add(caseId)).Str("id");

        Assert.Equal(204, (await _world.Delete($"/v1/cases/{caseId}/creditors/{entityId}")).Status);
        Assert.Equal(404, (await _world.Delete($"/v1/cases/{caseId}/creditors/{entityId}")).Status);
        Assert.Equal("""{"creditors":[]}""", (await _world.Get($"/v1/cases/{caseId}/creditors")).Body);
    }

    // ── amended (#370) ──────────────────────────────────────────────

    [Fact]
    public async Task Amended_defaults_false_and_true_is_refused_before_filing_on_create_and_edit()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var created = await Add(caseId);
        Assert.False(created.Object["amended"]!.GetValue<bool>());

        Assert.Equal(409, (await Add(caseId, Creditor[..^1] + """, "amended": true}""")).Status);
        Assert.Equal(409, (await _world.Put($"/v1/cases/{caseId}/creditors/{created.Str("id")}", """{"name": "Renamed Bank", "provenance": {"name": {"source": "staff_typed"}}, "amended": true}""")).Status);
        Assert.False((await Add(caseId, Creditor[..^1] + """, "amended": false}""")).Object["amended"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Amended_true_is_accepted_once_filed_and_must_be_a_boolean()
    {
        var caseId = await _world.OpenCaseIdAsync();
        await _world.FileAsync(caseId);

        var answer = await Add(caseId, Creditor[..^1] + """, "amended": true}""");

        Assert.Equal(201, answer.Status);
        Assert.True(answer.Object["amended"]!.GetValue<bool>());
        Assert.Contains("amended", (await Add(caseId, Creditor[..^1] + """, "amended": "yes"}""")).FieldErrors);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData(""", "amended": false""", false)]
    public async Task A_put_that_omits_amended_keeps_the_stored_flag(string sent, bool expected)
    {
        var caseId = await _world.OpenCaseIdAsync();
        await _world.FileAsync(caseId);
        var entityId = (await Add(caseId, Creditor[..^1] + """, "amended": true}""")).Str("id");

        var answer = await _world.Put($"/v1/cases/{caseId}/creditors/{entityId}", """{"name": "Renamed Bank", "provenance": {"name": {"source": "staff_typed"}}""" + sent + "}");

        Assert.Equal(200, answer.Status);
        Assert.Equal(expected, answer.Object["amended"]!.GetValue<bool>());
    }

    // ── Lists ───────────────────────────────────────────────────────

    [Fact]
    public async Task Listing_returns_records_in_creation_order_and_collections_separately()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var first = (await Add(caseId)).Str("id");
        var second = (await Add(caseId, """{"name": "Second Bank", "provenance": {"name": """ + Typed + "}}")).Str("id");
        var expense = await _world.Post($"/v1/cases/{caseId}/expenses", """{"category": "rent_or_home_ownership", "amount": "1800.00", "provenance": {"category": {"source": "staff_typed"}, "amount": {"source": "staff_typed"}}}""");
        Assert.Equal(201, expense.Status);

        var creditors = (await _world.Get($"/v1/cases/{caseId}/creditors")).Object["creditors"]!.AsArray();
        Assert.Equal([first, second], creditors.Select(c => c!.AsObject().Str("id")));
        Assert.Single((await _world.Get($"/v1/cases/{caseId}/expenses")).Object["expenses"]!.AsArray());
    }

    [Fact]
    public async Task A_sofa_entry_round_trips_over_the_api()
    {
        var caseId = await _world.OpenCaseIdAsync();

        var answer = await _world.Post($"/v1/cases/{caseId}/sofa_entries", """{"entry_type": "gift", "payload": {"recipient": {"name": "Example Recipient"}, "value": "700.00"}, "provenance": {"entry_type": {"source": "staff_typed"}, "payload.recipient.name": {"source": "staff_typed"}, "payload.value": {"source": "staff_typed"}}}""");

        Assert.Equal(201, answer.Status);
        Assert.Equal("gift", answer.Str("entry_type"));
        Assert.Equal("""{"recipient":{"name":"Example Recipient"},"value":"700.00"}""", Http.FlaskJson.Serialize(answer.Object["payload"]));
    }

    // ── The access log ──────────────────────────────────────────────

    [Fact]
    public async Task Reads_and_writes_are_recorded_either_way_and_a_malformed_body_never_is()
    {
        var caseId = await _world.OpenCaseIdAsync();
        await Add(caseId);
        await _world.Get($"/v1/cases/{caseId}/creditors");
        await _world.Get($"/v1/cases/{caseId}/creditors", Carol);

        var outcomes = _world.Events.Where(e => e.CaseId == caseId).Select(e => (e.Action, e.Outcome)).ToList();
        Assert.Contains((AccessAction.CaseUpdate, AccessOutcome.Allowed), outcomes);
        Assert.Contains((AccessAction.CaseRead, AccessOutcome.Allowed), outcomes);
        Assert.Contains((AccessAction.CaseRead, AccessOutcome.Denied), outcomes);

        var before = _world.Events.Count;
        Assert.Equal(400, (await Add(caseId, """{"name": 7}""")).Status);
        Assert.Equal(before, _world.Events.Count);
    }

    private Task<Answer> Add(string caseId, string body = Creditor, string subject = Alice) =>
        _world.Post($"/v1/cases/{caseId}/creditors", body, subject);
}
