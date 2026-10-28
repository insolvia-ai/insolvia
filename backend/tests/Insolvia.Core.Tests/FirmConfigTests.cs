using Insolvia.Core.DocumentRequests;
using Insolvia.Core.Documents;
using Insolvia.Core.Fields;
using Insolvia.Core.LibraryCreditors;
using Insolvia.Core.Questionnaires;

namespace Insolvia.Core.Tests;

// Translated from test_library_creditors.py: the firm's reusable creditor
// library (issue 13.9) — parsing and the whole-record replace.
public sealed class LibraryCreditorTests
{
    [Fact]
    public void A_name_is_required() =>
        Assert.Contains("name", Assert.Throws<FieldValidationException>(() => LibraryCreditorRules.Parse(Body.Of("{}"))).Fields.Keys);

    [Fact]
    public void A_blank_name_is_also_refused() =>
        Assert.Contains("name", Assert.Throws<FieldValidationException>(() => LibraryCreditorRules.Parse(Body.Of("""{"name": "   "}"""))).Fields.Keys);

    [Fact]
    public void Everything_but_the_name_is_optional()
    {
        var draft = LibraryCreditorRules.Parse(Body.Of("""{"name": "Acme Collections"}"""));
        Assert.Equal("Acme Collections", draft.Name);
        Assert.Equal(new Address(), draft.Address);
        Assert.Empty(draft.AdditionalNoticeParties);
        Assert.False(draft.Preferred);
        Assert.Null(draft.Notes);
    }

    [Fact]
    public void The_address_is_parsed()
    {
        var draft = LibraryCreditorRules.Parse(Body.Of("""{"name": "Acme Collections", "address": {"line1": "1 Main St", "city": "Springfield", "state": "IL"}}"""));
        Assert.Equal("1 Main St", draft.Address.Line1);
        Assert.Equal("Springfield", draft.Address.City);
    }

    [Fact]
    public void Additional_notice_parties_use_the_claim_notice_party_shape()
    {
        var draft = LibraryCreditorRules.Parse(Body.Of("""
            {"name": "Acme Collections", "additional_notice_parties": [{"id": "np1", "name": "Acme Legal Dept", "account_last4": "1234"}]}
            """));
        var party = Assert.Single(draft.AdditionalNoticeParties);
        Assert.Equal(("np1", "Acme Legal Dept", "1234"), (party.Id, party.Name, party.AccountLast4));
    }

    [Fact]
    public void A_notice_party_with_no_id_is_refused_under_the_library_field_name()
    {
        var fields = Assert.Throws<FieldValidationException>(() => LibraryCreditorRules.Parse(
            Body.Of("""{"name": "Acme Collections", "additional_notice_parties": [{"name": "x"}]}"""))).Fields;
        Assert.Contains("additional_notice_parties[0].id", fields.Keys);
        Assert.DoesNotContain(fields.Keys, key => key.StartsWith("notice_parties", StringComparison.Ordinal));
    }

    [Fact]
    public void Preferred_defaults_to_false_but_is_settable() =>
        Assert.True(LibraryCreditorRules.Parse(Body.Of("""{"name": "Acme Collections", "preferred": true}""")).Preferred);

    [Fact]
    public void Notes_accepts_multiple_lines() =>
        Assert.Equal("Line one\nLine two", LibraryCreditorRules.Parse(Body.Of("""{"name": "Acme Collections", "notes": "Line one\nLine two"}""")).Notes);

    [Fact]
    public void Creation_mints_an_id_and_stamps_the_firm()
    {
        var creditor = LibraryCreditorRules.Create(LibraryCreditorRules.Parse(Body.Of("""{"name": "Acme Collections"}""")), Ids.Firm);
        Assert.Equal(Ids.Firm, creditor.FirmId);
        Assert.False(string.IsNullOrEmpty(creditor.Id));
        Assert.Equal(creditor.CreatedAt, creditor.UpdatedAt);
    }

    [Fact]
    public void Replace_keeps_identity_and_bumps_updated_at()
    {
        var original = LibraryCreditorRules.Create(LibraryCreditorRules.Parse(Body.Of("""{"name": "Acme Collections"}""")), Ids.Firm);
        var edited = LibraryCreditorRules.Replace(original, LibraryCreditorRules.Parse(Body.Of("""{"name": "Acme Collections LLC"}""")));
        Assert.Equal((original.Id, original.FirmId, original.CreatedAt), (edited.Id, edited.FirmId, edited.CreatedAt));
        Assert.Equal("Acme Collections LLC", edited.Name);
        Assert.True(edited.UpdatedAt >= original.UpdatedAt);
    }

    [Fact]
    public void A_library_creditor_carries_the_expected_fields()
    {
        var properties = typeof(LibraryCreditor).GetProperties().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Superset(
            new HashSet<string>(StringComparer.Ordinal) { "FirmId", "Id", "Name", "Address", "AdditionalNoticeParties", "Preferred", "Notes", "CreatedAt", "UpdatedAt" },
            properties);
    }
}

// Translated from test_questionnaire.py: the section catalogue and a firm's
// config of it (ADR 0023 PR 3). The JSON-view tests are restated against
// Resolve/ClientSections — the decisions those views encoded.
public sealed class QuestionnaireTests
{
    // A whole-record save with every section on and no instructions; each
    // override's members are MERGED over its section's, as Python's `**` did.
    private static string Save(params (string Id, string Members)[] overrides)
    {
        var sections = new System.Text.Json.Nodes.JsonArray();
        foreach (var id in Questionnaire.SectionIds)
        {
            var section = new System.Text.Json.Nodes.JsonObject { ["id"] = id, ["enabled"] = true };
            foreach (var (overrideId, members) in overrides)
            {
                if (overrideId != id)
                {
                    continue;
                }

                foreach (var (key, value) in Body.Of(members))
                {
                    section[key] = value?.DeepClone();
                }
            }

            sections.Add(section);
        }

        return new System.Text.Json.Nodes.JsonObject { ["sections"] = sections }.ToJsonString();
    }

    private static QuestionnaireConfig ConfigFrom(string body) =>
        Questionnaire.BuildConfig(Questionnaire.ParseUpdate(Body.Of(body)), Ids.Firm, Ids.Alice);

    private static IReadOnlyDictionary<string, string> Refused(string body) =>
        Assert.Throws<FieldValidationException>(() => Questionnaire.ParseUpdate(Body.Of(body))).Fields;

    [Fact]
    public void The_catalogue_ids_are_the_contract_the_answers_will_hang_off() =>
        Assert.Equal(["personal_information", "property", "debts", "income", "expenses", "other"], Questionnaire.SectionIds);

    [Fact]
    public void Only_personal_information_is_always_on() =>
        Assert.Equal(["personal_information"], Questionnaire.Catalogue.Where(section => !section.Switchable).Select(section => section.Id));

    [Fact]
    public void A_firm_with_no_config_shows_its_clients_every_section_with_defaults()
    {
        var sections = Questionnaire.ClientSections(null);
        Assert.Equal(Questionnaire.SectionIds, sections.Select(section => section.Section.Id));
        Assert.Equal(Questionnaire.Catalogue[1].DefaultInstructions, sections[1].Instructions);
    }

    [Fact]
    public void A_firm_with_no_config_reads_as_default() =>
        Assert.All(Questionnaire.Resolve(null), section => Assert.True(section.Enabled && section.CustomInstructions is null));

    [Fact]
    public void A_section_switched_off_is_gone_from_the_client_and_kept_for_staff()
    {
        var config = ConfigFrom(Save(("expenses", """{"enabled": false}""")));
        Assert.DoesNotContain("expenses", Questionnaire.ClientSections(config).Select(section => section.Section.Id));
        var staff = Questionnaire.Resolve(config);
        Assert.False(staff.Single(section => section.Section.Id == "expenses").Enabled);
        Assert.Equal(Questionnaire.SectionIds, staff.Select(section => section.Section.Id));
    }

    [Fact]
    public void Personal_information_cannot_be_switched_off()
    {
        var body = Save(("personal_information", """{"enabled": false}"""));
        Assert.Equal(
            new Dictionary<string, string> { ["sections.personal_information.enabled"] = "Personal information is always on." },
            Refused(body));
    }

    [Fact]
    public void Personal_information_is_on_even_if_a_stored_row_says_otherwise()
    {
        var config = ConfigFrom(Save());
        var tampered = config with
        {
            Sections = config.Sections.Select(pair => pair.Key == "personal_information" ? new(pair.Key, new SectionSetting(false)) : pair).ToValueDictionary(),
        };
        Assert.True(Questionnaire.Resolve(tampered)[0].Enabled);
    }

    [Fact]
    public void The_firms_instructions_replace_the_default_for_the_client()
    {
        var config = ConfigFrom(Save(("debts", """{"instructions": "  Bring your last statement.  "}""")));
        var debts = Questionnaire.ClientSections(config)[2];
        Assert.Equal(("debts", "Debts", "Bring your last statement."), (debts.Section.Id, debts.Section.Title, debts.Instructions));
    }

    public static TheoryData<string> SaysNothingNew() =>
    [
        "\"\"",
        "\"   \"",
        "null",
        System.Text.Json.JsonSerializer.Serialize(Questionnaire.Catalogue[3].DefaultInstructions),
    ];

    [Theory]
    [MemberData(nameof(SaysNothingNew))]
    public void Instructions_that_say_nothing_new_are_stored_as_the_default(string instructions)
    {
        var config = ConfigFrom(Save(("income", $$"""{"instructions": {{instructions}}}""")));
        Assert.Null(config.Sections["income"].Instructions);
    }

    [Fact]
    public void Overlong_instructions_are_refused_beside_their_section() =>
        Assert.Equal(
            new Dictionary<string, string> { ["sections.property.instructions"] = $"Must be {Questionnaire.MaxInstructions} characters or fewer." },
            Refused(Save(("property", $$"""{"instructions": "{{new string('x', Questionnaire.MaxInstructions + 1)}}"}"""))));

    public static TheoryData<string, string, string> NotOneWholeRecord()
    {
        var all = Questionnaire.SectionIds.Select(id => $$"""{"id": "{{id}}", "enabled": true}""").ToList();
        var allButLast = string.Join(", ", all.Take(all.Count - 1));
        var withIncomeFlagAbsent = string.Join(", ", Questionnaire.SectionIds.Select(id => id == "income" ? """{"id": "income"}""" : $$"""{"id": "{{id}}", "enabled": true}"""));
        return new()
        {
            { "{}", "sections", "Must be a list of sections." },
            { $$"""{"sections": [{{allButLast}}]}""", "sections.other", "Missing from the save." },
            { $$"""{"sections": [{{string.Join(", ", all)}}, {"id": "debts", "enabled": false}]}""", "sections.debts", "Named more than once." },
            { $$"""{"sections": [{{string.Join(", ", all)}}, {"id": "hobbies", "enabled": true}]}""", "sections.6.id", "Must be one of " + string.Join(", ", Questionnaire.SectionIds) },
            { Save(("debts", """{"enabled": "no"}""")), "sections.debts.enabled", "Must be true or false." },
            { $$"""{"sections": [{{withIncomeFlagAbsent}}]}""", "sections.income.enabled", "Must be true or false." },
        };
    }

    [Theory]
    [MemberData(nameof(NotOneWholeRecord))]
    public void A_save_that_is_not_one_whole_record_is_refused(string body, string field, string message) =>
        Assert.Equal(new Dictionary<string, string> { [field] = message }, Refused(body));

    [Fact]
    public void A_section_added_after_the_firm_saved_takes_its_default()
    {
        var config = ConfigFrom(Save(("other", """{"enabled": false}""")));
        var withoutIncome = config with { Sections = config.Sections.Where(pair => pair.Key != "income").ToValueDictionary() };
        var resolved = Questionnaire.Resolve(withoutIncome).ToDictionary(section => section.Section.Id, StringComparer.Ordinal);
        Assert.True(resolved["income"].Enabled);
        Assert.False(resolved["other"].Enabled);
    }
}

// Translated from test_document_requests.py's FIRM half: the checklist
// (ADR 0023 PR 5). The request half — create, satisfy, progress, the case's
// store — is 18.7's, with the rest of that file.
public sealed class DocumentChecklistTests
{
    [Fact]
    public void The_default_checklist_covers_the_issues_documents_with_known_kinds()
    {
        Assert.Equal(
            ["Pay stubs for the last six months", "Tax returns for the last two years", "Bank statements", "Vehicle titles", "Mortgage statements", "Identification"],
            DocumentChecklists.DefaultChecklist.Select(item => item.Title));
        Assert.All(DocumentChecklists.DefaultChecklist, item => Assert.True(Enum.IsDefined(item.Kind)));
        Assert.All(DocumentChecklists.DefaultChecklist, item => Assert.False(string.IsNullOrEmpty(item.Description)));
    }

    [Fact]
    public void No_stored_checklist_means_the_default() =>
        Assert.Equal(DocumentChecklists.DefaultChecklist, DocumentChecklists.Resolve(null));

    [Fact]
    public void An_empty_stored_checklist_is_a_choice_not_the_default() =>
        Assert.Empty(DocumentChecklists.Resolve(DocumentChecklists.Build([], Ids.Firm, Ids.Alice)));

    [Fact]
    public void A_checklist_save_is_parsed_whole_and_trimmed() =>
        Assert.Equal(
            [new ChecklistItem("Lease", DocumentKind.Other), new ChecklistItem("Pay stubs", DocumentKind.PayStub, "Six months")],
            DocumentChecklists.ParseUpdate(Body.Of("""
                {"items": [{"title": "  Lease  ", "kind": "other", "description": " "},
                           {"title": "Pay stubs", "kind": "pay_stub", "description": "Six months"}]}
                """)));

    [Fact]
    public void Every_bad_checklist_entry_is_reported_by_index() =>
        Assert.Equal(
            ["items.0.title", "items.1.kind", "items.2"],
            Assert.Throws<FieldValidationException>(() => DocumentChecklists.ParseUpdate(Body.Of("""
                {"items": [{"title": "", "kind": "pay_stub"}, {"title": "Car", "kind": "vehicle"}, "not an object"]}
                """))).Fields.Keys.Order(StringComparer.Ordinal));

    [Fact]
    public void Two_entries_with_the_same_title_are_refused() =>
        Assert.Equal(
            ["items.1.title"],
            Assert.Throws<FieldValidationException>(() => DocumentChecklists.ParseUpdate(Body.Of("""
                {"items": [{"title": "Bank statements", "kind": "bank_statement"}, {"title": "bank  STATEMENTS", "kind": "other"}]}
                """))).Fields.Keys);

    [Fact]
    public void A_checklist_has_a_ceiling()
    {
        var entries = string.Join(", ", Enumerable.Range(0, 41).Select(n => $$"""{"title": "Doc {{n}}", "kind": "other"}"""));
        Assert.Throws<FieldValidationException>(() => DocumentChecklists.ParseUpdate(Body.Of($$"""{"items": [{{entries}}]}""")));
    }

    [Fact]
    public void A_request_is_parsed_like_a_checklist_entry()
    {
        var errors = new Dictionary<string, string>();
        Assert.Equal(new ChecklistItem("Lease", DocumentKind.Other), DocumentChecklists.ParseItem(Body.Of("""{"title": "Lease", "kind": "other"}"""), "request", errors));
        Assert.Null(DocumentChecklists.ParseItem(Body.Of("""{"kind": "nope"}"""), "request", errors));
        Assert.Equal(["request.kind", "request.title"], errors.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Every_document_kind_keeps_its_python_wire_value() =>
        Assert.Equal(["credit_report", "pay_stub", "bank_statement", "tax_return", "identification", "court_notice", "other"], Wire.Names<DocumentKind>());
}
