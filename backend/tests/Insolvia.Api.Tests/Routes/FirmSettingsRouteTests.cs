using System.Text.Json.Nodes;
using Insolvia.Api.Tests.Support;
using Insolvia.Core.DocumentRequests;
using Insolvia.Core.Firms;
using Insolvia.Core.Questionnaires;

namespace Insolvia.Api.Tests.Routes;

/// <summary>
/// The staff halves of test_portal_routes.py's questionnaire section and
/// test_portal_documents.py's "the firm's checklist" section: the two firm
/// settings under <c>/v1/firm</c>. What a firm's CLIENT reads of them is the
/// portal's (18.18), so the client-side assertions are not here.
/// </summary>
public sealed class FirmSettingsRouteTests : IAsyncLifetime, IDisposable
{
    private const string Carol = Ids.Sam;

    private static readonly string[] Sections = ["personal_information", "property", "debts", "income", "expenses", "other"];

    private readonly FirmWorld _world = new();

    public static TheoryData<string, string?> QuestionnaireMethods => new()
    {
        { "GET", null },
        { "PUT", Save() },
        { "DELETE", null },
    };

    public static TheoryData<string> ChecklistMethods => ["GET", "PUT", "DELETE"];

    public async ValueTask InitializeAsync()
    {
        await (await _world.WithFirmsAsync()).WithAsync(
            FirmWorld.Member(Ids.Alice, isAdmin: true),
            FirmWorld.Member(Ids.Bob, role: FirmRole.Paralegal),
            FirmWorld.Member(Carol, Ids.FirmB, isAdmin: true));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _world.Dispose();

    // ── The questionnaire (ADR 0023 PR 3 / #362) ─────────────────────

    [Fact]
    public async Task A_firm_that_saved_nothing_reads_every_section_as_the_default()
    {
        var body = (await _world.Get("/v1/firm/questionnaire", Ids.Alice)).Object;

        Assert.True(body["isDefault"]!.GetValue<bool>());
        Assert.Null(body["updatedAt"]);
        Assert.Null(body["updatedBy"]);
        Assert.Equal(Sections, body["sections"]!.AsArray().Select(s => s!["id"]!.GetValue<string>()));
        Assert.Equal(
            ["covers", "defaultInstructions", "enabled", "id", "instructions", "switchable", "title"],
            Answer.Keys(body["sections"]![0]));
        Assert.Null(body["sections"]![0]!["instructions"]);
    }

    [Fact]
    public async Task A_section_switched_off_is_kept_for_staff()
    {
        var saved = await _world.Put("/v1/firm/questionnaire", Save(("expenses", """ "enabled": false """)), Ids.Alice);
        Assert.Equal(200, saved.Status);

        var staffView = (await _world.Get("/v1/firm/questionnaire", Ids.Alice)).Object;

        Assert.Equal(Sections, staffView["sections"]!.AsArray().Select(s => s!["id"]!.GetValue<string>()));
        Assert.Equal([true, true, true, true, false, true], staffView["sections"]!.AsArray().Select(s => s!["enabled"]!.GetValue<bool>()));
        Assert.False(staffView["isDefault"]!.GetValue<bool>());
        Assert.Equal(Ids.Alice, staffView["updatedBy"]!.GetValue<string>());
        Assert.Matches("^\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}\\.\\d{6}Z$", staffView["updatedAt"]!.GetValue<string>());
        Assert.Equal(saved.Body, (await _world.Get("/v1/firm/questionnaire", Ids.Alice)).Body);
    }

    [Fact]
    public async Task The_firms_instructions_are_saved_beside_the_default()
    {
        await _world.Put("/v1/firm/questionnaire", Save(("debts", """ "instructions": "Bring your statements." """)), Ids.Alice);

        var debts = (await _world.Get("/v1/firm/questionnaire", Ids.Alice)).Object["sections"]![2]!;

        Assert.Equal("debts", debts["id"]!.GetValue<string>());
        Assert.Equal("Bring your statements.", debts["instructions"]!.GetValue<string>());
        Assert.Equal(Questionnaire.Catalogue[2].DefaultInstructions, debts["defaultInstructions"]!.GetValue<string>());
    }

    [Fact]
    public async Task Reset_to_defaults_deletes_the_config()
    {
        await _world.Put("/v1/firm/questionnaire", Save(("other", """ "enabled": false """)), Ids.Alice);

        var reset = await _world.Delete("/v1/firm/questionnaire", Ids.Alice);

        Assert.Equal(200, reset.Status);
        Assert.True(reset.Object["isDefault"]!.GetValue<bool>());
        Assert.Null(await _world.Firms.GetQuestionnaireAsync(Ids.FirmA, HttpAssert.Ct));
    }

    [Fact]
    public async Task Resetting_a_firm_already_on_the_defaults_is_not_an_error()
    {
        var answer = await _world.Delete("/v1/firm/questionnaire", Ids.Alice);

        Assert.Equal(200, answer.Status);
        Assert.True(answer.Object["isDefault"]!.GetValue<bool>());
        Assert.Equal((await _world.Get("/v1/firm/questionnaire", Ids.Alice)).Body, answer.Body);
    }

    [Fact]
    public async Task Another_firms_config_does_not_reach_this_firm()
    {
        await _world.Put("/v1/firm/questionnaire", Save(("property", """ "enabled": false """)), Carol);

        Assert.True((await _world.Get("/v1/firm/questionnaire", Ids.Alice)).Object["isDefault"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Personal_information_cannot_be_switched_off_through_the_api()
    {
        var answer = await _world.Put("/v1/firm/questionnaire", Save(("personal_information", """ "enabled": false """)), Ids.Alice);

        Assert.Equal(400, answer.Status);
        Assert.Equal("""{"error":"ValidationError","fields":{"sections.personal_information.enabled":"Personal information is always on."}}""", answer.Body);
    }

    [Theory]
    [MemberData(nameof(QuestionnaireMethods))]
    public async Task The_questionnaire_config_is_a_firm_administration_setting(string method, string? body)
    {
        // Bob is a paralegal on the role defaults: firm_administration hidden.
        var answer = await _world.SendAsync(new HttpMethod(method), "/v1/firm/questionnaire", Ids.Bob, body);

        Assert.Equal(403, answer.Status);
    }

    // ── The document checklist (ADR 0023 PR 5 / #364) ───────────────

    [Fact]
    public async Task A_firm_that_saved_nothing_reads_the_default_checklist()
    {
        var body = (await _world.Get("/v1/firm/document-checklist", Ids.Alice)).Object;

        Assert.True(body["isDefault"]!.GetValue<bool>());
        Assert.Equal(DocumentChecklists.DefaultChecklist.Select(item => item.Title), body["items"]!.AsArray().Select(i => i!["title"]!.GetValue<string>()));
        Assert.True(JsonNode.DeepEquals(body["defaultItems"], body["items"]));
        Assert.Equal(["description", "kind", "title"], Answer.Keys(body["items"]![0]));
    }

    [Fact]
    public async Task A_saved_checklist_is_answered_as_stored_with_explicit_null_descriptions()
    {
        var saved = await _world.Put("/v1/firm/document-checklist", """{"items": [{"title": "Lease", "kind": "other"}]}""", Ids.Alice);

        Assert.Equal(200, saved.Status);
        Assert.Equal("""[{"description":null,"kind":"other","title":"Lease"}]""", saved.Object["items"]!.ToJsonString());
        Assert.False(saved.Object["isDefault"]!.GetValue<bool>());
        Assert.Equal(Ids.Alice, saved.Str("updatedBy"));
        Assert.Equal(saved.Body, (await _world.Get("/v1/firm/document-checklist", Ids.Alice)).Body);
    }

    [Fact]
    public async Task Reset_returns_the_firm_to_the_default_checklist()
    {
        await _world.Put("/v1/firm/document-checklist", """{"items": []}""", Ids.Alice);
        Assert.NotNull(await _world.Firms.GetDocumentChecklistAsync(Ids.FirmA, HttpAssert.Ct));

        var reset = await _world.Delete("/v1/firm/document-checklist", Ids.Alice);

        Assert.Equal(200, reset.Status);
        Assert.True(reset.Object["isDefault"]!.GetValue<bool>());
        Assert.Null(await _world.Firms.GetDocumentChecklistAsync(Ids.FirmA, HttpAssert.Ct));
    }

    [Fact]
    public async Task Another_firms_checklist_does_not_reach_this_one()
    {
        await _world.Put("/v1/firm/document-checklist", """{"items": [{"title": "Lease", "kind": "other"}]}""", Carol);

        Assert.True((await _world.Get("/v1/firm/document-checklist", Ids.Alice)).Object["isDefault"]!.GetValue<bool>());
    }

    [Theory]
    [MemberData(nameof(ChecklistMethods))]
    public async Task The_checklist_is_a_firm_administration_setting(string method)
    {
        var answer = await _world.SendAsync(new HttpMethod(method), "/v1/firm/document-checklist", Ids.Bob, """{"items": []}""");

        Assert.Equal(403, answer.Status);
    }

    [Fact]
    public async Task A_bad_checklist_entry_is_a_400_keyed_to_the_entry()
    {
        var answer = await _world.Put("/v1/firm/document-checklist", """{"items": [{"title": "Lease", "kind": "spaceship"}]}""", Ids.Alice);

        Assert.Equal(400, answer.Status);
        Assert.Contains(answer.FieldErrors, field => field.StartsWith("items.0.", StringComparison.Ordinal));
    }

    // test_portal_routes.py's questionnaire_save: every section enabled, then
    // each override's keys laid over its section (a dict merge, not a second key).
    private static string Save(params (string Section, string Fields)[] overrides)
    {
        var sections = new JsonArray();
        foreach (var id in Sections)
        {
            var section = new JsonObject { ["id"] = id, ["enabled"] = true };
            foreach (var (_, fields) in overrides.Where(o => o.Section == id))
            {
                foreach (var (key, value) in JsonNode.Parse("{" + fields + "}")!.AsObject())
                {
                    section[key] = value?.DeepClone();
                }
            }

            sections.Add(section);
        }

        return new JsonObject { ["sections"] = sections }.ToJsonString();
    }
}
