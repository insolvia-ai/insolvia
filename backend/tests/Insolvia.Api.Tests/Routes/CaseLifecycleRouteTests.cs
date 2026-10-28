using System.Text.Json.Nodes;
using Insolvia.Api.Tests.Support;
using Insolvia.Core;
using Insolvia.Core.AccessLog;
using Insolvia.Core.Memory;
using static Insolvia.Api.Tests.Support.CaseWorld;

namespace Insolvia.Api.Tests.Routes;

/// <summary>
/// test_case_lifecycle_routes.py (issue 14.3 / #355) and ADR 0027's wire: the
/// status lifecycle on the case PATCH, the history (<c>…/lifecycle</c> and the
/// kept <c>…/status-history</c>), the retained transition, archive, soft
/// delete, Copy case, and a conversion. The funnel tests that need no case
/// (stage moves, clearing, another firm's client, the whole-record PUT
/// keeping the stage) were ported with the client routes in 18.14.
/// </summary>
public sealed class CaseLifecycleRouteTests : IAsyncLifetime, IDisposable
{
    private const string Filing = """ "status": "filed", "filed_at": "2026-09-01", "case_number": "8:26-bk-01234" """;

    private readonly CaseWorld _world = new();

    public async ValueTask InitializeAsync() => await _world.SeedAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _world.Dispose();

    // ── The status lifecycle ────────────────────────────────────────

    [Fact]
    public async Task A_case_moves_from_intake_to_filed_with_its_docket_facts_and_the_history_records_each_move()
    {
        var caseId = await _world.OpenCaseIdAsync();
        Assert.Equal(200, (await _world.Patch($"/v1/cases/{caseId}", """{"status": "ready_to_file"}""")).Status);

        var filed = await _world.Patch($"/v1/cases/{caseId}", "{" + Filing + """, "meeting_341_at": "2026-10-05", "judge": "Hon. Example Judge", "trustee": "Example Trustee", "office_file_number": "F-1001"}""");

        Assert.Equal(200, filed.Status);
        Assert.Equal("filed", filed.Str("status"));
        Assert.Equal(("8:26-bk-01234", "Hon. Example Judge", "Example Trustee", "F-1001"), (filed.Str("caseNumber"), filed.Str("judge"), filed.Str("trustee"), filed.Str("officeFileNumber")));
        Assert.Equal(("2026-09-01", "2026-10-05"), (filed.Str("filedAt"), filed.Str("meeting341At")));

        var history = (await _world.Get($"/v1/cases/{caseId}/status-history")).Object["history"]!.AsArray();
        Assert.Equal([("intake", "ready_to_file"), ("ready_to_file", "filed")], history.Select(h => (h!.AsObject().Str("fromStatus"), h.AsObject().Str("toStatus"))));
        Assert.All(history, h => Assert.Equal(Alice, h!.AsObject().Str("changedBy")));
        Assert.Equal(["changedAt", "changedBy", "fromStatus", "toStatus"], Answer.Keys(history[0]));
    }

    [Fact]
    public async Task Filing_without_the_case_number_is_refused_naming_it()
    {
        var caseId = await _world.OpenCaseIdAsync();

        var answer = await _world.Patch($"/v1/cases/{caseId}", """{"status": "filed", "filed_at": "2026-09-01"}""");

        Assert.Equal(400, answer.Status);
        Assert.Equal(["case_number"], answer.FieldErrors);
    }

    [Fact]
    public async Task A_filed_case_cannot_be_moved_back_into_preparation()
    {
        var caseId = await _world.OpenCaseIdAsync();
        await _world.Patch($"/v1/cases/{caseId}", "{" + Filing + "}");

        Assert.Equal(409, (await _world.Patch($"/v1/cases/{caseId}", """{"status": "intake"}""")).Status);
    }

    [Fact]
    public async Task The_history_is_as_private_as_its_case()
    {
        var caseId = await _world.OpenCaseIdAsync();

        Assert.Equal(404, (await _world.Get($"/v1/cases/{caseId}/status-history", Carol)).Status);
        Assert.Equal(404, (await _world.Get($"/v1/cases/{caseId}/lifecycle", Carol)).Status);
        Assert.Equal(404, (await _world.Get($"/v1/cases/{caseId}/lifecycle", Bob)).Status);
    }

    // ── ADR 0027: the lifecycle history ─────────────────────────────

    [Fact]
    public async Task The_lifecycle_lists_every_event_oldest_first_with_the_court_dates()
    {
        var caseId = await _world.OpenCaseIdAsync(chapter: 13);
        await _world.Patch($"/v1/cases/{caseId}", """{"status": "ready_to_file"}""");
        await _world.Patch($"/v1/cases/{caseId}", "{" + Filing + "}");
        Assert.Equal(200, (await _world.Patch($"/v1/cases/{caseId}", """{"status": "dismissed", "effective_on": "2026-11-20"}""")).Status);

        var answer = await _world.Get($"/v1/cases/{caseId}/lifecycle");

        Assert.Equal(200, answer.Status);
        var events = answer.Object["events"]!.AsArray().Select(e => e!.AsObject()).ToList();
        Assert.Equal(["status", "status", "status"], events.Select(e => e.Str("kind")));
        Assert.Null(events[0]["effectiveOn"]);
        Assert.True(events[0].ContainsKey("effectiveOn"));
        Assert.Equal(["effectiveOn", "fromStatus", "kind", "provenance", "recordedAt", "recordedBy", "toStatus"], Answer.Keys(events[0]));
        Assert.Equal(("2026-09-01", 13), (events[1].Str("effectiveOn"), events[1]["chapter"]!.GetValue<int>()));
        Assert.Equal(("dismissed", "2026-11-20", "manual", Alice), (events[2].Str("toStatus"), events[2].Str("effectiveOn"), events[2].Str("provenance"), events[2].Str("recordedBy")));
        Assert.True(string.CompareOrdinal(events[1].Str("recordedAt"), events[2].Str("recordedAt")) < 0);
    }

    [Fact]
    public async Task An_effective_date_needs_a_status_move_and_cannot_precede_the_petition()
    {
        var caseId = await _world.OpenCaseIdAsync();

        Assert.Equal(["effective_on"], (await _world.Patch($"/v1/cases/{caseId}", """{"judge": "X", "effective_on": "2026-09-01"}""")).FieldErrors);
        Assert.Equal(["effective_on"], (await _world.Patch($"/v1/cases/{caseId}", """{"status": "ready_to_file", "effective_on": "2026-09-01"}""")).FieldErrors);

        await _world.Patch($"/v1/cases/{caseId}", "{" + Filing + "}");
        Assert.Equal(["effective_on"], (await _world.Patch($"/v1/cases/{caseId}", """{"status": "dismissed", "effective_on": "2026-08-01"}""")).FieldErrors);
        Assert.Equal("filed", (await _world.StoredAsync(caseId))!.Status.ToWire());
    }

    [Fact]
    public async Task An_edit_is_not_an_event()
    {
        var caseId = await _world.OpenCaseIdAsync();
        await _world.Patch($"/v1/cases/{caseId}", """{"court": "flmb", "division": "orlando"}""");
        await _world.Patch($"/v1/cases/{caseId}", """{"status": "intake"}""");

        Assert.Equal("""{"events":[]}""", (await _world.Get($"/v1/cases/{caseId}/lifecycle")).Body);
    }

    [Fact]
    public async Task A_filed_cases_chapter_changes_only_by_a_conversion()
    {
        var caseId = await _world.OpenCaseIdAsync();
        Assert.Equal(200, (await _world.Patch($"/v1/cases/{caseId}", """{"chapter": 13}""")).Status);
        await _world.Patch($"/v1/cases/{caseId}", "{" + Filing + "}");

        Assert.Equal(409, (await _world.Patch($"/v1/cases/{caseId}", """{"chapter": 7}""")).Status);
        Assert.Equal(200, (await _world.Patch($"/v1/cases/{caseId}", """{"chapter": 13, "judge": "Hon. Example"}""")).Status);
    }

    [Fact]
    public async Task The_petition_date_of_a_filed_case_stays_correctable()
    {
        var caseId = await _world.OpenCaseIdAsync();
        await _world.Patch($"/v1/cases/{caseId}", "{" + Filing + "}");

        var corrected = await _world.Patch($"/v1/cases/{caseId}", """{"filed_at": "2026-09-02"}""");

        Assert.Equal(200, corrected.Status);
        Assert.Equal("2026-09-02", corrected.Str("filedAt"));
        Assert.Equal(400, (await _world.Patch($"/v1/cases/{caseId}", """{"filed_at": null}""")).Status);
    }

    // ── ADR 0027: conversions ───────────────────────────────────────

    [Fact]
    public async Task A_conversion_changes_the_chapter_keeps_the_number_and_petition_date_and_is_an_event()
    {
        var caseId = await _world.OpenCaseIdAsync(chapter: 13);
        await _world.Patch($"/v1/cases/{caseId}", "{" + Filing + "}");

        var answer = await _world.Post($"/v1/cases/{caseId}/conversions", """{"toChapter": 7, "effectiveOn": "2026-12-01"}""");

        Assert.Equal(201, answer.Status);
        var converted = answer.Object["case"]!.AsObject();
        Assert.Equal((7, "filed", "8:26-bk-01234", "2026-09-01"), (converted["chapter"]!.GetValue<int>(), converted.Str("status"), converted.Str("caseNumber"), converted.Str("filedAt")));
        var recorded = answer.Object["event"]!.AsObject();
        Assert.Equal(("conversion", 13, 7, "2026-12-01"), (recorded.Str("kind"), recorded["fromChapter"]!.GetValue<int>(), recorded["toChapter"]!.GetValue<int>(), recorded.Str("effectiveOn")));
        Assert.Equal(7, (await _world.StoredAsync(caseId))!.Chapter);
        var events = (await _world.Get($"/v1/cases/{caseId}/lifecycle")).Object["events"]!.AsArray();
        Assert.Equal(["status", "conversion"], events.Select(e => e!.AsObject().Str("kind")));

        // Status history shows status moves only.
        Assert.Single((await _world.Get($"/v1/cases/{caseId}/status-history")).Object["history"]!.AsArray());
        Assert.Equal((AccessAction.CaseUpdate, AccessOutcome.Allowed), (_world.Events[^1].Action, _world.Events[^1].Outcome));
    }

    [Fact]
    public async Task A_conversion_into_11_is_recorded_not_refused_and_repeats_are_allowed()
    {
        var caseId = await _world.OpenCaseIdAsync(chapter: 7);
        await _world.Patch($"/v1/cases/{caseId}", "{" + Filing + "}");

        Assert.Equal(201, (await _world.Post($"/v1/cases/{caseId}/conversions", """{"toChapter": 13, "effectiveOn": "2026-10-01"}""")).Status);
        Assert.Equal(201, (await _world.Post($"/v1/cases/{caseId}/conversions", """{"toChapter": 11, "effectiveOn": "2026-10-02"}""")).Status);
        Assert.Equal(11, (await _world.StoredAsync(caseId))!.Chapter);
    }

    [Theory]
    [InlineData("""{"toChapter": 7, "effectiveOn": "2026-12-01"}""", "to_chapter")]
    [InlineData("""{"toChapter": 9, "effectiveOn": "2026-12-01"}""", "to_chapter")]
    [InlineData("""{"toChapter": "13", "effectiveOn": "2026-12-01"}""", "to_chapter")]
    [InlineData("""{"toChapter": 13}""", "effective_on")]
    [InlineData("""{"toChapter": 13, "effectiveOn": "12/01/2026"}""", "effective_on")]
    [InlineData("""{"toChapter": 13, "effectiveOn": "2026-08-31"}""", "effective_on")]
    public async Task A_conversion_is_checked_naming_the_field(string body, string field)
    {
        var caseId = await _world.OpenCaseIdAsync(chapter: 7);
        await _world.Patch($"/v1/cases/{caseId}", "{" + Filing + "}");

        var answer = await _world.Post($"/v1/cases/{caseId}/conversions", body);

        Assert.Equal(400, answer.Status);
        Assert.Equal([field], answer.FieldErrors);
        Assert.Equal(7, (await _world.StoredAsync(caseId))!.Chapter);
    }

    [Fact]
    public async Task Only_a_filed_case_converts_and_only_for_one_who_may_see_and_edit_it()
    {
        var caseId = await _world.OpenCaseIdAsync();
        const string body = """{"toChapter": 13, "effectiveOn": "2026-12-01"}""";

        Assert.Equal(409, (await _world.Post($"/v1/cases/{caseId}/conversions", body)).Status);
        await _world.Patch($"/v1/cases/{caseId}", "{" + Filing + "}");
        await _world.Patch($"/v1/cases/{caseId}", """{"status": "dismissed", "effective_on": "2026-11-01"}""");
        Assert.Equal(409, (await _world.Post($"/v1/cases/{caseId}/conversions", body)).Status);
        Assert.Equal(404, (await _world.Post($"/v1/cases/{caseId}/conversions", body, Carol)).Status);
        Assert.Equal((AccessAction.CaseUpdate, AccessOutcome.Denied), (_world.Events[^1].Action, _world.Events[^1].Outcome));
        Assert.Equal(404, (await _world.Post($"/v1/cases/{caseId}/conversions", body, Bob)).Status);
    }

    // ── The retained transition (ADR 0022, #355) ────────────────────

    [Fact]
    public async Task A_case_never_opens_as_a_prospect()
    {
        var clientId = await _world.AddClientAsync();
        var answer = await _world.Post("/v1/cases", $$"""{ {{Tampa}}, "status": "prospect", "prospect_stage": "possible", "client_ids": ["{{clientId}}"]}""");

        Assert.Equal("intake", answer.Str("status"));
        Assert.False(answer.Object.ContainsKey("prospectStage"));
    }

    [Fact]
    public async Task Opening_a_case_retains_the_prospect_and_clears_the_stage()
    {
        var clientId = await _world.AddClientAsync();
        foreach (var value in new[] { "possible", "consultation_scheduled", "awaiting_signed_agreement" })
        {
            Assert.Equal(value, (await Stage(clientId, value)).Str("prospect_stage"));
        }

        await _world.Post("/v1/cases", $$"""{ {{Tampa}}, "client_ids": ["{{clientId}}"]}""");

        var retained = (await _world.Get($"/v1/firm/clients/{clientId}")).Object;
        Assert.Equal(Http.FlaskJson.Date(Clock.Today()), retained.Str("first_retained_at"));
        Assert.False(retained.ContainsKey("prospect_stage"));
        Assert.Null((await _world.Firms.GetClientAsync(Ids.FirmA, clientId, HttpAssert.Ct))!.ProspectStage);
        Assert.Contains(_world.Events, e => (e.Action, e.ClientId) == (AccessAction.ClientUpdate, clientId));
    }

    [Fact]
    public async Task A_retained_client_takes_no_stage()
    {
        var clientId = await _world.AddClientAsync();
        await _world.Post("/v1/cases", $$"""{ {{Tampa}}, "client_ids": ["{{clientId}}"]}""");

        Assert.Equal(409, (await Stage(clientId, "possible")).Status);
    }

    [Fact]
    public async Task An_earlier_retained_date_is_never_overwritten()
    {
        var clientId = await _world.AddClientAsync(extra: """ "name": {"given": "Jordan", "surname": "Example"}, "first_retained_at": "2020-01-02" """);
        await _world.Post("/v1/cases", $$"""{ {{Tampa}}, "client_ids": ["{{clientId}}"]}""");

        Assert.Equal("2020-01-02", (await _world.Get($"/v1/firm/clients/{clientId}")).Str("first_retained_at"));
    }

    [Fact]
    public async Task A_copy_retains_its_clients_too()
    {
        var clientId = await _world.AddClientAsync();
        var source = (await _world.Post("/v1/cases", $$"""{ {{Tampa}}, "client_ids": ["{{clientId}}"]}""")).Str("id");
        var stored = (await _world.Firms.GetClientAsync(Ids.FirmA, clientId, HttpAssert.Ct))!;
        await _world.Firms.UpdateClientAsync(stored with { FirstRetainedAt = null }, HttpAssert.Ct);

        Assert.Equal(201, (await _world.Post($"/v1/cases/{source}/copy", null)).Status);

        Assert.Equal(Http.FlaskJson.Date(Clock.Today()), (await _world.Get($"/v1/firm/clients/{clientId}")).Str("first_retained_at"));
    }

    // ── Archive ─────────────────────────────────────────────────────

    [Fact]
    public async Task An_archived_case_leaves_the_default_list_but_not_the_firms_records()
    {
        var archivedId = await _world.OpenCaseIdAsync();
        var kept = await _world.OpenCaseIdAsync();

        var answer = await _world.Put($"/v1/cases/{archivedId}/archived", """{"archived": true}""");

        Assert.Equal(200, answer.Status);
        Assert.Equal(Alice, answer.Str("archivedBy"));
        Assert.Equal([kept], await ListedAsync());
        Assert.Equal([archivedId], await ListedAsync("?archived=true"));
        Assert.Equal(200, (await _world.Get($"/v1/cases/{archivedId}")).Status);
        Assert.Equal(AccessAction.CaseUpdate, _world.Events[^2].Action);
    }

    [Fact]
    public async Task A_restored_case_returns_to_the_working_list_and_archiving_twice_keeps_the_first_stamp()
    {
        var caseId = await _world.OpenCaseIdAsync();
        var url = $"/v1/cases/{caseId}/archived";
        var first = await _world.Put(url, """{"archived": true}""");
        Assert.Equal(first.Str("archivedAt"), (await _world.Put(url, """{"archived": true}""")).Str("archivedAt"));

        var restored = await _world.Put(url, """{"archived": false}""");

        Assert.False(restored.Object.ContainsKey("archivedAt"));
        Assert.Equal([caseId], await ListedAsync());
        Assert.Equal(["archived"], (await _world.Put(url, """{"archived": "yes"}""")).FieldErrors);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("TRUE")]
    public async Task An_unknown_archive_view_is_refused(string value)
    {
        Assert.Equal(400, (await _world.Get($"/v1/cases?archived={value}")).Status);
    }

    [Fact]
    public async Task Archiving_another_firms_case_is_404()
    {
        var caseId = await _world.OpenCaseIdAsync();

        Assert.Equal(404, (await _world.Put($"/v1/cases/{caseId}/archived", """{"archived": true}""", Carol)).Status);
    }

    // ── Delete ──────────────────────────────────────────────────────

    [Fact]
    public async Task An_admin_deletes_an_unfiled_case_and_it_is_gone_from_every_read()
    {
        var clientId = await _world.AddClientAsync();
        var caseId = (await _world.Post("/v1/cases", $$"""{ {{Tampa}}, "client_ids": ["{{clientId}}"]}""")).Str("id");

        var answer = await _world.Delete($"/v1/cases/{caseId}");

        Assert.Equal(204, answer.Status);
        Assert.Equal("", answer.Body);
        Assert.Equal(AccessAction.CaseDelete, _world.Events[^1].Action);
        foreach (var path in new[] { $"/v1/cases/{caseId}", $"/v1/cases/{caseId}/debtors", $"/v1/cases/{caseId}/status-history", $"/v1/cases/{caseId}/lifecycle" })
        {
            Assert.Equal(404, (await _world.Get(path)).Status);
        }

        Assert.DoesNotContain(caseId, await ListedAsync());
        Assert.DoesNotContain(caseId, await ListedAsync("?archived=true"));
        Assert.Equal("""{"cases":[]}""", (await _world.Get($"/v1/firm/clients/{clientId}/cases")).Body);
    }

    [Fact]
    public async Task Deleting_is_a_firm_admins_act()
    {
        var caseId = await _world.OpenCaseIdAsync(Dana);

        var answer = await _world.Delete($"/v1/cases/{caseId}", Dana);

        Assert.Equal(403, answer.Status);
        Assert.Equal("""{"error":"ForbiddenError","message":"only a firm admin may delete a case"}""", answer.Body);
        Assert.False((await _world.StoredAsync(caseId))!.Deleted);
    }

    [Fact]
    public async Task A_filed_case_cannot_be_deleted()
    {
        var caseId = await _world.OpenCaseIdAsync();
        await _world.Patch($"/v1/cases/{caseId}", "{" + Filing + "}");

        Assert.Equal(409, (await _world.Delete($"/v1/cases/{caseId}")).Status);
    }

    [Fact]
    public async Task Deleting_another_firms_case_is_404_and_logged_denied()
    {
        var caseId = await _world.OpenCaseIdAsync();

        Assert.Equal(404, (await _world.Delete($"/v1/cases/{caseId}", Carol)).Status);
        Assert.Equal((AccessAction.CaseDelete, AccessOutcome.Denied), (_world.Events[^1].Action, _world.Events[^1].Outcome));
    }

    // ── Copy case ───────────────────────────────────────────────────

    [Fact]
    public async Task A_copy_is_a_new_case_whose_values_name_their_source()
    {
        var clientId = await _world.AddClientAsync();
        var source = (await _world.Post("/v1/cases", $$"""{ {{Tampa}}, "client_ids": ["{{clientId}}"]}""")).Str("id");
        var creditor = await _world.Post($"/v1/cases/{source}/creditors", """{"name": "Example Bank", "provenance": {"name": {"source": "staff_typed"}}}""");
        await _world.Patch($"/v1/cases/{source}", "{" + Filing + """, "judge": "Hon. Example"}""");

        var answer = await _world.Post($"/v1/cases/{source}/copy", null);

        Assert.Equal(201, answer.Status);
        var copy = answer.Object;
        Assert.NotEqual(source, copy.Str("id"));
        Assert.Equal("intake", copy.Str("status"));
        Assert.False(copy.ContainsKey("caseNumber"));
        Assert.False(copy.ContainsKey("judge"));
        var debtor = Assert.Single((await _world.Get($"/v1/cases/{copy.Str("id")}/debtors")).Object["debtors"]!.AsArray())!.AsObject();
        Assert.Equal(clientId, debtor.Str("client_id"));
        Assert.All(debtor["provenance"]!.AsObject(), entry => Assert.Equal(source, entry.Value!["copied_from_case_id"]!.GetValue<string>()));
        var copied = Assert.Single((await _world.Get($"/v1/cases/{copy.Str("id")}/creditors")).Object["creditors"]!.AsArray())!.AsObject();
        Assert.Equal(creditor.Str("id"), copied.Str("id"));
        Assert.Equal($$"""{"copied_from_case_id":"{{source}}","source":"staff_typed"}""", Http.FlaskJson.Serialize(copied["provenance"]!["name"]));
        var forClient = (await _world.Get($"/v1/firm/clients/{clientId}/cases")).Object["cases"]!.AsArray();
        Assert.Equal(new[] { source, copy.Str("id") }.Order(StringComparer.Ordinal), forClient.Select(e => e!["case"]!.AsObject().Str("id")).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_copy_of_a_case_whose_client_is_archived_is_refused()
    {
        var clientId = await _world.AddClientAsync();
        var source = (await _world.Post("/v1/cases", $$"""{ {{Tampa}}, "client_ids": ["{{clientId}}"]}""")).Str("id");
        await _world.Put($"/v1/firm/clients/{clientId}/status", """{"status": "archived"}""");

        Assert.Equal(409, (await _world.Post($"/v1/cases/{source}/copy", null)).Status);
    }

    // As in CaseRouteTests: the claim lands before the request; the
    // transaction's own refusal is the store conformance suite's.
    [Fact]
    public async Task A_copy_whose_client_is_being_merged_is_refused_and_writes_no_case()
    {
        var clientId = await _world.AddClientAsync();
        var source = (await _world.Post("/v1/cases", $$"""{ {{Tampa}}, "client_ids": ["{{clientId}}"]}""")).Str("id");
        var survivor = await _world.AddClientAsync(extra: """ "name": {"given": "Jordan B."} """);
        Assert.True(await _world.Firms.ClaimClientMergeAsync(Ids.FirmA, clientId, survivor, HttpAssert.Ct));

        var answer = await _world.Post($"/v1/cases/{source}/copy", null);

        Assert.Equal(409, answer.Status);
        Assert.Equal("That client is being merged into another client.", answer.Str("message"));
        Assert.Equal([source], (await _world.FirmCasesAsync()).Select(found => found.Id));
    }

    [Fact]
    public async Task Copying_a_case_the_caller_cannot_see_is_404()
    {
        var source = await _world.OpenCaseIdAsync();

        Assert.Equal(404, (await _world.Post($"/v1/cases/{source}/copy", null, Bob)).Status);
    }

    [Fact]
    public async Task A_copy_carries_the_sealed_tax_id_without_opening_it()
    {
        var source = await _world.OpenCaseIdAsync();
        var stored = Assert.Single((await _world.Get($"/v1/cases/{source}/debtors")).Object["debtors"]!.AsArray())!.AsObject();
        var provenance = stored["provenance"]!.AsObject().DeepClone().AsObject();
        provenance["tax_id"] = new JsonObject { ["source"] = "staff_typed" };
        var body = new JsonObject { ["name"] = stored["name"]!.DeepClone(), ["tax_id"] = new JsonObject { ["kind"] = "ssn", ["value"] = "987-65-4321" }, ["provenance"] = provenance };
        var saved = await _world.Put($"/v1/cases/{source}/debtors/debtor_1", body.ToJsonString());
        Assert.True(saved.Status == 200, saved.Body);

        var copyId = (await _world.Post($"/v1/cases/{source}/copy", null)).Str("id");

        var copied = Assert.Single((await _world.Get($"/v1/cases/{copyId}/debtors")).Object["debtors"]!.AsArray())!.AsObject();
        Assert.Equal("""{"kind":"ssn","last_four":"4321"}""", Http.FlaskJson.Serialize(copied["tax_id"]));
        var items = _world.Service<Core.Ports.ITaxIdStore>() as MemoryTaxIdStore;
        Assert.Equal(new[] { source, copyId }.Order(StringComparer.Ordinal), items!.Items.Keys.Select(key => key.CaseId).Order(StringComparer.Ordinal));
    }

    private Task<Answer> Stage(string clientId, string? value) =>
        _world.Put($"/v1/firm/clients/{clientId}/prospect-stage", value is null ? """{"prospect_stage": null}""" : $$"""{"prospect_stage": "{{value}}"}""");

    private async Task<List<string?>> ListedAsync(string query = "")
    {
        var answer = await _world.Get($"/v1/cases{query}");
        Assert.Equal(200, answer.Status);
        return [.. answer.Object["cases"]!.AsArray().Select(found => found!.AsObject().Str("id"))];
    }
}
