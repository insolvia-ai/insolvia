using Insolvia.Api.Tests.Support;
using Insolvia.Core.AccessLog;
using Insolvia.Core.Candidates;
using Insolvia.Core.Ports;
using static Insolvia.Api.Tests.Support.CaseWorld;

namespace Insolvia.Api.Tests.Routes;

/// <summary>
/// services/api tests/unit/test_portal_answers.py, the client half: an answer
/// is a pending CANDIDATE (provenance <c>client_answered</c> on acceptance),
/// edit-while-pending, withdrawal, and what a client cannot do.
/// </summary>
/// <remarks>
/// The staff review queue (<c>/v1/cases/&lt;id&gt;/extraction/candidates</c>
/// and its <c>…/review</c>) is not ported yet (18.17), so where the Python
/// read the queue these read the candidate store behind the API, and where it
/// accepted an answer through the review route these move the row to
/// <c>accepted</c> in the store. The eight tests that are ABOUT acceptance —
/// the debtor write, <c>client_answered</c> provenance, corrected payloads,
/// appended names, record answers, the employment link, the queue's
/// <c>client</c>/<c>question</c> blocks and the two staff-save provenance
/// tests — belong with the review route and move with it.
/// </remarks>
public sealed class PortalAnswerRouteTests : IAsyncLifetime, IDisposable
{
    private const string LegalName = """{"questionId": "personal_information.legal_name", "value": {"given": "Patricia", "surname": "Example"}}""";
    private const string Vehicle = """{"questionId": "property.vehicle", "value": {"year": 2099, "make": "Examplecar", "value_entire": "1200"}}""";

    private readonly CaseWorld _world = new();
    private string _caseId = string.Empty;
    private string _pat = string.Empty;

    private ICandidateStore Candidates => _world.Service<ICandidateStore>();

    public async ValueTask InitializeAsync()
    {
        await _world.SeedAsync();
        _caseId = await _world.OpenCaseIdAsync();
        _pat = await _world.InvitedAsync(_caseId);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _world.Dispose();

    // ── The done-when ───────────────────────────────────────────────

    [Fact]
    public async Task An_answer_is_a_pending_candidate_and_does_not_touch_the_case()
    {
        var before = await DebtorOneAsync();

        var answer = await Answer(LegalName);

        Assert.True(answer.Status == 201, answer.Body);
        Assert.Equal("pending", answer.Object.Str("status"));
        Assert.Equal("""{"given":"Patricia","surname":"Example"}""", answer.Object["value"]!.ToJsonString());
        Assert.Equal(["createdAt", "filingRole", "id", "questionId", "sectionId", "status", "updatedAt", "value"], Support.Answer.Keys(answer.Object));
        Assert.Equal(before, await DebtorOneAsync());
    }

    [Fact]
    public async Task The_candidate_is_in_the_cases_queue_from_the_client_with_its_question()
    {
        var id = (await Answer(LegalName)).Object.Str("id")!;

        var row = Assert.Single(await Candidates.ListForCaseAsync(_caseId, HttpAssert.Ct));

        Assert.Equal(id, row.Id);
        Assert.Equal((CandidateStatus.Pending, "debtors"), (row.Status, row.EntityType));
        Assert.Equal("""{"name":{"given":"Patricia","surname":"Example"}}""", row.Payload.ToJsonString());
        Assert.Equal("""{"kind":"question","section_id":"personal_information","question_id":"personal_information.legal_name","filing_role":"debtor_1"}""", row.Locator!.ToJsonString());
    }

    [Fact]
    public async Task The_client_reads_back_the_review_status()
    {
        var id = (await Answer(LegalName)).Object.Str("id")!;
        await AcceptAsync(id);

        var mine = Assert.Single((await _world.PortalGet("/v1/portal/answers", _pat)).Object["answers"]!.AsArray())!.AsObject();

        Assert.Equal((id, "accepted"), (mine.Str("id"), mine.Str("status")));
        Assert.False(mine.ContainsKey("confirmedBy"));
    }

    // ── Edit while pending ──────────────────────────────────────────

    [Fact]
    public async Task A_pending_answer_can_be_changed()
    {
        var id = (await Answer(LegalName)).Object.Str("id")!;

        var answer = await _world.PortalPut($"/v1/portal/answers/{id}", """{"value": {"given": "Pat", "surname": "Example"}}""", _pat);

        Assert.True(answer.Status == 200, answer.Body);
        var row = Assert.Single(await Candidates.ListForCaseAsync(_caseId, HttpAssert.Ct));
        Assert.Equal("""{"name":{"given":"Pat","surname":"Example"}}""", row.Payload.ToJsonString());
    }

    [Fact]
    public async Task A_pending_answer_can_be_withdrawn_and_stays_in_the_queue_as_withdrawn()
    {
        var id = (await Answer(LegalName)).Object.Str("id")!;

        var answer = await _world.PortalDelete($"/v1/portal/answers/{id}", _pat);

        Assert.Equal(200, answer.Status);
        Assert.Equal("withdrawn", answer.Object.Str("status"));
        Assert.Equal(CandidateStatus.Withdrawn, Assert.Single(await Candidates.ListForCaseAsync(_caseId, HttpAssert.Ct)).Status);
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task A_reviewed_answer_is_immutable(string method)
    {
        var id = (await Answer(LegalName)).Object.Str("id")!;
        await AcceptAsync(id);

        var answer = method == "PUT"
            ? await _world.PortalPut($"/v1/portal/answers/{id}", """{"value": {"given": "Other", "surname": "Name"}}""", _pat)
            : await _world.PortalDelete($"/v1/portal/answers/{id}", _pat);

        Assert.Equal(409, answer.Status);
    }

    [Fact]
    public async Task A_second_live_answer_to_a_one_answer_question_is_a_conflict()
    {
        await Answer(LegalName);

        Assert.Equal(409, (await Answer(LegalName)).Status);
    }

    [Fact]
    public async Task After_review_the_same_question_takes_a_new_answer()
    {
        await AcceptAsync((await Answer(LegalName)).Object.Str("id")!);

        Assert.Equal(201, (await Answer(LegalName)).Status);
    }

    // ── What a client cannot do ─────────────────────────────────────

    [Fact]
    public async Task A_switched_off_sections_questions_are_not_answerable()
    {
        Assert.Equal(200, (await _world.Put("/v1/firm/questionnaire", PropertyOff)).Status);

        var answer = await Answer(Vehicle);

        Assert.Equal(400, answer.Status);
        Assert.True(answer.Object["fields"]!.AsObject().ContainsKey("questionId"));
    }

    [Fact]
    public async Task Switching_a_section_off_hides_its_answers_from_the_client()
    {
        var id = (await Answer(Vehicle)).Object.Str("id")!;
        await _world.Put("/v1/firm/questionnaire", PropertyOff);

        var listed = await _world.PortalGet("/v1/portal/answers", _pat);
        var deleted = await _world.PortalDelete($"/v1/portal/answers/{id}", _pat);

        Assert.Equal("""{"answers":[]}""", listed.Body);
        Assert.Equal(404, deleted.Status);

        // Staff keep it: a firm that takes a section in-house still reviews it.
        Assert.Equal(id, Assert.Single(await Candidates.ListForCaseAsync(_caseId, HttpAssert.Ct)).Id);
    }

    [Fact]
    public async Task A_client_cannot_answer_for_the_other_debtor()
    {
        var answer = await Answer("""{"questionId": "personal_information.legal_name", "filingRole": "debtor_2", "value": {"given": "P", "surname": "E"}}""");

        Assert.Equal(400, answer.Status);
        Assert.True(answer.Object["fields"]!.AsObject().ContainsKey("filingRole"));
    }

    [Fact]
    public async Task Another_clients_answer_is_not_found()
    {
        var id = (await Answer(LegalName)).Object.Str("id")!;
        var sam = await _world.InvitedAsync(await _world.OpenCaseIdAsync(), "sam@example.test");

        var answer = await _world.PortalPut($"/v1/portal/answers/{id}", """{"value": {"given": "X", "surname": "Y"}}""", sam);

        Assert.Equal(404, answer.Status);
        Assert.Equal("""{"answers":[]}""", (await _world.PortalGet("/v1/portal/answers", sam)).Body);
    }

    [Fact]
    public async Task The_origin_is_the_tokens_not_the_bodys()
    {
        await Answer("""{"questionId": "personal_information.legal_name", "value": {"given": "Patricia", "surname": "Example"}, "origin": {"channel": "extraction", "subject": "00000000-0000-4000-8000-00000000a11c"}}""");

        var row = Assert.Single(await Candidates.ListForCaseAsync(_caseId, HttpAssert.Ct));

        Assert.Equal(new CandidateOrigin(OriginChannel.Client, Tokens.PortalClientId, _pat), row.Origin);
    }

    [Fact]
    public async Task A_staff_token_cannot_answer() =>
        Assert.Equal(401, (await _world.Post("/v1/portal/answers", LegalName)).Status);

    [Fact]
    public async Task Every_answer_request_is_on_the_access_log_as_the_client()
    {
        var before = _world.Events.Count;
        var id = (await Answer(LegalName)).Object.Str("id")!;
        await _world.PortalPut($"/v1/portal/answers/{id}", """{"value": {"given": "Pat", "surname": "Example"}}""", _pat);
        await _world.PortalGet("/v1/portal/answers", _pat);

        var rows = _world.Events.Skip(before).Select(e => (e.Action, e.Principal, e.SubjectId)).ToList();

        Assert.Equal([(AccessAction.PortalAnswer, _pat, _caseId), (AccessAction.PortalAnswer, _pat, _caseId), (AccessAction.PortalRead, _pat, _caseId)], rows);
    }

    [Fact]
    public async Task An_answer_body_over_16_KiB_is_refused()
    {
        var answer = await Answer("{\"questionId\": \"personal_information.legal_name\", \"value\": {\"given\": \"" + new string('x', 17 * 1024) + "\"}}");

        Assert.Equal(400, answer.Status);
        Assert.Equal("request body exceeds 16 KiB", answer.Object.Str("message"));
    }

    [Fact]
    public async Task The_answer_log_line_names_ids_and_the_verb_and_never_the_value()
    {
        await Answer(LegalName);

        var line = Assert.Single(_world.World.Host.Logs.In("insolvia_api.api.routes.portal"));
        Assert.Equal(("portal answer", "created", _caseId), (line.Message, line.Fields["what"], line.Fields["case_id"]));
        Assert.DoesNotContain("Patricia", line.Rendered, StringComparison.Ordinal);
    }

    private static readonly string[] SectionIds = ["personal_information", "property", "debts", "income", "expenses", "other"];

    private static string PropertyOff =>
        "{\"sections\": [" + string.Join(", ", SectionIds
            .Select(id => $$"""{"id": "{{id}}", "enabled": {{(id == "property" ? "false" : "true")}}}""")) + "]}";

    private Task<Answer> Answer(string body) => _world.PortalPost("/v1/portal/answers", body, _pat);

    private async Task<string> DebtorOneAsync()
    {
        var answer = await _world.Get($"/v1/cases/{_caseId}/debtors");
        Assert.True(answer.Status == 200, answer.Body);
        return answer.Object["debtors"]!.AsArray().Single(d => d!["filing_role"]!.GetValue<string>() == "debtor_1")!.ToJsonString();
    }

    // The review route's state change, in the store (see the remarks).
    private async Task AcceptAsync(string candidateId)
    {
        var row = (await Candidates.GetAsync(_caseId, candidateId, HttpAssert.Ct))!;
        Assert.NotNull(await Candidates.UpdateAsync(row with { Status = CandidateStatus.Accepted, ConfirmedBy = Alice, ConfirmedAt = "2026-01-02T00:00:00.000000Z" }, CandidateStatus.Pending, HttpAssert.Ct));
    }
}
