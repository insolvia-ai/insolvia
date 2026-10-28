using System.Text.Json.Nodes;
using Insolvia.Api.Tests.Support;
using Insolvia.Core;
using Insolvia.Core.Candidates;
using Insolvia.Core.Clients;
using Insolvia.Core.Ports;
using Insolvia.Core.Questionnaires;
using Insolvia.Core.Questions;
using static Insolvia.Api.Tests.Support.CaseWorld;

namespace Insolvia.Api.Tests.Routes;

/// <summary>
/// test_portal_answers.py's staff half — the acceptance of a client's portal
/// answer through the review queue (ADR 0023 PR 4): the queue's client and
/// question blocks, a debtor answer MERGED into the debtor with
/// <c>client_answered</c> provenance (every other field untouched), a
/// correction staff_typed, an appended other name, a record answer as a new
/// record, the employment's debtor link, and the two staff-save rules. The
/// answers are planted through Core's own answer rules (the portal's routes
/// are 18.19's); everything after is the HTTP surface.
/// </summary>
public sealed class PortalAnswerReviewTests : IAsyncLifetime, IDisposable
{
    private const string Pat = "00000000-0000-4000-8000-0000000000a1";
    private const string PortalClientId = "portal-client";

    private readonly CaseWorld _world = new();
    private string _caseId = "";

    public async ValueTask InitializeAsync()
    {
        await _world.SeedAsync();
        _caseId = await _world.OpenCaseIdAsync();
        var found = (await _world.StoredAsync(_caseId))!;
        var binding = new ClientBinding(found.FirmId, _caseId, Pat, "pat@example.test", "Pat Example", new[] { ClientRole.Debtor1 }.ToValueCollection(), BindingStatus.Invited, Alice, FirmWorld.Seeded, FirmWorld.Seeded);
        await _world.Service<IClientBindingStore>().BindAsync(binding, [], HttpAssert.Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _world.Dispose();

    private async Task<string> AnswerAsync(string json)
    {
        var candidates = _world.Service<ICandidateStore>();
        var request = QuestionRules.ParseAnswer(JsonNode.Parse(json)!.AsObject(), Questionnaire.ClientSections(null), [ClientRole.Debtor1]);
        var candidate = QuestionRules.CreateAnswer(request, _caseId, new CandidateOrigin(OriginChannel.Client, PortalClientId, Pat), await candidates.ListForCaseAsync(_caseId, HttpAssert.Ct));
        await candidates.CreateAsync(candidate, HttpAssert.Ct);
        return candidate.Id;
    }

    private Task<Answer> Review(string candidateId, string extra = "") =>
        _world.Post($"/v1/cases/{_caseId}/extraction/candidates/{candidateId}/review", "{\"action\": \"accept\"" + extra + "}");

    private async Task<JsonObject> Debtor1Async()
    {
        var answer = await _world.Get($"/v1/cases/{_caseId}/debtors");
        Assert.True(answer.Status == 200, answer.Body);
        return answer.Object["debtors"]!.AsArray().Single(debtor => debtor!["filing_role"]!.GetValue<string>() == "debtor_1")!.AsObject();
    }

    private const string LegalName = """{"questionId": "personal_information.legal_name", "value": {"given": "Patricia", "surname": "Example"}}""";
    private const string Vehicle = """{"questionId": "property.vehicle", "value": {"year": 2099, "make": "Examplecar", "value_entire": "1200"}}""";

    [Fact]
    public async Task Staff_see_the_answer_from_the_client_with_its_question()
    {
        await AnswerAsync(LegalName);
        var row = Assert.Single((await _world.Get($"/v1/cases/{_caseId}/extraction/candidates")).Object["candidates"]!.AsArray())!;
        Assert.Equal("client", row["origin"]!["channel"]!.GetValue<string>());
        Assert.Equal("""{"displayName":"Pat Example"}""", row["client"]!.ToJsonString());
        Assert.Equal(
            """{"filingRole":"debtor_1","id":"personal_information.legal_name","sectionId":"personal_information","sectionTitle":"Personal information","text":"What is your full legal name?"}""",
            row["question"]!.ToJsonString());
    }

    [Fact]
    public async Task Accepting_writes_the_debtor_field_with_client_answered_provenance()
    {
        var answerId = await AnswerAsync(LegalName);
        var answer = await Review(answerId);
        Assert.True(answer.Status == 200, answer.Body);
        var debtor = await Debtor1Async();
        Assert.Equal("""{"given":"Patricia","surname":"Example"}""", debtor["name"]!.ToJsonString());
        var entry = debtor["provenance"]!["name.given"]!.AsObject();
        Assert.Equal("client_answered", entry.Str("source"));
        Assert.Equal(Alice, entry.Str("confirmed_by"));
        Assert.False(string.IsNullOrEmpty(entry.Str("confirmed_at")));
        Assert.Equal(answerId, entry.Str("extraction_id"));
        Assert.False(entry.ContainsKey("locator"));
    }

    [Fact]
    public async Task An_accepted_debtor_answer_leaves_every_other_field_as_it_was()
    {
        var before = await Debtor1Async();
        var answerId = await AnswerAsync("""{"questionId": "personal_information.phone", "value": {"phone": "555-0100"}}""");
        Assert.Equal(200, (await Review(answerId)).Status);
        var after = await Debtor1Async();
        Assert.Equal("555-0100", after.Str("phone"));
        Assert.True(JsonNode.DeepEquals(before["name"], after["name"]));
        Assert.True(JsonNode.DeepEquals(before["provenance"]!["name.given"], after["provenance"]!["name.given"]));
    }

    [Fact]
    public async Task A_corrected_answer_is_staff_typed_where_the_reviewer_changed_it()
    {
        var answerId = await AnswerAsync(LegalName);
        Assert.Equal(200, (await Review(answerId, """, "correctedPayload": {"name": {"given": "Patricia", "surname": "Examples"}}""")).Status);
        var provenance = (await Debtor1Async())["provenance"]!;
        Assert.Equal("client_answered", provenance["name.given"]!["source"]!.GetValue<string>());
        Assert.Equal("staff_typed", provenance["name.surname"]!["source"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_appended_other_name_is_added_not_overwritten()
    {
        foreach (var surname in new[] { "Formerly", "Earlier" })
        {
            var answerId = await AnswerAsync("{\"questionId\": \"personal_information.other_names\", \"value\": {\"surname\": \"" + surname + "\"}}");
            var answer = await Review(answerId);
            Assert.True(answer.Status == 200, answer.Body);
        }

        Assert.Equal(["Formerly", "Earlier"], (await Debtor1Async())["other_names_used"]!.AsArray().Select(name => name!["surname"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_record_answer_becomes_a_new_record_with_client_answered_provenance()
    {
        var answer = await Review(await AnswerAsync(Vehicle));
        Assert.True(answer.Status == 200, answer.Body);
        var record = answer.Object["record"]!;
        Assert.Equal("vehicle", record["category"]!.GetValue<string>());
        Assert.Equal("Examplecar", record["make"]!.GetValue<string>());
        Assert.Equal("client_answered", record["provenance"]!["make"]!["source"]!.GetValue<string>());
        Assert.Equal("client_answered", record["provenance"]!["category"]!["source"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_employment_answer_is_linked_to_the_debtor_who_gave_it()
    {
        var answer = await Review(await AnswerAsync("""{"questionId": "income.employment", "value": {"employer_name": "Example Co"}}"""));
        Assert.True(answer.Status == 200, answer.Body);
        var record = answer.Object["record"]!;
        Assert.Equal((await Debtor1Async()).Str("id"), record["debtor_id"]!.GetValue<string>());
        Assert.Equal("staff_typed", record["provenance"]!["debtor_id"]!["source"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_staff_save_cannot_mint_client_answered_provenance()
    {
        var answer = await _world.Post($"/v1/cases/{_caseId}/assets", $$"""
            {"category": "vehicle", "make": "Examplecar", "provenance": {
              "category": {"source": "staff_typed"},
              "make": {"source": "client_answered", "confirmed_by": "{{Alice}}", "confirmed_at": "2099-01-01T00:00:00.000000Z"} } }
            """);
        Assert.Equal(400, answer.Status);
        Assert.Contains("provenance.make", answer.FieldErrors);
    }

    [Fact]
    public async Task A_staff_save_keeps_an_accepted_answer_it_did_not_touch()
    {
        var record = (await Review(await AnswerAsync(Vehicle))).Object["record"]!.AsObject();
        var body = new JsonObject();
        foreach (var (key, value) in record)
        {
            if (key is not ("id" or "case_id" or "created_at" or "updated_at"))
            {
                body[key] = value?.DeepClone();
            }
        }

        body["model"] = "Roadster";
        body["provenance"]!["model"] = JsonNode.Parse("""{"source": "staff_typed"}""");
        var answer = await _world.Put($"/v1/cases/{_caseId}/assets/{record.Str("id")}", body.ToJsonString());
        Assert.True(answer.Status == 200, answer.Body);
        Assert.Equal("client_answered", answer.Object["provenance"]!["make"]!["source"]!.GetValue<string>());
    }
}
