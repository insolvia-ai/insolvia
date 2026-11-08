using System.Text.Json.Nodes;
using Insolvia.Core.Candidates;
using Insolvia.Core.Clients;
using Insolvia.Core.Questionnaires;
using Insolvia.Core.Questions;

namespace Insolvia.Core.Tests;

/// <summary>The portal's questions and a client's answers, translated from test_questions.py.</summary>
public sealed class QuestionTests
{
    private const string CaseId = "00000000-0000-4000-8000-0000000ca5e1";
    private const string ClientSub = "00000000-0000-4000-8000-0000000c11e1";
    private const string OtherSub = "00000000-0000-4000-8000-0000000c11e2";
    private const string PortalApp = "portal-app-client-id";

    private const string LegalName = """{"questionId": "personal_information.legal_name", "value": {"given": "Testy", "surname": "Example"}}""";
    private const string Vehicle = """{"questionId": "property.vehicle", "value": {"year": 2099, "make": "Examplecar", "value_entire": "1200"}}""";

    private static readonly CandidateOrigin Origin = new(OriginChannel.Client, PortalApp, ClientSub);

    private static IReadOnlyList<ResolvedSection> AllOn => Questionnaire.Resolve(null);

    private static IReadOnlyList<ResolvedSection> PropertyOff() => Questionnaire.Resolve(new QuestionnaireConfig(
        Ids.Firm,
        new Dictionary<string, SectionSetting> { [Questionnaire.Property] = new(Enabled: false) }.ToValueDictionary(),
        DateTimeOffset.UnixEpoch,
        Ids.Alice));

    private static AnswerRequest Answer(string body, ClientRole[]? roles = null, IReadOnlyList<ResolvedSection>? sections = null) =>
        QuestionRules.ParseAnswer(Body.Of(body), sections ?? AllOn, roles ?? [ClientRole.Debtor1]);

    private static Candidate Stored(string body, CandidateOrigin? origin = null, IReadOnlyList<Candidate>? existing = null) =>
        QuestionRules.CreateAnswer(Answer(body), CaseId, origin ?? Origin, existing ?? []);

    private static void AssertJson(string expected, JsonNode? actual) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), actual), $"{expected} != {actual?.ToJsonString()}");

    // ── The catalogue ───────────────────────────────────────────────

    [Fact]
    public void Every_question_hangs_off_a_section_and_is_prefixed_by_it() =>
        Assert.All(QuestionRules.Catalogue, entry =>
        {
            Assert.Contains(entry.Section, Questionnaire.SectionIds);
            Assert.StartsWith($"{entry.Section}.", entry.Id, StringComparison.Ordinal);
        });

    [Fact]
    public void Question_ids_are_unique_because_a_stored_answer_names_one() =>
        Assert.Equal(QuestionRules.QuestionIds.Count, QuestionRules.QuestionIds.Distinct().Count());

    [Fact]
    public void Every_section_has_at_least_one_question() =>
        Assert.All(Questionnaire.SectionIds, sectionId => Assert.NotEmpty(QuestionRules.For(sectionId)));

    [Fact]
    public void The_tax_id_is_never_asked_through_a_candidate() =>
        Assert.All(QuestionRules.Catalogue, entry =>
        {
            Assert.DoesNotContain("tax_id", entry.InputKeys);
            Assert.NotEqual("tax_id", entry.DebtorField);
        });

    [Fact]
    public void The_vehicle_question_asks_its_five_boxes_in_order()
    {
        // test_question_json_carries_what_the_portal_renders_and_no_target and
        // test_the_portal_questionnaire_carries_each_enabled_sections_questions
        // asserted the portal's JSON views (the API's); what they read is here.
        var vehicle = QuestionRules.Find("property.vehicle")!;
        Assert.True(vehicle.Repeats);
        Assert.Equal(["year", "make", "model", "mileage", "value_entire"], vehicle.InputKeys);
        Assert.DoesNotContain(QuestionRules.For(Questionnaire.Property), entry => entry.Retired);
    }

    // ── Building the answer ─────────────────────────────────────────

    [Fact]
    public void A_nested_answer_becomes_the_debtor_field_it_maps_to()
    {
        var parsed = Answer(LegalName);
        Assert.Equal("debtors", parsed.Question.Target);
        Assert.Equal(ClientRole.Debtor1, parsed.FilingRole);
        AssertJson("""{"name": {"given": "Testy", "surname": "Example"}}""", parsed.Payload);
    }

    [Fact]
    public void A_record_answer_carries_its_fixed_category_and_canonical_values() =>
        AssertJson("""{"year": 2099, "make": "Examplecar", "value_entire": "1200.00", "category": "vehicle"}""", Answer(Vehicle).Payload);

    [Fact]
    public void A_key_the_question_does_not_ask_is_dropped_not_stored() =>
        AssertJson(
            """{"make": "Examplecar", "category": "vehicle"}""",
            Answer("""{"questionId": "property.vehicle", "value": {"make": "Examplecar", "category": "real_property", "detail": "x"}}""").Payload);

    [Fact]
    public void An_address_answer_never_carries_the_raw_fallback() =>
        AssertJson(
            """{"residence_address": {"line1": "1 Example Way", "city": "Testville"}}""",
            Answer("""{"questionId": "personal_information.residence_address", "value": {"residence_address": {"line1": "1 Example Way", "city": "Testville", "raw": "unparsed"}}}""").Payload);

    [Fact]
    public void An_appended_other_name_gets_an_id_provenance_can_address()
    {
        var element = Assert.Single(Answer("""{"questionId": "personal_information.other_names", "value": {"surname": "Formerly"}}""").Payload["other_names_used"]!.AsArray())!;
        Assert.Equal("Formerly", (string)element["surname"]!);
        Assert.False(string.IsNullOrEmpty((string?)element["id"]));
    }

    [Theory]
    [InlineData("""{"questionId": "property.vehicle", "value": {"make": "X", "year": "new"}}""", "value.year")]
    [InlineData("""{"questionId": "expenses.housing", "value": {"amount": "-5"}}""", "value.amount")]
    [InlineData("""{"questionId": "personal_information.legal_name", "value": {"given": "A"}}""", "value.surname")]
    [InlineData("""{"questionId": "personal_information.legal_name", "value": {"given": 7}}""", "value.given")]
    [InlineData("""{"questionId": "expenses.food", "value": {}}""", "value.amount")]
    [InlineData("""{"questionId": "personal_information.mailing_address", "value": {}}""", "value")]
    [InlineData("""{"questionId": "expenses.food", "value": "12"}""", "value")]
    [InlineData("""{"questionId": "no.such_question", "value": {}}""", "questionId")]
    public void A_malformed_answer_names_the_box_to_fix(string body, string key) =>
        Assert.Contains(key, Assert.Throws<FieldValidationException>(() => Answer(body)).Fields.Keys);

    [Fact]
    public void A_question_in_a_switched_off_section_is_not_answerable() =>
        Assert.Contains("questionId", Assert.Throws<FieldValidationException>(() => Answer(Vehicle, sections: PropertyOff())).Fields.Keys);

    [Fact]
    public void A_one_role_binding_answers_for_its_own_role_by_default() =>
        Assert.Equal(ClientRole.Debtor2, Answer(LegalName, roles: [ClientRole.Debtor2]).FilingRole);

    [Fact]
    public void A_two_role_binding_must_say_which_debtor() =>
        Assert.Contains("filingRole", Assert.Throws<FieldValidationException>(() => Answer(LegalName, roles: [ClientRole.Debtor1, ClientRole.Debtor2])).Fields.Keys);

    [Theory]
    [InlineData("debtor_2")]
    [InlineData("non_filing_spouse")]
    [InlineData("both")]
    public void A_client_cannot_answer_for_a_role_their_binding_lacks(string role)
    {
        var body = Body.Of(LegalName);
        body["filingRole"] = role;
        Assert.Contains("filingRole", Assert.Throws<FieldValidationException>(() => Answer(body.ToJsonString(), roles: [ClientRole.Debtor1])).Fields.Keys);
    }

    // ── Storing it ──────────────────────────────────────────────────

    [Fact]
    public void An_answer_is_a_pending_candidate_with_a_question_locator()
    {
        var candidate = Stored(LegalName);
        Assert.Equal(CandidateStatus.Pending, candidate.Status);
        Assert.Equal("debtors", candidate.EntityType);
        Assert.Equal(Origin, candidate.Origin);
        AssertJson("""{"kind": "question", "section_id": "personal_information", "question_id": "personal_information.legal_name", "filing_role": "debtor_1"}""", candidate.Locator);
        Assert.Null(candidate.DocumentId);
    }

    [Fact]
    public void A_second_live_answer_to_a_one_answer_question_is_refused() =>
        Assert.Throws<ConflictException>(() => Stored(LegalName, existing: [Stored(LegalName)]));

    [Fact]
    public void Once_reviewed_a_one_answer_question_takes_a_new_answer() =>
        Assert.Equal(CandidateStatus.Pending, Stored(LegalName, existing: [Stored(LegalName) with { Status = CandidateStatus.Accepted }]).Status);

    [Fact]
    public void A_repeating_question_takes_many_answers()
    {
        var first = Stored(Vehicle);
        Assert.NotEqual(first.Id, Stored(Vehicle, existing: [first]).Id);
    }

    [Fact]
    public void Another_persons_pending_answer_does_not_block_mine()
    {
        var theirs = Stored(LegalName, origin: new CandidateOrigin(OriginChannel.Client, PortalApp, OtherSub));
        Assert.Equal(CandidateStatus.Pending, Stored(LegalName, existing: [theirs]).Status);
    }

    [Fact]
    public void The_pending_answers_a_client_may_hold_are_bounded()
    {
        var pending = Enumerable.Range(0, QuestionRules.MaxPendingAnswers).Select(_ => Stored(Vehicle)).ToList();
        Assert.Throws<ConflictException>(() => Stored(Vehicle, existing: pending));
    }

    // ── Reading and editing it ──────────────────────────────────────

    [Fact]
    public void The_client_reads_back_their_own_value_only()
    {
        // test_the_client_reads_back_their_own_value_and_status_only asserted
        // the portal JSON view; the value it carried is AnswerValue's.
        var candidate = Stored(Vehicle) with { Status = CandidateStatus.Accepted, ConfirmedBy = Ids.Alice, CorrectedPayload = Body.Of("""{"make": "Changed"}""") };
        var found = QuestionRules.RefOf(candidate)!;
        AssertJson("""{"year": 2099, "make": "Examplecar", "value_entire": "1200.00"}""", QuestionRules.AnswerValue(found.Question, candidate.Payload));
        Assert.Equal("property.vehicle", found.Question.Id);
    }

    [Fact]
    public void A_pending_answer_is_editable_and_keeps_its_question_and_element()
    {
        var candidate = Stored("""{"questionId": "personal_information.other_names", "value": {"surname": "A"}}""");
        var revised = QuestionRules.ReviseAnswer(candidate, QuestionRules.RefOf(candidate)!, Body.Of("""{"surname": "B"}"""));
        Assert.Same(candidate.Locator, revised.Locator);
        Assert.Equal((string?)candidate.Payload["other_names_used"]![0]!["id"], (string?)revised.Payload["other_names_used"]![0]!["id"]);
        Assert.Equal("B", (string?)revised.Payload["other_names_used"]![0]!["surname"]);
    }

    [Fact]
    public void A_reviewed_answer_cannot_be_edited()
    {
        var candidate = Stored(Vehicle) with { Status = CandidateStatus.Accepted };
        Assert.Throws<ConflictException>(() => QuestionRules.ReviseAnswer(candidate, QuestionRules.RefOf(candidate)!, Body.Of("""{"make": "Other"}""")));
    }

    [Fact]
    public void Someone_elses_candidate_is_not_found_rather_than_forbidden()
    {
        var mcp = CandidateRules.Create(new ProposalDraft("assets", [], null, null), CaseId, new CandidateOrigin(OriginChannel.Mcp, "x", ClientSub));
        var theirs = Stored(Vehicle, origin: new CandidateOrigin(OriginChannel.Client, PortalApp, OtherSub));
        foreach (var candidate in new[] { mcp, theirs, null })
        {
            Assert.Throws<NotFoundException>(() => QuestionRules.OwnAnswerOr404(candidate, ClientSub, AllOn));
        }
    }

    [Fact]
    public void An_answer_in_a_section_since_switched_off_is_gone_for_the_client() =>
        Assert.Throws<NotFoundException>(() => QuestionRules.OwnAnswerOr404(Stored(Vehicle), ClientSub, PropertyOff()));

    // test_the_review_queue_labels_an_answer_with_its_section_and_question:
    // the staff review JSON view; dropped. The reference it labels:
    [Fact]
    public void An_answer_names_its_question_and_the_debtor_it_was_for()
    {
        var found = QuestionRules.RefOf(Stored(LegalName))!;
        Assert.Equal(("personal_information.legal_name", "personal_information", "debtor_1"), (found.Question.Id, found.Question.Section, found.FilingRole));
    }

    [Fact]
    public void A_non_client_candidate_has_no_question()
    {
        var mcp = CandidateRules.Create(
            new ProposalDraft("assets", [], null, null),
            CaseId,
            new CandidateOrigin(OriginChannel.Mcp, "x", ClientSub),
            locator: Body.Of("""{"kind": "question", "question_id": "property.vehicle"}"""));
        Assert.Null(QuestionRules.RefOf(mcp));
    }
}
